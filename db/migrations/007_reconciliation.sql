-- Daily reconciliation runs and the discrepancies each found. Both are evidence, so append-only. One transaction.

BEGIN;

CREATE TABLE ops.reconciliation_run (
    run_id UUID PRIMARY KEY,
    run_number BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
    provider TEXT NOT NULL CHECK (btrim(provider) <> ''),
    business_day DATE NOT NULL,
    window_start TIMESTAMPTZ NOT NULL,
    window_end TIMESTAMPTZ NOT NULL CHECK (window_end > window_start),
    ran_at TIMESTAMPTZ NOT NULL,
    report_lines INTEGER NOT NULL CHECK (report_lines >= 0),
    ledger_entries INTEGER NOT NULL CHECK (ledger_entries >= 0),
    matched INTEGER NOT NULL CHECK (matched >= 0),
    discrepancies INTEGER NOT NULL CHECK (discrepancies >= 0)
);
CREATE INDEX ix_reconciliation_run_day ON ops.reconciliation_run (business_day, provider, ran_at DESC);

CREATE TABLE ops.reconciliation_discrepancy (
    run_id UUID NOT NULL REFERENCES ops.reconciliation_run (run_id),
    line_number INTEGER NOT NULL CHECK (line_number >= 1),
    discrepancy_type TEXT NOT NULL CHECK (discrepancy_type IN (
        'MISSING_IN_LEDGER', 'MISSING_IN_REPORT', 'AMOUNT_MISMATCH', 'DUPLICATE_IN_LEDGER', 'DUPLICATE_IN_REPORT')),
    kind TEXT NOT NULL,
    reference TEXT NOT NULL,
    order_id UUID,
    report_asset_key TEXT REFERENCES gateway.asset (asset_key),
    report_amount NUMERIC(38, 18) CHECK (report_amount > 0),
    ledger_asset_key TEXT REFERENCES gateway.asset (asset_key),
    ledger_amount NUMERIC(38, 18) CHECK (ledger_amount > 0),
    PRIMARY KEY (run_id, line_number),
    CHECK ((report_amount IS NULL) = (report_asset_key IS NULL)),
    CHECK ((ledger_amount IS NULL) = (ledger_asset_key IS NULL)),
    -- Each type shows the side(s) it is about.
    CHECK (CASE discrepancy_type
        WHEN 'MISSING_IN_LEDGER' THEN report_amount IS NOT NULL AND ledger_amount IS NULL
        WHEN 'MISSING_IN_REPORT' THEN report_amount IS NULL AND ledger_amount IS NOT NULL
        WHEN 'AMOUNT_MISMATCH' THEN report_amount IS NOT NULL AND ledger_amount IS NOT NULL
        ELSE report_amount IS NOT NULL OR ledger_amount IS NOT NULL
    END)
);

CREATE TRIGGER reconciliation_run_append_only BEFORE UPDATE OR DELETE ON ops.reconciliation_run
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER reconciliation_run_no_truncate BEFORE TRUNCATE ON ops.reconciliation_run
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER reconciliation_discrepancy_append_only BEFORE UPDATE OR DELETE ON ops.reconciliation_discrepancy
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER reconciliation_discrepancy_no_truncate BEFORE TRUNCATE ON ops.reconciliation_discrepancy
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

INSERT INTO ops.schema_migration (version) VALUES ('007_reconciliation');

COMMIT;
