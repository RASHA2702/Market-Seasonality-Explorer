-- What the PostgreSQL stores need to rebuild quotes and orders exactly as the code held them.
-- One transaction; records itself in ops.schema_migration. Apply with db/migrate.sh.

BEGIN;

-- The currency the pivot amount is in (USD by default), so it can be rebuilt as money.
ALTER TABLE gateway.quote ADD COLUMN pivot_asset_key TEXT NOT NULL REFERENCES gateway.asset (asset_key);

-- Each provider's record of a step, kept as evidence. The money itself is in the ledger.
ALTER TABLE gateway.payment_order
    ADD COLUMN fiat_payment JSONB,
    ADD COLUMN purchase JSONB,
    ADD COLUMN transfer JSONB,
    ADD COLUMN refund JSONB;

-- Every compliance decision on an order is kept, in order, and can never be changed.
ALTER TABLE gateway.compliance_decision ADD COLUMN sequence INTEGER NOT NULL CHECK (sequence >= 1);
ALTER TABLE gateway.compliance_decision
    ADD CONSTRAINT uq_compliance_decision_sequence UNIQUE (order_id, sequence);
CREATE TRIGGER compliance_decision_append_only BEFORE UPDATE OR DELETE ON gateway.compliance_decision
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER compliance_decision_no_truncate BEFORE TRUNCATE ON gateway.compliance_decision
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

-- A stable posting order, even for entries recorded in the same instant.
ALTER TABLE ledger.journal_entry ADD COLUMN entry_number BIGINT GENERATED ALWAYS AS IDENTITY;
CREATE UNIQUE INDEX ix_journal_entry_number ON ledger.journal_entry (entry_number);

-- A replayed response must be byte-for-byte the first one; JSONB would reformat it.
ALTER TABLE gateway.idempotency_key ALTER COLUMN response_body TYPE TEXT;

INSERT INTO ops.schema_migration (version) VALUES ('002_persistence');

COMMIT;
