# Change log

## 2026-11-24 — Reviewer queue and audit trail (milestone item 2)

- Reviewer API keys (`Role: Reviewer`) and the `/v1/review` endpoints: a queue of held orders, longest-waiting
  first, with screening reasons and a 24-hour overdue flag; approve, reject, freeze and release decisions.
- Money from a rejected order is now frozen and reported (decision: freeze and report). The new `Frozen` state
  records the report reference; a refund needs a later, reasoned release. The direct rejected-to-refund path
  is gone, in code and in the database (migration `003_review_queue.sql`).
- Every review action is kept in an append-only log with reviewer, reason and time; compliance decisions are
  append-only too.
- The whole-ledger balance view moved to `/v1/review/ledger/balances`, because it shows every merchant.
- The check that the database's state machine matches the code now runs against a real migrated database.

## 2026-11-24 — PostgreSQL storage (milestone item 1)

- New `FiatCryptoGateway.Persistence.Postgres` project: PostgreSQL stores for quotes, orders, order history,
  compliance decisions, the ledger, idempotency keys and the webhook inbox.
- Each order step is now saved in one transaction with the journal entries it caused. Previously the ledger
  entry and the state change were two separate writes, so a crash between them could have recorded an
  entry twice on retry.
- Safe with several API instances: a per-order advisory lock across the provider call, plus a row lock and
  version check on save. A test races two instances on one order.
- Migration `002_persistence.sql`; the API can apply migrations at startup. Every compliance decision,
  including the screening before payment, is kept in order and cannot be changed.
- Storage is selected with `Gateway:Storage:Provider` (`InMemory` by default, or `Postgres`).

## 2026-11-24 — Sandbox foundation

- .NET 10 solution: Domain, Application, Sandbox and API projects, with unit and API tests.
- Assets: all 156 circulating ISO 4217 currencies (list of 2026-01-01) with official minor units; crypto
  assets identified by symbol and network, with the asset their network fees are paid in.
- Money with explicit rounding directions; quotes with fresh, no-look-ahead price routes, fee, spread,
  network fee, limits and expiry.
- On-ramp order state machine with refunds for late or wrong payments, manual review and failure states.
- Double-entry ledger balanced per asset; postings for payment, purchase, send and refund.
- Compliance rules (KYC level and limits, blocked countries, sanctions, wallet risk, review threshold)
  that fail closed and avoid tipping off.
- HTTP API: API-key authentication with hashed keys, idempotent order creation, signed and deduplicated
  payment webhooks, per-client isolation, sandbox simulators, background processing worker.
- Sandbox-only safety gate: production settings stop startup.
- PostgreSQL schema enforcing the ledger, state-machine and uniqueness rules, with a migration runner and
  constraint tests; Docker Compose for local development; CI with .NET, database and secret-scan jobs.
- No real provider, credential or money is involved.
