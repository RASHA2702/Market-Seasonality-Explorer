-- Reviewer queue and audit trail. One transaction; records itself in ops.schema_migration.

BEGIN;

-- Money from a rejected order is frozen and reported. A refund needs a later, explicit release.
INSERT INTO gateway.order_status (status, is_terminal) VALUES ('FROZEN', FALSE);
DELETE FROM gateway.order_transition WHERE from_status = 'COMPLIANCE_REJECTED' AND to_status = 'REFUND_PENDING';
INSERT INTO gateway.order_transition (from_status, to_status) VALUES
    ('COMPLIANCE_REJECTED', 'FROZEN'),
    ('FROZEN', 'REFUND_PENDING');

-- Merchants create payments; reviewers decide on held ones.
ALTER TABLE gateway.api_client
    ADD COLUMN role TEXT NOT NULL DEFAULT 'MERCHANT' CHECK (role IN ('MERCHANT', 'REVIEWER'));

-- Every reviewer action: who, what, when and why. Append-only.
CREATE TABLE gateway.review_action (
    order_id UUID NOT NULL REFERENCES gateway.payment_order (order_id),
    sequence INTEGER NOT NULL CHECK (sequence >= 1),
    action TEXT NOT NULL CHECK (action IN ('APPROVE', 'REJECT', 'FREEZE_AND_REPORT', 'RELEASE_FOR_REFUND')),
    reviewer TEXT NOT NULL,
    reason TEXT NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 2000),
    report_reference TEXT,
    occurred_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (order_id, sequence),
    CONSTRAINT ck_review_report_reference CHECK ((action = 'FREEZE_AND_REPORT') = (report_reference IS NOT NULL))
);
CREATE INDEX ix_review_action_reviewer ON gateway.review_action (reviewer, occurred_at DESC);
CREATE TRIGGER review_action_append_only BEFORE UPDATE OR DELETE ON gateway.review_action
    FOR EACH ROW EXECUTE FUNCTION ops.reject_history_change();
CREATE TRIGGER review_action_no_truncate BEFORE TRUNCATE ON gateway.review_action
    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_history_change();

CREATE INDEX ix_payment_order_held ON gateway.payment_order (status, updated_at)
    WHERE status IN ('COMPLIANCE_REVIEW', 'COMPLIANCE_REJECTED');

INSERT INTO ops.schema_migration (version) VALUES ('003_review_queue');

COMMIT;
