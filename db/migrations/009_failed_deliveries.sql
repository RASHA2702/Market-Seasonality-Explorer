-- Lists the notifications given up on, newest first, for the reviewer console. One transaction.

BEGIN;

CREATE INDEX ix_outbox_given_up ON ops.outbox_message (dead_at DESC) WHERE dead_at IS NOT NULL;

INSERT INTO ops.schema_migration (version) VALUES ('009_failed_deliveries');

COMMIT;
