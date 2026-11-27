-- The sweep that finishes approved payments looks for the few attempts that were approved and not yet captured.
-- This index keeps that look-up cheap however many attempts the table holds.

BEGIN;

CREATE INDEX ix_payment_attempt_authorized ON gateway.payment_attempt (updated_at)
    WHERE status = 'AUTHORIZED';

INSERT INTO ops.schema_migration (version) VALUES ('013_payment_attempt_authorized_index');

COMMIT;
