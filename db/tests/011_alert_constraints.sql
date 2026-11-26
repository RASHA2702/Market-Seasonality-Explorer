-- Constraint tests for 011_alerts_and_backup_runs. Runs in one transaction that is rolled back.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/011_alert_constraints.sql

BEGIN;

INSERT INTO ops.backup_run (succeeded, file, bytes) VALUES (TRUE, 'gateway-20261126T210000Z.dump.age', 85000);
INSERT INTO ops.alert_log (sent_at, kind, problem_keys) VALUES (now(), 'PROBLEMS', ARRAY['held:1:due']);

DO $$
BEGIN
    BEGIN
        UPDATE ops.backup_run SET succeeded = FALSE;
        RAISE EXCEPTION 'FAILED: a backup run was rewritten';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: the backup log is append-only';
    END;

    BEGIN
        INSERT INTO ops.backup_run (succeeded, file) VALUES (FALSE, 'gateway-20261126T220000Z.dump.age');
        RAISE EXCEPTION 'FAILED: a failed backup named a file';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: only a successful backup names its file';
    END;

    BEGIN
        DELETE FROM ops.alert_log;
        RAISE EXCEPTION 'FAILED: an alert record was deleted';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: the alert log is append-only';
    END;

    BEGIN
        INSERT INTO ops.alert_log (sent_at, kind, problem_keys) VALUES (now(), 'ALL_CLEAR', ARRAY['held:1:due']);
        RAISE EXCEPTION 'FAILED: an all-clear email listed problems';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: an all-clear lists no problems';
    END;
END $$;

ROLLBACK;
