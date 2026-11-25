-- Constraint tests for 008_settlement_wallets. Runs in one transaction that is rolled back.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/008_settlement_wallet_constraints.sql

BEGIN;

INSERT INTO gateway.api_client (client_id, display_name, key_sha256, role)
VALUES ('merchant-008', 'Test merchant', repeat('8', 64), 'MERCHANT');
INSERT INTO gateway.merchant_settlement (client_id) VALUES ('merchant-008');
INSERT INTO gateway.settlement_wallet (
    wallet_id, client_id, network, address, status, screening_risk, screener, registered_at, usable_from)
VALUES
    ('c0000000-0000-0000-0000-000000000001', 'merchant-008', 'polygon', '0x1111111111111111111111111111111111111111',
     'APPROVED', 'LOW', 'test', '2026-11-25T00:00:00Z', '2026-11-26T00:00:00Z'),
    ('c0000000-0000-0000-0000-000000000002', 'merchant-008', 'ethereum', '0x5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a',
     'UNDER_REVIEW', 'BLOCKED', 'test', '2026-11-25T00:00:00Z', '2026-11-26T00:00:00Z'),
    ('c0000000-0000-0000-0000-000000000003', 'merchant-008', 'tron', 'TLyqzVGLV1srkB7dToTAEqgDSfPtXRJZYH',
     'DISABLED', 'LOW', 'test', '2026-11-25T00:00:00Z', '2026-11-26T00:00:00Z');

DO $$
BEGIN
    BEGIN
        UPDATE gateway.settlement_wallet SET status = 'APPROVED' WHERE wallet_id = 'c0000000-0000-0000-0000-000000000002';
        RAISE EXCEPTION 'FAILED: a blocked wallet was approved';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: a blocked wallet can never be approved';
    END;

    BEGIN
        UPDATE gateway.settlement_wallet SET address = '0x9999999999999999999999999999999999999999'
         WHERE wallet_id = 'c0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: a screened wallet''s address was changed';
    EXCEPTION WHEN SQLSTATE 'LG004' THEN
        RAISE NOTICE 'ok: a wallet''s address and screening never change';
    END;

    BEGIN
        UPDATE gateway.settlement_wallet SET status = 'APPROVED' WHERE wallet_id = 'c0000000-0000-0000-0000-000000000003';
        RAISE EXCEPTION 'FAILED: a disabled wallet was reopened';
    EXCEPTION WHEN SQLSTATE 'LG004' THEN
        RAISE NOTICE 'ok: a closed wallet stays closed';
    END;

    BEGIN
        UPDATE gateway.settlement_wallet SET status = 'UNDER_REVIEW' WHERE wallet_id = 'c0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: an approved wallet went back under review';
    EXCEPTION WHEN SQLSTATE 'LG004' THEN
        RAISE NOTICE 'ok: an approved wallet can only be disabled';
    END;

    BEGIN
        DELETE FROM gateway.settlement_wallet WHERE wallet_id = 'c0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: a settlement wallet was deleted';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: settlement wallets are never deleted';
    END;

    BEGIN
        INSERT INTO gateway.settlement_wallet_action (wallet_id, sequence, action, actor, reason, occurred_at)
        VALUES ('c0000000-0000-0000-0000-000000000002', 1, 'REJECTED', 'officer', NULL, now());
        RAISE EXCEPTION 'FAILED: a review decision without a reason was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: review decisions always carry a reason';
    END;
END $$;

ROLLBACK;
