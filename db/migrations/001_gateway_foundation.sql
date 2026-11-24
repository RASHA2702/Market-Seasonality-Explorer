-- Gateway foundation: assets, API clients, quotes, orders with an enforced state machine,
-- compliance decisions, an append-only double-entry ledger, idempotency keys, webhook inbox, outbox.
-- Apply with db/migrate.sh. The file is one transaction and records itself in ops.schema_migration.
--
-- Status values are the C# OrderStatus names in UPPER_SNAKE_CASE. A unit test compares the
-- transition table below with PaymentOrder's, so the two cannot drift apart silently.

BEGIN;

CREATE SCHEMA gateway;
CREATE SCHEMA ledger;
CREATE SCHEMA ops;

CREATE TABLE ops.schema_migration (
    version TEXT PRIMARY KEY,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Assets: fiat by ISO 4217 code, crypto by symbol AND network ---------------------------------
CREATE TABLE gateway.asset (
    asset_key TEXT PRIMARY KEY,
    kind TEXT NOT NULL CHECK (kind IN ('FIAT', 'CRYPTO')),
    code TEXT NOT NULL,
    network TEXT,
    scale SMALLINT NOT NULL CHECK (scale BETWEEN 0 AND 18),
    network_fee_asset_key TEXT REFERENCES gateway.asset (asset_key),
    name TEXT NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT ck_asset_identity CHECK (
        (kind = 'FIAT' AND network IS NULL AND network_fee_asset_key IS NULL
            AND code ~ '^[A-Z]{3}$' AND asset_key = code)
        OR
        (kind = 'CRYPTO' AND network ~ '^[a-z0-9-]{2,32}$' AND network_fee_asset_key IS NOT NULL
            AND code ~ '^[A-Z0-9]{2,10}$' AND asset_key = code || '@' || network))
);

-- API clients: only a SHA-256 of each key is stored ---------------------------------------------
CREATE TABLE gateway.api_client (
    client_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    key_sha256 CHAR(64) NOT NULL UNIQUE CHECK (key_sha256 ~ '^[0-9a-f]{64}$'),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at TIMESTAMPTZ
);

-- Quotes: figures must add up, and each quote can back one order -------------------------------
CREATE TABLE gateway.quote (
    quote_id UUID PRIMARY KEY,
    client_id TEXT NOT NULL REFERENCES gateway.api_client (client_id),
    source_asset_key TEXT NOT NULL REFERENCES gateway.asset (asset_key),
    source_amount NUMERIC(38, 18) NOT NULL CHECK (source_amount > 0),
    service_fee NUMERIC(38, 18) NOT NULL CHECK (service_fee >= 0),
    net_source_amount NUMERIC(38, 18) NOT NULL CHECK (net_source_amount > 0),
    destination_asset_key TEXT NOT NULL REFERENCES gateway.asset (asset_key),
    gross_destination_amount NUMERIC(38, 18) NOT NULL CHECK (gross_destination_amount > 0),
    network_fee NUMERIC(38, 18) NOT NULL CHECK (network_fee >= 0),
    destination_amount NUMERIC(38, 18) NOT NULL CHECK (destination_amount > 0),
    -- Rates are unconstrained NUMERIC: a crypto-per-fiat rate can need more than 18 decimals.
    market_rate NUMERIC NOT NULL CHECK (market_rate > 0),
    customer_rate NUMERIC NOT NULL CHECK (customer_rate > 0 AND customer_rate <= market_rate),
    pivot_amount NUMERIC(38, 18) NOT NULL CHECK (pivot_amount > 0),
    -- Every price used, with observed_at, available_at and source, for audit and replay.
    price_path JSONB NOT NULL,
    pricing_policy_version TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    consumed_by_order_id UUID UNIQUE,
    CONSTRAINT ck_quote_source_adds_up CHECK (source_amount = service_fee + net_source_amount),
    CONSTRAINT ck_quote_destination_adds_up CHECK (gross_destination_amount = destination_amount + network_fee),
    CONSTRAINT ck_quote_expiry CHECK (expires_at > created_at)
);

-- Order state machine ----------------------------------------------------------------------------
CREATE TABLE gateway.order_status (
    status TEXT PRIMARY KEY,
    is_terminal BOOLEAN NOT NULL
);

INSERT INTO gateway.order_status (status, is_terminal) VALUES
    ('AWAITING_FIAT_PAYMENT', FALSE),
    ('FIAT_RECEIVED', FALSE),
    ('COMPLIANCE_REVIEW', FALSE),
    ('READY_TO_SETTLE', FALSE),
    ('CRYPTO_PURCHASED', FALSE),
    ('CRYPTO_SENT', FALSE),
    ('COMPLETED', TRUE),
    ('EXPIRED', FALSE),
    ('COMPLIANCE_REJECTED', FALSE),
    ('REFUND_PENDING', FALSE),
    ('REFUNDED', TRUE),
    ('FAILED', TRUE);

CREATE TABLE gateway.order_transition (
    from_status TEXT NOT NULL REFERENCES gateway.order_status (status),
    to_status TEXT NOT NULL REFERENCES gateway.order_status (status),
    PRIMARY KEY (from_status, to_status)
);

INSERT INTO gateway.order_transition (from_status, to_status) VALUES
    ('AWAITING_FIAT_PAYMENT', 'FIAT_RECEIVED'),
    ('AWAITING_FIAT_PAYMENT', 'REFUND_PENDING'),
    ('AWAITING_FIAT_PAYMENT', 'EXPIRED'),
    ('FIAT_RECEIVED', 'READY_TO_SETTLE'),
    ('FIAT_RECEIVED', 'COMPLIANCE_REVIEW'),
    ('FIAT_RECEIVED', 'COMPLIANCE_REJECTED'),
    ('COMPLIANCE_REVIEW', 'READY_TO_SETTLE'),
    ('COMPLIANCE_REVIEW', 'COMPLIANCE_REJECTED'),
    ('READY_TO_SETTLE', 'CRYPTO_PURCHASED'),
    ('READY_TO_SETTLE', 'FAILED'),
    ('CRYPTO_PURCHASED', 'CRYPTO_SENT'),
    ('CRYPTO_PURCHASED', 'FAILED'),
    ('CRYPTO_SENT', 'COMPLETED'),
    ('CRYPTO_SENT', 'FAILED'),
    ('EXPIRED', 'REFUND_PENDING'),
    ('COMPLIANCE_REJECTED', 'REFUND_PENDING'),
    ('REFUND_PENDING', 'REFUNDED');

CREATE TABLE gateway.payment_order (
    order_id UUID PRIMARY KEY,
    client_id TEXT NOT NULL REFERENCES gateway.api_client (client_id),
    customer_id TEXT NOT NULL CHECK (length(customer_id) BETWEEN 1 AND 128),
    quote_id UUID NOT NULL UNIQUE REFERENCES gateway.quote (quote_id),
    destination_address TEXT NOT NULL,
    status TEXT NOT NULL REFERENCES gateway.order_status (status),
    fiat_session_id TEXT,
    checkout_url TEXT,
    payment_deadline TIMESTAMPTZ NOT NULL,
    fiat_provider TEXT,
    fiat_payment_id TEXT,
    transaction_hash TEXT,
    confirmations INTEGER NOT NULL DEFAULT 0 CHECK (confirmations >= 0),
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    -- Optimistic concurrency for deployments with more than one API instance.
    row_version INTEGER NOT NULL DEFAULT 0,
    -- One provider payment can settle one order only.
    CONSTRAINT uq_order_fiat_payment UNIQUE (fiat_provider, fiat_payment_id)
);
CREATE INDEX ix_payment_order_client ON gateway.payment_order (client_id, created_at DESC);
CREATE INDEX ix_payment_order_actionable ON gateway.payment_order (status, updated_at)
    WHERE status IN ('AWAITING_FIAT_PAYMENT', 'FIAT_RECEIVED', 'READY_TO_SETTLE', 'CRYPTO_PURCHASED', 'REFUND_PENDING');

-- Order history: append-only, and every event must follow an allowed edge from the previous one.
CREATE TABLE gateway.order_event (
    order_id UUID NOT NULL REFERENCES gateway.payment_order (order_id),
    sequence INTEGER NOT NULL CHECK (sequence >= 1),
    from_status TEXT,
    to_status TEXT NOT NULL REFERENCES gateway.order_status (status),
    occurred_at TIMESTAMPTZ NOT NULL,
    reason TEXT NOT NULL,
    PRIMARY KEY (order_id, sequence),
    CONSTRAINT ck_order_event_first CHECK ((sequence = 1) = (from_status IS NULL)),
    CONSTRAINT ck_order_event_creation CHECK (sequence <> 1 OR to_status = 'AWAITING_FIAT_PAYMENT'),
    CONSTRAINT fk_order_event_transition FOREIGN KEY (from_status, to_status)
        REFERENCES gateway.order_transition (from_status, to_status)
);

CREATE FUNCTION gateway.assert_event_continues_history() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    previous RECORD;
BEGIN
    IF NEW.sequence = 1 THEN
        RETURN NEW;
    END IF;

    SELECT sequence, to_status INTO previous
      FROM gateway.order_event
     WHERE order_id = NEW.order_id
     ORDER BY sequence DESC
     LIMIT 1;

    IF NOT FOUND OR previous.sequence <> NEW.sequence - 1 OR previous.to_status <> NEW.from_status THEN
        RAISE EXCEPTION 'order % event % does not continue from the previous event', NEW.order_id, NEW.sequence
            USING ERRCODE = 'OR001';
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER order_event_continues_history
    BEFORE INSERT ON gateway.order_event
    FOR EACH ROW EXECUTE FUNCTION gateway.assert_event_continues_history();

-- Compliance decisions. The pre-payment screening runs before the order row exists, so the
-- reference is checked at commit.
CREATE TABLE gateway.compliance_decision (
    decision_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id UUID NOT NULL REFERENCES gateway.payment_order (order_id) DEFERRABLE INITIALLY DEFERRED,
    outcome TEXT NOT NULL CHECK (outcome IN ('APPROVED', 'MANUAL_REVIEW', 'REJECTED')),
    reasons JSONB NOT NULL,
    screener TEXT NOT NULL,
    decided_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_compliance_decision_order ON gateway.compliance_decision (order_id, decided_at);

-- Ledger: append-only double entry, balanced per asset ------------------------------------------
CREATE TABLE ledger.journal_entry (
    entry_id UUID PRIMARY KEY,
    kind TEXT NOT NULL,
    order_id UUID REFERENCES gateway.payment_order (order_id),
    posted_at TIMESTAMPTZ NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ix_journal_entry_order ON ledger.journal_entry (order_id, posted_at);

CREATE TABLE ledger.journal_line (
    entry_id UUID NOT NULL REFERENCES ledger.journal_entry (entry_id),
    line_number SMALLINT NOT NULL CHECK (line_number >= 1),
    account_code TEXT NOT NULL,
    asset_key TEXT NOT NULL REFERENCES gateway.asset (asset_key),
    side TEXT NOT NULL CHECK (side IN ('DEBIT', 'CREDIT')),
    amount NUMERIC(38, 18) NOT NULL CHECK (amount > 0),
    PRIMARY KEY (entry_id, line_number)
);
CREATE INDEX ix_journal_line_account ON ledger.journal_line (account_code, asset_key);

-- Checked at commit: every entry has at least two lines and balances separately for every asset.
CREATE FUNCTION ledger.assert_entry_balanced() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    line_count INTEGER;
    unbalanced RECORD;
BEGIN
    SELECT count(*) INTO line_count FROM ledger.journal_line WHERE entry_id = NEW.entry_id;
    IF line_count < 2 THEN
        RAISE EXCEPTION 'journal entry % has % line(s); at least two are required', NEW.entry_id, line_count
            USING ERRCODE = 'LG001';
    END IF;

    SELECT asset_key, sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) AS difference
      INTO unbalanced
      FROM ledger.journal_line
     WHERE entry_id = NEW.entry_id
     GROUP BY asset_key
    HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0
     LIMIT 1;

    IF FOUND THEN
        RAISE EXCEPTION 'journal entry % does not balance for % (difference %)', NEW.entry_id, unbalanced.asset_key, unbalanced.difference
            USING ERRCODE = 'LG002';
    END IF;

    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER journal_entry_balanced
    AFTER INSERT ON ledger.journal_entry
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION ledger.assert_entry_balanced();

-- Also fires when a line is added to an existing entry in a later transaction.
CREATE CONSTRAINT TRIGGER journal_line_keeps_entry_balanced
    AFTER INSERT ON ledger.journal_line
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION ledger.assert_entry_balanced();

-- History tables are append-only. Corrections are new, reversing entries.
-- (Database roles should also deny UPDATE, DELETE and TRUNCATE to the application user.)
CREATE FUNCTION ops.reject_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only; record a new, reversing row instead', TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME
        USING ERRCODE = 'LG003';
END $$;

CREATE TRIGGER journal_entry_append_only BEFORE UPDATE OR DELETE ON ledger.journal_entry
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER journal_line_append_only BEFORE UPDATE OR DELETE ON ledger.journal_line
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER order_event_append_only BEFORE UPDATE OR DELETE ON gateway.order_event
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER journal_entry_no_truncate BEFORE TRUNCATE ON ledger.journal_entry
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER journal_line_no_truncate BEFORE TRUNCATE ON ledger.journal_line
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER order_event_no_truncate BEFORE TRUNCATE ON gateway.order_event
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

-- Idempotency, webhook inbox, outbox -------------------------------------------------------------
CREATE TABLE gateway.idempotency_key (
    client_id TEXT NOT NULL REFERENCES gateway.api_client (client_id),
    idempotency_key TEXT NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 255),
    request_sha256 CHAR(64) NOT NULL,
    response_status SMALLINT,
    response_body JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ NOT NULL DEFAULT now() + INTERVAL '24 hours',
    PRIMARY KEY (client_id, idempotency_key),
    CONSTRAINT ck_idempotency_completion CHECK (
        (response_status IS NULL) = (completed_at IS NULL) AND (response_status IS NULL) = (response_body IS NULL))
);

CREATE TABLE ops.webhook_inbox (
    provider TEXT NOT NULL,
    event_id TEXT NOT NULL,
    payload_sha256 CHAR(64) NOT NULL,
    received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at TIMESTAMPTZ,
    PRIMARY KEY (provider, event_id)
);

CREATE TABLE ops.outbox_message (
    message_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_type TEXT NOT NULL,
    aggregate_id UUID NOT NULL,
    event_type TEXT NOT NULL,
    payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    published_at TIMESTAMPTZ,
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0)
);
CREATE INDEX ix_outbox_unpublished ON ops.outbox_message (occurred_at) WHERE published_at IS NULL;

INSERT INTO ops.schema_migration (version) VALUES ('001_gateway_foundation');

COMMIT;
