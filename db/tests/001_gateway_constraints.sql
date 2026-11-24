-- Constraint tests for 001_gateway_foundation. Runs in one transaction that is rolled back.
-- Constraint triggers are switched to IMMEDIATE so they fire per statement instead of at commit;
-- each journal entry is therefore written together with its lines in a single statement.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/001_gateway_constraints.sql

BEGIN;
SET CONSTRAINTS ALL IMMEDIATE;

-- Fixtures -------------------------------------------------------------------------------------
INSERT INTO gateway.asset (asset_key, kind, code, network, scale, network_fee_asset_key, name) VALUES
    ('INR', 'FIAT', 'INR', NULL, 2, NULL, 'Indian Rupee'),
    ('USD', 'FIAT', 'USD', NULL, 2, NULL, 'US Dollar'),
    ('ETH@ethereum', 'CRYPTO', 'ETH', 'ethereum', 18, 'ETH@ethereum', 'Ether'),
    ('USDC@ethereum', 'CRYPTO', 'USDC', 'ethereum', 6, 'ETH@ethereum', 'USD Coin on Ethereum');

INSERT INTO gateway.api_client (client_id, display_name, key_sha256)
VALUES ('client-a', 'Test client', repeat('a', 64));

INSERT INTO gateway.quote (
    quote_id, client_id, source_asset_key, source_amount, service_fee, net_source_amount,
    destination_asset_key, gross_destination_amount, network_fee, destination_amount,
    market_rate, customer_rate, pivot_asset_key, pivot_amount, price_path, pricing_policy_version, created_at, expires_at)
VALUES
    ('40000000-0000-0000-0000-000000000001', 'client-a', 'INR', 10000, 150, 9850,
     'USDC@ethereum', 115.64885, 1.5, 114.14885, 0.0118, 0.011741, 'USD', 118, '[]', 'test',
     '2026-11-24T12:00:00Z', '2026-11-24T12:01:00Z'),
    ('40000000-0000-0000-0000-000000000002', 'client-a', 'INR', 10000, 150, 9850,
     'USDC@ethereum', 115.64885, 1.5, 114.14885, 0.0118, 0.011741, 'USD', 118, '[]', 'test',
     '2026-11-24T12:00:00Z', '2026-11-24T12:01:00Z');

INSERT INTO gateway.payment_order (
    order_id, client_id, customer_id, quote_id, destination_address, status, payment_deadline,
    fiat_provider, fiat_payment_id, created_at, updated_at)
VALUES ('50000000-0000-0000-0000-000000000001', 'client-a', 'cust_verified', '40000000-0000-0000-0000-000000000001',
        '0x1111111111111111111111111111111111111111', 'FIAT_RECEIVED', '2026-11-24T12:15:00Z',
        'sandbox-fiat', 'pay_1', '2026-11-24T12:00:30Z', '2026-11-24T12:01:00Z');

INSERT INTO gateway.order_event (order_id, sequence, from_status, to_status, occurred_at, reason) VALUES
    ('50000000-0000-0000-0000-000000000001', 1, NULL, 'AWAITING_FIAT_PAYMENT', '2026-11-24T12:00:30Z', 'ORDER_CREATED'),
    ('50000000-0000-0000-0000-000000000001', 2, 'AWAITING_FIAT_PAYMENT', 'FIAT_RECEIVED', '2026-11-24T12:01:00Z', 'FIAT_CAPTURED');

-- The valid path: a balanced two-asset entry is accepted.
WITH entry AS (
    INSERT INTO ledger.journal_entry (entry_id, kind, order_id, posted_at)
    VALUES ('60000000-0000-0000-0000-000000000001', 'CRYPTO_PURCHASED', '50000000-0000-0000-0000-000000000001', now())
    RETURNING entry_id
)
INSERT INTO ledger.journal_line (entry_id, line_number, account_code, asset_key, side, amount)
SELECT entry_id, line_number, account_code, asset_key, side, amount
  FROM entry,
       (VALUES (1, 'liabilities:customer_orders', 'INR', 'DEBIT', 9850::numeric),
               (2, 'liabilities:liquidity_provider:lp', 'INR', 'CREDIT', 9850),
               (3, 'assets:hot_wallet:custody', 'USDC@ethereum', 'DEBIT', 116.23),
               (4, 'liabilities:customer_orders', 'USDC@ethereum', 'CREDIT', 115.64885),
               (5, 'revenue:spread', 'USDC@ethereum', 'CREDIT', 0.58115)) AS lines (line_number, account_code, asset_key, side, amount);

-- Each block must fail with the named error; any other outcome aborts the script. ----------------
DO $$
BEGIN
    BEGIN
        WITH entry AS (
            INSERT INTO ledger.journal_entry (entry_id, kind, posted_at)
            VALUES ('60000000-0000-0000-0000-000000000002', 'TEST', now()) RETURNING entry_id)
        INSERT INTO ledger.journal_line (entry_id, line_number, account_code, asset_key, side, amount)
        SELECT entry_id, n, a, k, s, v FROM entry,
            (VALUES (1, 'assets:psp_clearing:psp', 'INR', 'DEBIT', 100::numeric),
                    (2, 'liabilities:customer_orders', 'INR', 'CREDIT', 99)) AS l (n, a, k, s, v);
        RAISE EXCEPTION 'FAILED: an unbalanced entry was accepted';
    EXCEPTION WHEN SQLSTATE 'LG002' THEN
        RAISE NOTICE 'ok: an unbalanced entry is rejected';
    END;

    BEGIN
        WITH entry AS (
            INSERT INTO ledger.journal_entry (entry_id, kind, posted_at)
            VALUES ('60000000-0000-0000-0000-000000000003', 'TEST', now()) RETURNING entry_id)
        INSERT INTO ledger.journal_line (entry_id, line_number, account_code, asset_key, side, amount)
        SELECT entry_id, n, a, k, s, v FROM entry,
            (VALUES (1, 'assets:psp_clearing:psp', 'INR', 'DEBIT', 100::numeric),
                    (2, 'liabilities:customer_orders', 'USDC@ethereum', 'CREDIT', 100)) AS l (n, a, k, s, v);
        RAISE EXCEPTION 'FAILED: an entry balanced only across currencies was accepted';
    EXCEPTION WHEN SQLSTATE 'LG002' THEN
        RAISE NOTICE 'ok: every asset must balance on its own';
    END;

    BEGIN
        INSERT INTO ledger.journal_entry (entry_id, kind, posted_at)
        VALUES ('60000000-0000-0000-0000-000000000004', 'TEST', now());
        RAISE EXCEPTION 'FAILED: an entry without lines was accepted';
    EXCEPTION WHEN SQLSTATE 'LG001' THEN
        RAISE NOTICE 'ok: an entry needs at least two lines';
    END;

    BEGIN
        INSERT INTO ledger.journal_line (entry_id, line_number, account_code, asset_key, side, amount)
        VALUES ('60000000-0000-0000-0000-000000000001', 6, 'revenue:spread', 'USDC@ethereum', 'CREDIT', 1);
        RAISE EXCEPTION 'FAILED: a line added later unbalanced an existing entry';
    EXCEPTION WHEN SQLSTATE 'LG002' THEN
        RAISE NOTICE 'ok: adding a line to a posted entry cannot unbalance it';
    END;

    BEGIN
        UPDATE ledger.journal_line SET amount = 1 WHERE entry_id = '60000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: a ledger line was edited';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: ledger lines cannot be edited';
    END;

    BEGIN
        DELETE FROM ledger.journal_entry;
        RAISE EXCEPTION 'FAILED: a ledger entry was deleted';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: ledger entries cannot be deleted';
    END;

    BEGIN
        TRUNCATE ledger.journal_line;
        RAISE EXCEPTION 'FAILED: the ledger was truncated';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: the ledger cannot be truncated';
    END;

    BEGIN
        INSERT INTO gateway.order_event (order_id, sequence, from_status, to_status, occurred_at, reason)
        VALUES ('50000000-0000-0000-0000-000000000001', 3, 'FIAT_RECEIVED', 'COMPLETED', now(), 'SKIP_AHEAD');
        RAISE EXCEPTION 'FAILED: an order skipped straight to COMPLETED';
    EXCEPTION WHEN foreign_key_violation THEN
        RAISE NOTICE 'ok: orders can only follow allowed transitions';
    END;

    BEGIN
        INSERT INTO gateway.order_event (order_id, sequence, from_status, to_status, occurred_at, reason)
        VALUES ('50000000-0000-0000-0000-000000000001', 3, 'COMPLIANCE_REVIEW', 'READY_TO_SETTLE', now(), 'WRONG_PREVIOUS');
        RAISE EXCEPTION 'FAILED: an event that does not follow the previous one was accepted';
    EXCEPTION WHEN SQLSTATE 'OR001' THEN
        RAISE NOTICE 'ok: each event must continue from the previous state';
    END;

    BEGIN
        UPDATE gateway.order_event SET reason = 'EDITED' WHERE sequence = 1;
        RAISE EXCEPTION 'FAILED: order history was edited';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: order history cannot be edited';
    END;

    BEGIN
        INSERT INTO gateway.payment_order (
            order_id, client_id, customer_id, quote_id, destination_address, status, payment_deadline, created_at, updated_at)
        VALUES ('50000000-0000-0000-0000-000000000002', 'client-a', 'cust_verified', '40000000-0000-0000-0000-000000000001',
                '0x1111111111111111111111111111111111111111', 'AWAITING_FIAT_PAYMENT', now(), now(), now());
        RAISE EXCEPTION 'FAILED: one quote backed two orders';
    EXCEPTION WHEN unique_violation THEN
        RAISE NOTICE 'ok: a quote backs one order only';
    END;

    BEGIN
        INSERT INTO gateway.payment_order (
            order_id, client_id, customer_id, quote_id, destination_address, status, payment_deadline,
            fiat_provider, fiat_payment_id, created_at, updated_at)
        VALUES ('50000000-0000-0000-0000-000000000003', 'client-a', 'cust_verified', '40000000-0000-0000-0000-000000000002',
                '0x1111111111111111111111111111111111111111', 'FIAT_RECEIVED', now(), 'sandbox-fiat', 'pay_1', now(), now());
        RAISE EXCEPTION 'FAILED: one provider payment settled two orders';
    EXCEPTION WHEN unique_violation THEN
        RAISE NOTICE 'ok: a provider payment settles one order only';
    END;

    BEGIN
        INSERT INTO gateway.quote (
            quote_id, client_id, source_asset_key, source_amount, service_fee, net_source_amount,
            destination_asset_key, gross_destination_amount, network_fee, destination_amount,
            market_rate, customer_rate, pivot_asset_key, pivot_amount, price_path, pricing_policy_version, created_at, expires_at)
        VALUES ('40000000-0000-0000-0000-000000000003', 'client-a', 'INR', 10000, 150, 9900,
                'USDC@ethereum', 115.64885, 1.5, 114.14885, 0.0118, 0.011741, 'USD', 118, '[]', 'test', now(), now() + INTERVAL '1 minute');
        RAISE EXCEPTION 'FAILED: a quote whose figures do not add up was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: quote figures must add up';
    END;

    BEGIN
        INSERT INTO gateway.asset (asset_key, kind, code, network, scale, network_fee_asset_key, name)
        VALUES ('USDC', 'CRYPTO', 'USDC', NULL, 6, NULL, 'Ambiguous USDC');
        RAISE EXCEPTION 'FAILED: a crypto asset without a network was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: crypto assets must name their network';
    END;

    BEGIN
        INSERT INTO gateway.idempotency_key (client_id, idempotency_key, request_sha256, response_status)
        VALUES ('client-a', 'key-1', repeat('b', 64), 201);
        RAISE EXCEPTION 'FAILED: an idempotency record completed without its response was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: idempotency records store the full response';
    END;
END $$;

ROLLBACK;
