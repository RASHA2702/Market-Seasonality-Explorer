-- What the backup container did, and which alert emails were sent. Both append-only. One transaction.

BEGIN;

-- Every backup attempt, written by the backup container (deploy/backup) when it finishes.
CREATE TABLE ops.backup_run (
    run_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    finished_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    succeeded BOOLEAN NOT NULL,
    file TEXT CHECK (file ~ '^gateway-[0-9]{8}T[0-9]{6}Z\.dump\.age$'),
    bytes BIGINT CHECK (bytes > 0),
    detail TEXT CHECK (length(detail) <= 2000),
    CONSTRAINT ck_backup_run_file CHECK (succeeded = (file IS NOT NULL))
);
CREATE TRIGGER backup_run_append_only BEFORE UPDATE OR DELETE ON ops.backup_run
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER backup_run_no_truncate BEFORE TRUNCATE ON ops.backup_run
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

-- Every alert email sent, with the problems it reported (none for "all clear").
CREATE TABLE ops.alert_log (
    alert_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sent_at TIMESTAMPTZ NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('PROBLEMS', 'ALL_CLEAR')),
    problem_keys TEXT[] NOT NULL,
    CONSTRAINT ck_alert_log_keys CHECK ((kind = 'ALL_CLEAR') = (cardinality(problem_keys) = 0))
);
CREATE TRIGGER alert_log_append_only BEFORE UPDATE OR DELETE ON ops.alert_log
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER alert_log_no_truncate BEFORE TRUNCATE ON ops.alert_log
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

INSERT INTO ops.schema_migration (version) VALUES ('011_alerts_and_backup_runs');

COMMIT;
