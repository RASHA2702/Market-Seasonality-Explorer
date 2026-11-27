-- Constraint tests for 012_payment_attempts. Runs in one transaction that is rolled back.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/012_payment_attempt_constraints.sql

BEGIN;

INSERT INTO gateway.asset (asset_key, kind, code, network, scale, network_fee_asset_key, name) VALUES
    ('INR', 'FIAT', 'INR', NULL, 2, NULL, 'Indian Rupee'),
    ('USD', 'FIAT', 'USD', NULL, 2, NULL, 'US Dollar'),
    ('ETH@ethereum', 'CRYPTO', 'ETH', 'ethereum', 18, 'ETH@ethereum', 'Ether'),
    ('USDC@ethereum', 'CRYPTO', 'USDC', 'ethereum', 6, 'ETH@ethereum', 'USD Coin on Ethereum')
ON CONFLICT (asset_key) DO NOTHING;
INSERT INTO gateway.api_client (client_id, display_name, key_sha256) VALUES ('client-a', 'Test client', repeat('a', 64))
ON CONFLICT (client_id) DO NOTHING;
INSERT INTO gateway.quote (
    quote_id, client_id, source_asset_key, source_amount, service_fee, net_source_amount,
    destination_asset_key, gross_destination_amount, network_fee, destination_amount,
    market_rate, customer_rate, pivot_asset_key, pivot_amount, price_path, pricing_policy_version, created_at, expires_at)
VALUES ('70000000-0000-0000-0000-000000000012', 'client-a', 'INR', 10000, 150, 9850,
        'USDC@ethereum', 115.64885, 1.5, 114.14885, 0.0118, 0.011741, 'USD', 118, '[]', 'test',
        '2026-11-24T12:00:00Z', '2026-11-24T12:01:00Z');
INSERT INTO gateway.payment_order (
    order_id, client_id, customer_id, quote_id, destination_address, status, payment_deadline, created_at, updated_at)
VALUES ('80000000-0000-0000-0000-000000000012', 'client-a', 'cust_verified', '70000000-0000-0000-0000-000000000012',
        '0x1111111111111111111111111111111111111111', 'AWAITING_FIAT_PAYMENT', '2026-11-24T12:15:00Z',
        '2026-11-24T12:00:00Z', '2026-11-24T12:00:00Z');
INSERT INTO gateway.order_event (order_id, sequence, from_status, to_status, occurred_at, reason)
VALUES ('80000000-0000-0000-0000-000000000012', 1, NULL, 'AWAITING_FIAT_PAYMENT', '2026-11-24T12:00:00Z', 'ORDER_CREATED');

-- A1 has just started. A2 was paid: started, authorised, captured.
INSERT INTO gateway.payment_attempt (
    attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at, row_version)
VALUES ('a0000000-0000-0000-0000-000000000001', '80000000-0000-0000-0000-000000000012', 'client-a', 'UPI', 'INR', 10000, 'STARTED',
        'success@sandbox', '2026-11-24T12:00:00Z', '2026-11-24T12:00:00Z', '2026-11-24T12:10:00Z', 1);
INSERT INTO gateway.payment_attempt_event (attempt_id, sequence, from_status, to_status, occurred_at, reason)
VALUES ('a0000000-0000-0000-0000-000000000001', 1, NULL, 'STARTED', '2026-11-24T12:00:00Z', 'ATTEMPT_STARTED');

INSERT INTO gateway.payment_attempt (
    attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, instrument_brand, instrument_last4,
    rail_reference, created_at, updated_at, expires_at, captured_at, provider_payment_id, row_version)
VALUES ('a0000000-0000-0000-0000-000000000002', '80000000-0000-0000-0000-000000000012', 'client-a', 'CARD', 'INR', 10000, 'CAPTURED',
        'Visa ····1111', 'Visa', '1111', 'card_2', '2026-11-24T12:01:00Z', '2026-11-24T12:01:02Z', '2026-11-24T12:11:00Z',
        '2026-11-24T12:01:02Z', 'gp_pay_2', 3);
INSERT INTO gateway.payment_attempt_event (attempt_id, sequence, from_status, to_status, occurred_at, reason) VALUES
    ('a0000000-0000-0000-0000-000000000002', 1, NULL, 'STARTED', '2026-11-24T12:01:00Z', 'ATTEMPT_STARTED'),
    ('a0000000-0000-0000-0000-000000000002', 2, 'STARTED', 'AUTHORIZED', '2026-11-24T12:01:01Z', 'AUTHORIZED'),
    ('a0000000-0000-0000-0000-000000000002', 3, 'AUTHORIZED', 'CAPTURED', '2026-11-24T12:01:02Z', 'CAPTURED');

-- Fixtures are consistent, so checking the history at commit passes for them.
SET CONSTRAINTS gateway.payment_attempt_matches_history IMMEDIATE;
SET CONSTRAINTS gateway.payment_attempt_matches_history DEFERRED;

DO $$
BEGIN
    BEGIN
        INSERT INTO gateway.payment_attempt_event (attempt_id, sequence, from_status, to_status, occurred_at, reason)
        VALUES ('a0000000-0000-0000-0000-000000000001', 2, 'STARTED', 'CAPTURED', now(), 'CAPTURED');
        RAISE EXCEPTION 'FAILED: an attempt was captured without being authorised';
    EXCEPTION WHEN foreign_key_violation THEN
        RAISE NOTICE 'ok: a payment attempt takes only legal steps';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt_event (attempt_id, sequence, from_status, to_status, occurred_at, reason)
        VALUES ('a0000000-0000-0000-0000-000000000001', 3, 'STARTED', 'AUTHORIZED', now(), 'AUTHORIZED');
        RAISE EXCEPTION 'FAILED: an event skipped one in the history';
    EXCEPTION WHEN SQLSTATE 'PA001' THEN
        RAISE NOTICE 'ok: each event continues from the one before it';
    END;

    BEGIN
        UPDATE gateway.payment_attempt SET status = 'AUTHORIZED', row_version = 2 WHERE attempt_id = 'a0000000-0000-0000-0000-000000000001';
        SET CONSTRAINTS gateway.payment_attempt_matches_history IMMEDIATE;
        RAISE EXCEPTION 'FAILED: an attempt changed status without a matching event';
    EXCEPTION WHEN SQLSTATE 'PA001' THEN
        RAISE NOTICE 'ok: an attempt''s status is where its history ends';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt (
            attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at,
            captured_at, provider_payment_id, row_version)
        VALUES ('a0000000-0000-0000-0000-000000000003', '80000000-0000-0000-0000-000000000012', 'client-a', 'UPI', 'INR', 10000, 'CAPTURED',
                'success@sandbox', '2026-11-24T12:02:00Z', '2026-11-24T12:02:00Z', '2026-11-24T12:12:00Z',
                '2026-11-24T12:02:00Z', 'gp_pay_3', 1);
        RAISE EXCEPTION 'FAILED: an order was paid twice';
    EXCEPTION WHEN unique_violation THEN
        RAISE NOTICE 'ok: an order is paid once';
    END;

    BEGIN
        UPDATE gateway.payment_attempt SET amount = 1, row_version = 2 WHERE attempt_id = 'a0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: what an attempt was for changed';
    EXCEPTION WHEN SQLSTATE 'PA002' THEN
        RAISE NOTICE 'ok: what an attempt is for never changes';
    END;

    BEGIN
        UPDATE gateway.payment_attempt SET updated_at = now() WHERE attempt_id = 'a0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: an attempt changed without raising its version';
    EXCEPTION WHEN SQLSTATE 'PA002' THEN
        RAISE NOTICE 'ok: each change raises the version by one';
    END;

    BEGIN
        UPDATE gateway.payment_attempt SET provider_payment_id = 'gp_pay_other', row_version = 4 WHERE attempt_id = 'a0000000-0000-0000-0000-000000000002';
        RAISE EXCEPTION 'FAILED: a captured payment''s identity changed';
    EXCEPTION WHEN SQLSTATE 'PA002' THEN
        RAISE NOTICE 'ok: a captured payment''s identity never changes';
    END;

    -- A captured payment may be refunded: the legal step goes through.
    INSERT INTO gateway.payment_attempt_event (attempt_id, sequence, from_status, to_status, occurred_at, reason)
    VALUES ('a0000000-0000-0000-0000-000000000002', 4, 'CAPTURED', 'REFUNDED', '2026-11-24T13:00:00Z', 'REFUNDED');
    UPDATE gateway.payment_attempt
       SET status = 'REFUNDED', refund_id = 'gp_rfd_2', refunded_at = '2026-11-24T13:00:00Z', updated_at = '2026-11-24T13:00:00Z',
           recorded_on_order_at = '2026-11-24T12:01:03Z', row_version = 4
     WHERE attempt_id = 'a0000000-0000-0000-0000-000000000002';
    SET CONSTRAINTS gateway.payment_attempt_matches_history IMMEDIATE;
    SET CONSTRAINTS gateway.payment_attempt_matches_history DEFERRED;
    RAISE NOTICE 'ok: a captured payment can be refunded';

    BEGIN
        UPDATE gateway.payment_attempt SET updated_at = now(), row_version = 5 WHERE attempt_id = 'a0000000-0000-0000-0000-000000000002';
        RAISE EXCEPTION 'FAILED: a refunded attempt changed';
    EXCEPTION WHEN SQLSTATE 'PA002' THEN
        RAISE NOTICE 'ok: a finished attempt is final';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt (
            attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at, row_version)
        VALUES ('a0000000-0000-0000-0000-000000000004', '80000000-0000-0000-0000-000000000012', 'client-a', 'CARD', 'INR', 10000, 'STARTED',
                'Visa 4111 1111 1111 1111', '2026-11-24T12:03:00Z', '2026-11-24T12:03:00Z', '2026-11-24T12:13:00Z', 1);
        RAISE EXCEPTION 'FAILED: a card number was stored in a label';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: no card number can be stored in an attempt';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt_event (attempt_id, sequence, from_status, to_status, occurred_at, reason)
        VALUES ('a0000000-0000-0000-0000-000000000001', 2, 'STARTED', 'DECLINED', now(), '4111111111111111');
        RAISE EXCEPTION 'FAILED: a card number was stored as an event reason';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: nor in its history';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt (
            attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, instrument_last4,
            created_at, updated_at, expires_at, row_version)
        VALUES ('a0000000-0000-0000-0000-000000000005', '80000000-0000-0000-0000-000000000012', 'client-a', 'CARD', 'INR', 10000, 'STARTED',
                'Visa', '41111', '2026-11-24T12:03:00Z', '2026-11-24T12:03:00Z', '2026-11-24T12:13:00Z', 1);
        RAISE EXCEPTION 'FAILED: more than the last four digits were stored';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: only the last four digits are kept';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt (
            attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at, row_version)
        VALUES ('a0000000-0000-0000-0000-000000000006', '80000000-0000-0000-0000-000000000012', 'client-a', 'UPI', 'INR', 10000, 'DECLINED',
                'success@sandbox', '2026-11-24T12:03:00Z', '2026-11-24T12:03:00Z', '2026-11-24T12:13:00Z', 1);
        RAISE EXCEPTION 'FAILED: a declined attempt had no reason';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: an attempt that did not succeed says why';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt (
            attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at, row_version)
        VALUES ('a0000000-0000-0000-0000-000000000007', '80000000-0000-0000-0000-000000000012', 'client-a', 'UPI', 'INR', 0, 'STARTED',
                'success@sandbox', '2026-11-24T12:03:00Z', '2026-11-24T12:03:00Z', '2026-11-24T12:13:00Z', 1);
        RAISE EXCEPTION 'FAILED: a payment of nothing was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: a payment is for a positive amount';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt (
            attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at,
            captured_at, row_version)
        VALUES ('a0000000-0000-0000-0000-000000000008', '80000000-0000-0000-0000-000000000012', 'client-a', 'UPI', 'INR', 10000, 'AUTHORIZED',
                'success@sandbox', '2026-11-24T12:03:00Z', '2026-11-24T12:03:00Z', '2026-11-24T12:13:00Z', now(), 1);
        RAISE EXCEPTION 'FAILED: an uncaptured attempt had a capture time';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: only a captured payment has a capture time and ID';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt (
            attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at,
            captured_at, provider_payment_id, row_version)
        VALUES ('a0000000-0000-0000-0000-000000000009', '80000000-0000-0000-0000-000000000012', 'client-a', 'UPI', 'INR', 10000, 'AUTHORIZED',
                'success@sandbox', '2026-11-24T12:03:00Z', '2026-11-24T12:03:00Z', '2026-11-24T12:13:00Z', now(), 'gp_pay_9', 1);
        RAISE EXCEPTION 'FAILED: an uncaptured attempt had a capture time and a payment ID';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: nor a payment ID';
    END;

    BEGIN
        INSERT INTO gateway.payment_attempt (
            attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at, row_version)
        VALUES ('a0000000-0000-0000-0000-00000000000a', '80000000-0000-0000-0000-000000000012', 'client-a', 'UPI', 'INR', 10000, 'CAPTURED',
                'success@sandbox', '2026-11-24T12:03:00Z', '2026-11-24T12:03:00Z', '2026-11-24T12:13:00Z', 1);
        RAISE EXCEPTION 'FAILED: a captured payment had no capture time or payment ID';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: a captured payment always has both';
    END;

    BEGIN
        UPDATE gateway.payment_attempt_event SET reason = 'CHANGED' WHERE attempt_id = 'a0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: an attempt''s history was rewritten';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: an attempt''s history is append-only';
    END;

    BEGIN
        DELETE FROM gateway.payment_attempt WHERE attempt_id = 'a0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: an attempt was deleted';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: attempts are kept as evidence';
    END;
END $$;

ROLLBACK;
