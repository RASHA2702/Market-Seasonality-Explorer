-- Constraint tests for 007_reconciliation. Runs in one transaction that is rolled back.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/007_reconciliation_constraints.sql

BEGIN;

INSERT INTO gateway.asset (asset_key, kind, code, network, scale, network_fee_asset_key, name)
VALUES ('INR', 'FIAT', 'INR', NULL, 2, NULL, 'Indian Rupee')
ON CONFLICT (asset_key) DO NOTHING;

INSERT INTO ops.reconciliation_run (
    run_id, provider, business_day, window_start, window_end, ran_at, report_lines, ledger_entries, matched, discrepancies)
VALUES ('b0000000-0000-0000-0000-000000000001', 'test-psp', '2026-11-24', '2026-11-23T18:30:00Z', '2026-11-24T18:30:00Z',
        '2026-11-25T01:00:00Z', 2, 1, 1, 1);
INSERT INTO ops.reconciliation_discrepancy (
    run_id, line_number, discrepancy_type, kind, reference, report_asset_key, report_amount)
VALUES ('b0000000-0000-0000-0000-000000000001', 1, 'MISSING_IN_LEDGER', 'FIAT_RECEIVED', 'pay_007', 'INR', 10000);

DO $$
BEGIN
    BEGIN
        UPDATE ops.reconciliation_run SET matched = 2;
        RAISE EXCEPTION 'FAILED: a reconciliation run was edited';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: reconciliation runs cannot be edited';
    END;

    BEGIN
        DELETE FROM ops.reconciliation_discrepancy;
        RAISE EXCEPTION 'FAILED: a discrepancy was deleted';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: discrepancies cannot be deleted';
    END;

    BEGIN
        INSERT INTO ops.reconciliation_discrepancy (
            run_id, line_number, discrepancy_type, kind, reference, ledger_asset_key, ledger_amount)
        VALUES ('b0000000-0000-0000-0000-000000000001', 2, 'MISSING_IN_LEDGER', 'FIAT_RECEIVED', 'pay_008', 'INR', 10);
        RAISE EXCEPTION 'FAILED: a missing-in-ledger discrepancy showed a ledger amount and no report amount';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: each discrepancy type shows the side it is about';
    END;

    BEGIN
        INSERT INTO ops.reconciliation_discrepancy (
            run_id, line_number, discrepancy_type, kind, reference, report_asset_key, report_amount)
        VALUES ('b0000000-0000-0000-0000-000000000001', 2, 'LOOKS_FINE', 'FIAT_RECEIVED', 'pay_009', 'INR', 10);
        RAISE EXCEPTION 'FAILED: an unknown discrepancy type was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: discrepancy types are a closed list';
    END;

    BEGIN
        INSERT INTO ops.reconciliation_run (
            run_id, provider, business_day, window_start, window_end, ran_at, report_lines, ledger_entries, matched, discrepancies)
        VALUES ('b0000000-0000-0000-0000-000000000002', 'test-psp', '2026-11-24', '2026-11-24T18:30:00Z', '2026-11-23T18:30:00Z',
                now(), 0, 0, 0, 0);
        RAISE EXCEPTION 'FAILED: a run whose window ends before it starts was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: a run window ends after it starts';
    END;
END $$;

ROLLBACK;
