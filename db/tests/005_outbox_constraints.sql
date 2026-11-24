-- Constraint tests for 005_merchant_webhooks. Runs in one transaction that is rolled back.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/005_outbox_constraints.sql

BEGIN;
SET CONSTRAINTS ALL IMMEDIATE;

INSERT INTO ops.outbox_message (
    message_id, aggregate_type, aggregate_id, client_id, event_sequence, event_type, payload, occurred_at, next_attempt_at)
VALUES ('90000000-0000-0000-0000-000000000001', 'payment_order', '80000000-0000-0000-0000-000000000005', 'client-a', 1,
        'order.status_changed', '{"id":"90000000-0000-0000-0000-000000000001"}', '2026-11-24T12:00:00Z', '2026-11-24T12:00:00Z');

DO $$
BEGIN
    BEGIN
        INSERT INTO ops.outbox_message (
            message_id, aggregate_type, aggregate_id, client_id, event_sequence, event_type, payload, occurred_at, next_attempt_at)
        VALUES ('90000000-0000-0000-0000-000000000002', 'payment_order', '80000000-0000-0000-0000-000000000005', 'client-a', 1,
                'order.status_changed', '{}', now(), now());
        RAISE EXCEPTION 'FAILED: the same order event was queued twice';
    EXCEPTION WHEN unique_violation THEN
        RAISE NOTICE 'ok: each order event is queued once';
    END;

    BEGIN
        INSERT INTO ops.outbox_message (
            message_id, aggregate_type, aggregate_id, client_id, event_sequence, event_type, payload, occurred_at, next_attempt_at)
        VALUES ('90000000-0000-0000-0000-000000000003', 'payment_order', '80000000-0000-0000-0000-000000000005', 'client-a', 0,
                'order.status_changed', '{}', now(), now());
        RAISE EXCEPTION 'FAILED: an event sequence below 1 was accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: event sequences start at 1';
    END;

    BEGIN
        INSERT INTO ops.outbox_message (
            message_id, aggregate_type, aggregate_id, event_sequence, event_type, payload, occurred_at, next_attempt_at)
        VALUES ('90000000-0000-0000-0000-000000000004', 'payment_order', '80000000-0000-0000-0000-000000000005', 2,
                'order.status_changed', '{}', now(), now());
        RAISE EXCEPTION 'FAILED: a notification without a recipient was accepted';
    EXCEPTION WHEN not_null_violation THEN
        RAISE NOTICE 'ok: every notification names the merchant it is for';
    END;
END $$;

ROLLBACK;
