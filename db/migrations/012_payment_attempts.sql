-- The payment processor's own records: every try by a customer to pay an order, and its history. One transaction.
-- These tables can never hold a card number or security code: there is no column for them, and every text column
-- refuses a run of 13 or more digits (the length of the shortest card number).

BEGIN;

CREATE TABLE gateway.payment_attempt_status (
    status TEXT PRIMARY KEY,
    is_terminal BOOLEAN NOT NULL
);

INSERT INTO gateway.payment_attempt_status (status, is_terminal) VALUES
    ('STARTED', FALSE),
    ('AWAITING_AUTHENTICATION', FALSE),
    ('AUTHORIZED', FALSE),
    ('CAPTURED', FALSE),
    ('DECLINED', TRUE),
    ('AUTHENTICATION_FAILED', TRUE),
    ('EXPIRED', TRUE),
    ('FAILED', TRUE),
    ('REFUNDED', TRUE);

CREATE TABLE gateway.payment_attempt_transition (
    from_status TEXT NOT NULL REFERENCES gateway.payment_attempt_status (status),
    to_status TEXT NOT NULL REFERENCES gateway.payment_attempt_status (status),
    PRIMARY KEY (from_status, to_status)
);

INSERT INTO gateway.payment_attempt_transition (from_status, to_status) VALUES
    ('STARTED', 'AWAITING_AUTHENTICATION'),
    ('STARTED', 'AUTHORIZED'),
    ('STARTED', 'DECLINED'),
    ('STARTED', 'FAILED'),
    ('STARTED', 'EXPIRED'),
    ('AWAITING_AUTHENTICATION', 'AUTHORIZED'),
    ('AWAITING_AUTHENTICATION', 'AUTHENTICATION_FAILED'),
    ('AWAITING_AUTHENTICATION', 'DECLINED'),
    ('AWAITING_AUTHENTICATION', 'FAILED'),
    ('AWAITING_AUTHENTICATION', 'EXPIRED'),
    ('AUTHORIZED', 'CAPTURED'),
    ('AUTHORIZED', 'FAILED'),
    ('CAPTURED', 'REFUNDED');

CREATE TABLE gateway.payment_attempt (
    attempt_id UUID PRIMARY KEY,
    order_id UUID NOT NULL REFERENCES gateway.payment_order (order_id),
    client_id TEXT NOT NULL REFERENCES gateway.api_client (client_id),
    method TEXT NOT NULL CHECK (method IN ('CARD', 'UPI')),
    amount_asset_key TEXT NOT NULL REFERENCES gateway.asset (asset_key),
    amount NUMERIC(38, 18) NOT NULL CHECK (amount > 0),
    status TEXT NOT NULL REFERENCES gateway.payment_attempt_status (status),
    -- How the customer paid, never the card number: a brand and last four digits, or a UPI address.
    instrument_label TEXT NOT NULL CHECK (length(instrument_label) BETWEEN 1 AND 64 AND instrument_label !~ '[0-9]([ -]?[0-9]){12}'),
    instrument_brand TEXT CHECK (length(instrument_brand) BETWEEN 1 AND 32 AND instrument_brand !~ '[0-9]([ -]?[0-9]){12}'),
    instrument_last4 TEXT CHECK (instrument_last4 ~ '^[0-9]{4}$'),
    rail_reference TEXT CHECK (length(rail_reference) BETWEEN 1 AND 128),
    challenge_kind TEXT CHECK (challenge_kind IN ('OTP', 'UPI_APPROVAL')),
    challenge_max_tries INTEGER CHECK (challenge_max_tries >= 1),
    challenge_tries_used INTEGER CHECK (challenge_tries_used >= 0),
    decline_code TEXT CHECK (decline_code ~ '^[A-Z][A-Z0-9_]{1,63}$'),
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    captured_at TIMESTAMPTZ,
    provider_payment_id TEXT UNIQUE CHECK (length(provider_payment_id) BETWEEN 1 AND 128),
    recorded_on_order_at TIMESTAMPTZ,
    refund_id TEXT UNIQUE CHECK (length(refund_id) BETWEEN 1 AND 128),
    refunded_at TIMESTAMPTZ,
    row_version INTEGER NOT NULL CHECK (row_version >= 1),
    CONSTRAINT ck_payment_attempt_expiry CHECK (expires_at > created_at),
    CONSTRAINT ck_payment_attempt_challenge CHECK (
        (challenge_kind IS NULL) = (challenge_max_tries IS NULL AND challenge_tries_used IS NULL)
        AND coalesce(challenge_tries_used <= challenge_max_tries, TRUE)),
    -- A capture time and a payment ID come together, and only with a captured (or later refunded) payment; the same
    -- holds for a refund time and ID.
    CONSTRAINT ck_payment_attempt_captured_pair CHECK ((captured_at IS NULL) = (provider_payment_id IS NULL)),
    CONSTRAINT ck_payment_attempt_captured CHECK ((status IN ('CAPTURED', 'REFUNDED')) = (captured_at IS NOT NULL)),
    CONSTRAINT ck_payment_attempt_refunded_pair CHECK ((refunded_at IS NULL) = (refund_id IS NULL)),
    CONSTRAINT ck_payment_attempt_refunded CHECK ((status = 'REFUNDED') = (refunded_at IS NOT NULL)),
    CONSTRAINT ck_payment_attempt_recorded CHECK (recorded_on_order_at IS NULL OR captured_at IS NOT NULL),
    -- An attempt that did not succeed says why, and one that did not fail has no reason.
    CONSTRAINT ck_payment_attempt_decline CHECK (
        (status IN ('DECLINED', 'AUTHENTICATION_FAILED', 'EXPIRED', 'FAILED')) = (decline_code IS NOT NULL))
);

CREATE INDEX ix_payment_attempt_order ON gateway.payment_attempt (order_id, created_at);
CREATE INDEX ix_payment_attempt_pending ON gateway.payment_attempt (expires_at)
    WHERE status IN ('STARTED', 'AWAITING_AUTHENTICATION');
CREATE INDEX ix_payment_attempt_unrecorded ON gateway.payment_attempt (captured_at)
    WHERE status IN ('CAPTURED', 'REFUNDED') AND recorded_on_order_at IS NULL;
CREATE INDEX ix_payment_attempt_captured_at ON gateway.payment_attempt (captured_at) WHERE captured_at IS NOT NULL;
CREATE INDEX ix_payment_attempt_refunded_at ON gateway.payment_attempt (refunded_at) WHERE refunded_at IS NOT NULL;

-- An order is paid once: at most one of its attempts is ever captured (or refunded after being captured).
CREATE UNIQUE INDEX ux_payment_attempt_one_capture ON gateway.payment_attempt (order_id)
    WHERE status IN ('CAPTURED', 'REFUNDED');

CREATE TABLE gateway.payment_attempt_event (
    attempt_id UUID NOT NULL REFERENCES gateway.payment_attempt (attempt_id),
    sequence INTEGER NOT NULL CHECK (sequence >= 1),
    from_status TEXT,
    to_status TEXT NOT NULL REFERENCES gateway.payment_attempt_status (status),
    occurred_at TIMESTAMPTZ NOT NULL,
    reason TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 64 AND reason !~ '[0-9]([ -]?[0-9]){12}'),
    PRIMARY KEY (attempt_id, sequence),
    CONSTRAINT ck_payment_attempt_event_first CHECK ((sequence = 1) = (from_status IS NULL)),
    CONSTRAINT ck_payment_attempt_event_creation CHECK (sequence <> 1 OR to_status = 'STARTED'),
    CONSTRAINT fk_payment_attempt_event_transition FOREIGN KEY (from_status, to_status)
        REFERENCES gateway.payment_attempt_transition (from_status, to_status)
);

-- Each event continues from the one before it.
CREATE FUNCTION gateway.assert_attempt_event_continues_history() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    previous RECORD;
BEGIN
    IF NEW.sequence = 1 THEN
        RETURN NEW;
    END IF;

    SELECT sequence, to_status INTO previous
      FROM gateway.payment_attempt_event
     WHERE attempt_id = NEW.attempt_id
     ORDER BY sequence DESC
     LIMIT 1;

    IF NOT FOUND OR previous.sequence <> NEW.sequence - 1 OR previous.to_status <> NEW.from_status THEN
        RAISE EXCEPTION 'payment attempt % event % does not continue from the previous event', NEW.attempt_id, NEW.sequence
            USING ERRCODE = 'PA001';
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER payment_attempt_event_continues_history
    BEFORE INSERT ON gateway.payment_attempt_event
    FOR EACH ROW EXECUTE FUNCTION gateway.assert_attempt_event_continues_history();

-- At commit, an attempt's status is where its history ends.
CREATE FUNCTION gateway.assert_attempt_matches_history() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    last_status TEXT;
BEGIN
    SELECT to_status INTO last_status
      FROM gateway.payment_attempt_event
     WHERE attempt_id = NEW.attempt_id
     ORDER BY sequence DESC
     LIMIT 1;

    IF last_status IS DISTINCT FROM NEW.status THEN
        RAISE EXCEPTION 'payment attempt % is % but its history ends at %', NEW.attempt_id, NEW.status, last_status
            USING ERRCODE = 'PA001';
    END IF;

    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER payment_attempt_matches_history
    AFTER INSERT OR UPDATE ON gateway.payment_attempt
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION gateway.assert_attempt_matches_history();

-- What an attempt is for never changes, a finished attempt is final, a captured payment's identity is fixed, and each
-- change raises the version by one.
CREATE FUNCTION gateway.guard_payment_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.attempt_id <> OLD.attempt_id OR NEW.order_id <> OLD.order_id OR NEW.client_id <> OLD.client_id
       OR NEW.method <> OLD.method OR NEW.amount_asset_key <> OLD.amount_asset_key OR NEW.amount <> OLD.amount
       OR NEW.instrument_label <> OLD.instrument_label
       OR NEW.instrument_brand IS DISTINCT FROM OLD.instrument_brand
       OR NEW.instrument_last4 IS DISTINCT FROM OLD.instrument_last4
       OR NEW.created_at <> OLD.created_at OR NEW.expires_at <> OLD.expires_at THEN
        RAISE EXCEPTION 'what a payment attempt is for never changes' USING ERRCODE = 'PA002';
    END IF;

    IF (SELECT is_terminal FROM gateway.payment_attempt_status WHERE status = OLD.status) THEN
        RAISE EXCEPTION 'payment attempt % is over (%) and stays as it is', OLD.attempt_id, OLD.status USING ERRCODE = 'PA002';
    END IF;

    IF (OLD.captured_at IS NOT NULL AND (
            NEW.captured_at IS DISTINCT FROM OLD.captured_at
            OR NEW.provider_payment_id IS DISTINCT FROM OLD.provider_payment_id
            OR NEW.rail_reference IS DISTINCT FROM OLD.rail_reference))
       OR (OLD.recorded_on_order_at IS NOT NULL AND NEW.recorded_on_order_at IS DISTINCT FROM OLD.recorded_on_order_at) THEN
        RAISE EXCEPTION 'a captured payment''s identity never changes' USING ERRCODE = 'PA002';
    END IF;

    IF NEW.row_version <> OLD.row_version + 1 THEN
        RAISE EXCEPTION 'payment attempt % changed without raising its version by one', OLD.attempt_id USING ERRCODE = 'PA002';
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER payment_attempt_guard
    BEFORE UPDATE ON gateway.payment_attempt
    FOR EACH ROW EXECUTE FUNCTION gateway.guard_payment_attempt();

-- Attempts and their history are evidence: never deleted, and events never changed.
CREATE TRIGGER payment_attempt_no_delete BEFORE DELETE ON gateway.payment_attempt
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER payment_attempt_no_truncate BEFORE TRUNCATE ON gateway.payment_attempt
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER payment_attempt_event_append_only BEFORE UPDATE OR DELETE ON gateway.payment_attempt_event
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER payment_attempt_event_no_truncate BEFORE TRUNCATE ON gateway.payment_attempt_event
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

INSERT INTO ops.schema_migration (version) VALUES ('012_payment_attempts');

COMMIT;
