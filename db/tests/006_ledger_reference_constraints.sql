-- Constraint tests for 006_ledger_references. Runs in one transaction that is rolled back, so the deferred
-- balance checks never fire and entries can be inserted without lines.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/006_ledger_reference_constraints.sql

BEGIN;

INSERT INTO gateway.asset (asset_key, kind, code, network, scale, network_fee_asset_key, name)
VALUES ('INR', 'FIAT', 'INR', NULL, 2, NULL, 'Indian Rupee')
ON CONFLICT (asset_key) DO NOTHING;

INSERT INTO ledger.journal_entry (
    entry_id, kind, order_id, posted_at, external_provider, external_id, external_asset_key, external_amount, external_occurred_at)
VALUES ('a0000000-0000-0000-0000-000000000001', 'FIAT_RECEIVED', NULL, '2026-11-24T12:00:00Z',
        'test-psp', 'pay_006', 'INR', 10000, '2026-11-24T11:59:00Z');

DO $$
BEGIN
    BEGIN
        INSERT INTO ledger.journal_entry (
            entry_id, kind, order_id, posted_at, external_provider, external_id, external_asset_key, external_amount, external_occurred_at)
        VALUES ('a0000000-0000-0000-0000-000000000002', 'FIAT_RECEIVED', NULL, now(), 'test-psp', 'pay_006', 'INR', 10000, now());
        RAISE EXCEPTION 'FAILED: one provider payment was booked twice';
    EXCEPTION WHEN unique_violation THEN
        RAISE NOTICE 'ok: a provider movement is booked once';
    END;

    BEGIN
        INSERT INTO ledger.journal_entry (entry_id, kind, order_id, posted_at, external_provider, external_id)
        VALUES ('a0000000-0000-0000-0000-000000000003', 'FIAT_RECEIVED', NULL, now(), 'test-psp', 'pay_007');
        RAISE EXCEPTION 'FAILED: a partial provider reference was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: provider references are complete or absent';
    END;

    BEGIN
        INSERT INTO ledger.journal_entry (
            entry_id, kind, order_id, posted_at, external_provider, external_id, external_asset_key, external_amount, external_occurred_at)
        VALUES ('a0000000-0000-0000-0000-000000000004', 'FIAT_REFUNDED', NULL, now(), 'test-psp', 'rfd_006', 'INR', 0, now());
        RAISE EXCEPTION 'FAILED: a zero provider amount was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: provider amounts are positive';
    END;
END $$;

ROLLBACK;
