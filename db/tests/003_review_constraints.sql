-- Constraint tests for 003_review_queue (and the append-only compliance history from 002).
-- Runs in one transaction that is rolled back.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/003_review_constraints.sql

BEGIN;
SET CONSTRAINTS ALL IMMEDIATE;

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
VALUES ('70000000-0000-0000-0000-000000000001', 'client-a', 'INR', 10000, 150, 9850,
        'USDC@ethereum', 115.64885, 1.5, 114.14885, 0.0118, 0.011741, 'USD', 118, '[]', 'test',
        '2026-11-24T12:00:00Z', '2026-11-24T12:01:00Z');
INSERT INTO gateway.payment_order (
    order_id, client_id, customer_id, quote_id, destination_address, status, payment_deadline, created_at, updated_at)
VALUES ('80000000-0000-0000-0000-000000000001', 'client-a', 'cust_verified', '70000000-0000-0000-0000-000000000001',
        '0x1111111111111111111111111111111111111111', 'COMPLIANCE_REJECTED', '2026-11-24T12:15:00Z',
        '2026-11-24T12:00:30Z', '2026-11-24T12:02:00Z');
INSERT INTO gateway.order_event (order_id, sequence, from_status, to_status, occurred_at, reason) VALUES
    ('80000000-0000-0000-0000-000000000001', 1, NULL, 'AWAITING_FIAT_PAYMENT', '2026-11-24T12:00:30Z', 'ORDER_CREATED'),
    ('80000000-0000-0000-0000-000000000001', 2, 'AWAITING_FIAT_PAYMENT', 'FIAT_RECEIVED', '2026-11-24T12:01:00Z', 'FIAT_CAPTURED'),
    ('80000000-0000-0000-0000-000000000001', 3, 'FIAT_RECEIVED', 'COMPLIANCE_REJECTED', '2026-11-24T12:02:00Z', 'COMPLIANCE_REJECTED');
INSERT INTO gateway.compliance_decision (order_id, sequence, outcome, reasons, screener, decided_at)
VALUES ('80000000-0000-0000-0000-000000000001', 1, 'REJECTED', '["WALLET_BLOCKED"]', 'test', '2026-11-24T12:02:00Z');

DO $$
BEGIN
    BEGIN
        INSERT INTO gateway.order_event (order_id, sequence, from_status, to_status, occurred_at, reason)
        VALUES ('80000000-0000-0000-0000-000000000001', 4, 'COMPLIANCE_REJECTED', 'REFUND_PENDING', now(), 'STRAIGHT_TO_REFUND');
        RAISE EXCEPTION 'FAILED: rejected money went straight to refund';
    EXCEPTION WHEN foreign_key_violation THEN
        RAISE NOTICE 'ok: rejected money must be frozen before any refund';
    END;

    BEGIN
        INSERT INTO gateway.review_action (order_id, sequence, action, reviewer, reason, report_reference, occurred_at)
        VALUES ('80000000-0000-0000-0000-000000000001', 1, 'FREEZE_AND_REPORT', 'officer', 'Reported.', NULL, now());
        RAISE EXCEPTION 'FAILED: money was frozen without a report reference';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: freezing requires the report reference';
    END;

    BEGIN
        INSERT INTO gateway.review_action (order_id, sequence, action, reviewer, reason, report_reference, occurred_at)
        VALUES ('80000000-0000-0000-0000-000000000001', 1, 'REJECT', 'officer', '   ', NULL, now());
        RAISE EXCEPTION 'FAILED: a review action without a reason was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: every review action needs a reason';
    END;

    BEGIN
        INSERT INTO gateway.api_client (client_id, display_name, key_sha256, role) VALUES ('x', 'x', repeat('c', 64), 'ADMIN');
        RAISE EXCEPTION 'FAILED: an unknown API role was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: API clients are merchants or reviewers';
    END;

    BEGIN
        UPDATE gateway.compliance_decision SET outcome = 'APPROVED';
        RAISE EXCEPTION 'FAILED: a compliance decision was edited';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: compliance decisions cannot be edited';
    END;
END $$;

-- The valid path: freeze with a report reference.
INSERT INTO gateway.order_event (order_id, sequence, from_status, to_status, occurred_at, reason)
VALUES ('80000000-0000-0000-0000-000000000001', 4, 'COMPLIANCE_REJECTED', 'FROZEN', now(), 'FROZEN_AND_REPORTED');
INSERT INTO gateway.review_action (order_id, sequence, action, reviewer, reason, report_reference, occurred_at)
VALUES ('80000000-0000-0000-0000-000000000001', 1, 'FREEZE_AND_REPORT', 'officer', 'Reported.', 'STR-1', now());

DO $$
BEGIN
    BEGIN
        UPDATE gateway.review_action SET reason = 'Edited later.';
        RAISE EXCEPTION 'FAILED: a review action was edited';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: review actions cannot be edited';
    END;

    BEGIN
        DELETE FROM gateway.review_action;
        RAISE EXCEPTION 'FAILED: a review action was deleted';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: review actions cannot be deleted';
    END;
END $$;

ROLLBACK;
