-- Reviewer sign-in: pause and code-reuse state, sessions, and an append-only sign-in log. One transaction.
-- Reviewers themselves (username, password hash, authenticator key) are settings, never rows.

BEGIN;

-- One row per reviewer who has tried to sign in: failures in a row, any pause, and the last authenticator step used.
CREATE TABLE ops.reviewer_guard (
    username TEXT PRIMARY KEY CHECK (username ~ '^[a-z][a-z0-9._-]{1,31}$'),
    failed_attempts INTEGER NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until TIMESTAMPTZ,
    last_totp_step BIGINT NOT NULL DEFAULT 0 CHECK (last_totp_step >= 0)
);

-- A session is found by the SHA-256 of its token; the token itself is never stored.
CREATE TABLE ops.reviewer_session (
    session_id UUID PRIMARY KEY,
    token_sha256 BYTEA NOT NULL UNIQUE CHECK (octet_length(token_sha256) = 32),
    username TEXT NOT NULL CHECK (username ~ '^[a-z][a-z0-9._-]{1,31}$'),
    credential_fingerprint BYTEA NOT NULL CHECK (octet_length(credential_fingerprint) = 32),
    created_at TIMESTAMPTZ NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    ended_at TIMESTAMPTZ,
    CONSTRAINT ck_reviewer_session_times CHECK (
        last_seen_at >= created_at AND expires_at > created_at AND (ended_at IS NULL OR ended_at >= created_at))
);
CREATE INDEX ix_reviewer_session_username ON ops.reviewer_session (username, created_at DESC);

-- Every sign-in, failure, pause, refused attempt and sign-out. Append-only.
CREATE TABLE ops.reviewer_access_event (
    event_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username TEXT NOT NULL CHECK (username ~ '^[a-z][a-z0-9._-]{1,31}$'),
    kind TEXT NOT NULL CHECK (kind IN ('SIGNED_IN', 'SIGN_IN_FAILED', 'LOCKED_OUT', 'SIGN_IN_BLOCKED', 'SIGNED_OUT')),
    occurred_at TIMESTAMPTZ NOT NULL,
    session_id UUID REFERENCES ops.reviewer_session (session_id),
    CONSTRAINT ck_reviewer_access_event_session CHECK ((kind IN ('SIGNED_IN', 'SIGNED_OUT')) = (session_id IS NOT NULL))
);
CREATE INDEX ix_reviewer_access_event_username ON ops.reviewer_access_event (username, event_id DESC);
CREATE TRIGGER reviewer_access_event_append_only BEFORE UPDATE OR DELETE ON ops.reviewer_access_event
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER reviewer_access_event_no_truncate BEFORE TRUNCATE ON ops.reviewer_access_event
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

-- A session is kept as evidence. Who it belongs to, its token and its limits never change; it can only be used
-- (last_seen_at moves forward) until it is ended, once.
CREATE FUNCTION ops.guard_reviewer_session() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.ended_at IS NOT NULL
       OR NEW.session_id <> OLD.session_id
       OR NEW.token_sha256 <> OLD.token_sha256
       OR NEW.username <> OLD.username
       OR NEW.credential_fingerprint <> OLD.credential_fingerprint
       OR NEW.created_at <> OLD.created_at
       OR NEW.expires_at <> OLD.expires_at
       OR NEW.last_seen_at < OLD.last_seen_at THEN
        RAISE EXCEPTION 'a reviewer session can only be used, then ended once' USING ERRCODE = 'LG005';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER reviewer_session_guard BEFORE UPDATE ON ops.reviewer_session
    FOR EACH ROW EXECUTE FUNCTION ops.guard_reviewer_session();
CREATE TRIGGER reviewer_session_no_delete BEFORE DELETE ON ops.reviewer_session
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER reviewer_session_no_truncate BEFORE TRUNCATE ON ops.reviewer_session
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

INSERT INTO ops.schema_migration (version) VALUES ('010_reviewer_access');

COMMIT;
