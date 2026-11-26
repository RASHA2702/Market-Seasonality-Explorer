-- Constraint tests for 010_reviewer_access. Runs in one transaction that is rolled back.
-- Usage: psql --set ON_ERROR_STOP=1 --file db/tests/010_reviewer_access_constraints.sql

BEGIN;

INSERT INTO ops.reviewer_session (session_id, token_sha256, username, credential_fingerprint, created_at, last_seen_at, expires_at)
VALUES
    ('d0000000-0000-0000-0000-000000000001', decode(repeat('01', 32), 'hex'), 'reviewer-010', decode(repeat('aa', 32), 'hex'),
     '2026-11-26T09:00:00Z', '2026-11-26T09:00:00Z', '2026-11-26T21:00:00Z'),
    ('d0000000-0000-0000-0000-000000000002', decode(repeat('02', 32), 'hex'), 'reviewer-010', decode(repeat('aa', 32), 'hex'),
     '2026-11-26T09:00:00Z', '2026-11-26T09:00:00Z', '2026-11-26T21:00:00Z');
UPDATE ops.reviewer_session SET ended_at = '2026-11-26T09:30:00Z' WHERE session_id = 'd0000000-0000-0000-0000-000000000002';
INSERT INTO ops.reviewer_access_event (username, kind, occurred_at, session_id)
VALUES ('reviewer-010', 'SIGNED_IN', '2026-11-26T09:00:00Z', 'd0000000-0000-0000-0000-000000000001');

DO $$
BEGIN
    BEGIN
        UPDATE ops.reviewer_access_event SET kind = 'SIGN_IN_FAILED', session_id = NULL WHERE username = 'reviewer-010';
        RAISE EXCEPTION 'FAILED: the sign-in log was changed';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: the sign-in log is append-only';
    END;

    BEGIN
        INSERT INTO ops.reviewer_access_event (username, kind, occurred_at) VALUES ('reviewer-010', 'SIGNED_IN', now());
        RAISE EXCEPTION 'FAILED: a sign-in was logged without its session';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: every sign-in and sign-out names its session';
    END;

    BEGIN
        UPDATE ops.reviewer_session SET username = 'someone-else' WHERE session_id = 'd0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: a session was handed to another reviewer';
    EXCEPTION WHEN SQLSTATE 'LG005' THEN
        RAISE NOTICE 'ok: a session''s reviewer, token and limits never change';
    END;

    BEGIN
        UPDATE ops.reviewer_session SET expires_at = '2026-11-30T09:00:00Z' WHERE session_id = 'd0000000-0000-0000-0000-000000000001';
        RAISE EXCEPTION 'FAILED: a session was extended';
    EXCEPTION WHEN SQLSTATE 'LG005' THEN
        RAISE NOTICE 'ok: a session cannot be extended';
    END;

    BEGIN
        UPDATE ops.reviewer_session SET ended_at = NULL WHERE session_id = 'd0000000-0000-0000-0000-000000000002';
        RAISE EXCEPTION 'FAILED: an ended session was reopened';
    EXCEPTION WHEN SQLSTATE 'LG005' THEN
        RAISE NOTICE 'ok: an ended session stays ended';
    END;

    BEGIN
        DELETE FROM ops.reviewer_session WHERE session_id = 'd0000000-0000-0000-0000-000000000002';
        RAISE EXCEPTION 'FAILED: a session was deleted';
    EXCEPTION WHEN SQLSTATE 'LG003' THEN
        RAISE NOTICE 'ok: sessions are kept as evidence';
    END;

    BEGIN
        INSERT INTO ops.reviewer_session (session_id, token_sha256, username, credential_fingerprint, created_at, last_seen_at, expires_at)
        VALUES ('d0000000-0000-0000-0000-000000000003', decode(repeat('03', 16), 'hex'), 'reviewer-010', decode(repeat('aa', 32), 'hex'),
                now(), now(), now() + interval '1 hour');
        RAISE EXCEPTION 'FAILED: a session was stored with a short token hash';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: a session is stored by the SHA-256 of its token';
    END;

    BEGIN
        INSERT INTO ops.reviewer_guard (username) VALUES ('Reviewer 010');
        RAISE EXCEPTION 'FAILED: an invalid username was stored';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'ok: usernames follow the settings rule';
    END;
END $$;

ROLLBACK;
