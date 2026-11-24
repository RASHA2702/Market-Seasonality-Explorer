-- Merchant notifications from the outbox: recipient, ordering, retries and dead letters. One transaction.

BEGIN;

ALTER TABLE ops.outbox_message
    ADD COLUMN client_id TEXT NOT NULL,
    ADD COLUMN event_sequence INTEGER NOT NULL CHECK (event_sequence >= 1),
    ADD COLUMN next_attempt_at TIMESTAMPTZ NOT NULL,
    ADD COLUMN last_error TEXT,
    ADD COLUMN dead_at TIMESTAMPTZ;

-- One notification per order event, so a retried save can never queue it twice.
ALTER TABLE ops.outbox_message ADD CONSTRAINT uq_outbox_event UNIQUE (aggregate_id, event_type, event_sequence);
CREATE INDEX ix_outbox_pending ON ops.outbox_message (aggregate_id, event_sequence)
    WHERE published_at IS NULL AND dead_at IS NULL;

INSERT INTO ops.schema_migration (version) VALUES ('005_merchant_webhooks');

COMMIT;
