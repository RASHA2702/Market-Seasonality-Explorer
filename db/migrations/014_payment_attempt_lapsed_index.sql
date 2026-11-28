-- The alert check lists approvals the gateway gave up on, so a person can check that no customer was left debited.
-- It runs every few minutes. This index keeps that look-up cheap however many attempts the table holds: only the few
-- attempts that lapsed are in it. The query's condition must stay the same as this one for the index to be used.

BEGIN;

CREATE INDEX ix_payment_attempt_lapsed ON gateway.payment_attempt (updated_at)
    WHERE status = 'FAILED' AND decline_code = 'AUTHORIZATION_LAPSED';

INSERT INTO ops.schema_migration (version) VALUES ('014_payment_attempt_lapsed_index');

COMMIT;
