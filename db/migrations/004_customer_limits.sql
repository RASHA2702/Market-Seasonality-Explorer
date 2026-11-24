-- Cumulative per-customer limits look up a customer's recent orders. One transaction.

BEGIN;

CREATE INDEX ix_payment_order_customer_recent ON gateway.payment_order (client_id, customer_id, created_at DESC);

INSERT INTO ops.schema_migration (version) VALUES ('004_customer_limits');

COMMIT;
