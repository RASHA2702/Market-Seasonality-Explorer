-- Merchants' settlement wallets and their history. One transaction.

BEGIN;

CREATE TABLE gateway.merchant_settlement (
    client_id TEXT PRIMARY KEY REFERENCES gateway.api_client (client_id),
    row_version INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0)
);

CREATE TABLE gateway.settlement_wallet (
    wallet_id UUID PRIMARY KEY,
    -- Registration order, also between wallets registered at the same instant.
    wallet_number BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
    client_id TEXT NOT NULL REFERENCES gateway.merchant_settlement (client_id),
    network TEXT NOT NULL CHECK (network ~ '^[a-z0-9-]{2,32}$'),
    address TEXT NOT NULL CHECK (btrim(address) <> '' AND length(address) <= 128),
    label TEXT CHECK (length(label) <= 100),
    status TEXT NOT NULL CHECK (status IN ('APPROVED', 'UNDER_REVIEW', 'REJECTED', 'DISABLED')),
    screening_risk TEXT NOT NULL CHECK (screening_risk IN ('LOW', 'HIGH', 'BLOCKED', 'UNKNOWN')),
    screener TEXT NOT NULL CHECK (btrim(screener) <> ''),
    screening_reference TEXT,
    registered_at TIMESTAMPTZ NOT NULL,
    usable_from TIMESTAMPTZ NOT NULL CHECK (usable_from >= registered_at),
    retired_at TIMESTAMPTZ,
    -- A wallet on a sanctions or block list can never be approved.
    CONSTRAINT ck_settlement_wallet_not_blocked CHECK (status <> 'APPROVED' OR screening_risk <> 'BLOCKED')
);
CREATE INDEX ix_settlement_wallet_client ON gateway.settlement_wallet (client_id, registered_at, wallet_number);
CREATE INDEX ix_settlement_wallet_review ON gateway.settlement_wallet (registered_at) WHERE status = 'UNDER_REVIEW';

-- Only the status and the retirement time of a wallet can change, and a closed wallet stays closed: its address
-- and screening are what was checked, so they are never edited.
CREATE FUNCTION gateway.guard_settlement_wallet() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.client_id <> OLD.client_id OR NEW.network <> OLD.network OR NEW.address <> OLD.address
       OR NEW.label IS DISTINCT FROM OLD.label OR NEW.registered_at <> OLD.registered_at
       OR NEW.usable_from <> OLD.usable_from OR NEW.screening_risk <> OLD.screening_risk
       OR NEW.screener <> OLD.screener OR NEW.screening_reference IS DISTINCT FROM OLD.screening_reference THEN
        RAISE EXCEPTION 'settlement wallet %: only its status and retirement can change', OLD.wallet_id
            USING ERRCODE = 'LG004';
    END IF;
    IF OLD.status IN ('REJECTED', 'DISABLED') AND NEW.status <> OLD.status THEN
        RAISE EXCEPTION 'settlement wallet % is closed', OLD.wallet_id USING ERRCODE = 'LG004';
    END IF;
    IF OLD.status = 'APPROVED' AND NEW.status NOT IN ('APPROVED', 'DISABLED') THEN
        RAISE EXCEPTION 'approved settlement wallet % can only be disabled', OLD.wallet_id USING ERRCODE = 'LG004';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER settlement_wallet_guard BEFORE UPDATE ON gateway.settlement_wallet
    FOR EACH ROW EXECUTE FUNCTION gateway.guard_settlement_wallet();
CREATE TRIGGER settlement_wallet_no_delete BEFORE DELETE ON gateway.settlement_wallet
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER settlement_wallet_no_truncate BEFORE TRUNCATE ON gateway.settlement_wallet
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

CREATE TABLE gateway.settlement_wallet_action (
    wallet_id UUID NOT NULL REFERENCES gateway.settlement_wallet (wallet_id),
    sequence INTEGER NOT NULL CHECK (sequence >= 1),
    action TEXT NOT NULL CHECK (action IN ('REGISTERED', 'APPROVED', 'REJECTED', 'DISABLED')),
    actor TEXT NOT NULL CHECK (btrim(actor) <> ''),
    reason TEXT CHECK (length(reason) <= 2000),
    occurred_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (wallet_id, sequence),
    -- A reviewer's approval or rejection always says why.
    CONSTRAINT ck_settlement_wallet_action_reason CHECK (action NOT IN ('APPROVED', 'REJECTED') OR btrim(coalesce(reason, '')) <> '')
);
CREATE TRIGGER settlement_wallet_action_append_only BEFORE UPDATE OR DELETE ON gateway.settlement_wallet_action
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER settlement_wallet_action_no_truncate BEFORE TRUNCATE ON gateway.settlement_wallet_action
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

INSERT INTO ops.schema_migration (version) VALUES ('008_settlement_wallets');

COMMIT;
