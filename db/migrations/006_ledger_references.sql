-- Each journal entry records the provider movement it accounts for, so it can be reconciled against the
-- provider's reports. One transaction.

BEGIN;

ALTER TABLE ledger.journal_entry
    ADD COLUMN external_provider TEXT,
    ADD COLUMN external_id TEXT,
    ADD COLUMN external_asset_key TEXT REFERENCES gateway.asset (asset_key),
    ADD COLUMN external_amount NUMERIC(38, 18),
    ADD COLUMN external_occurred_at TIMESTAMPTZ;

-- All or nothing: an entry either names a complete provider movement or none (internal entries).
ALTER TABLE ledger.journal_entry ADD CONSTRAINT ck_journal_entry_external_complete CHECK (
    (external_provider IS NULL AND external_id IS NULL AND external_asset_key IS NULL
        AND external_amount IS NULL AND external_occurred_at IS NULL)
    OR (btrim(external_provider) <> '' AND btrim(external_id) <> '' AND external_asset_key IS NOT NULL
        AND external_amount > 0 AND external_occurred_at IS NOT NULL));

-- A provider movement is booked once per kind: the same payment can never be credited twice.
CREATE UNIQUE INDEX ux_journal_entry_external ON ledger.journal_entry (external_provider, kind, external_id)
    WHERE external_id IS NOT NULL;
CREATE INDEX ix_journal_entry_external_time ON ledger.journal_entry (external_provider, external_occurred_at)
    WHERE external_id IS NOT NULL;

INSERT INTO ops.schema_migration (version) VALUES ('006_ledger_references');

COMMIT;
