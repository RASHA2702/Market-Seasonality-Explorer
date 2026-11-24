# Change log

## 2026-11-24 — Signed merchant webhooks (milestone item 5, part 1)

- Merchants receive a signed `order.status_changed` notification for every status change they may see. Each
  notification is written in the same transaction as the order change (transactional outbox), claimed with
  `FOR UPDATE SKIP LOCKED` and a lease so two instances never send the same one, sent in order per order, and
  retried with exponential backoff before being recorded as given up. Migration `005_merchant_webhooks`.
- Each merchant client takes `WebhookUrl` (HTTPS, or HTTP to localhost) and `WebhookSecret` (at least 32
  characters, from an environment variable or secret store). Invalid settings stop startup, and so do delivery
  settings under which a lease could expire mid-batch.
- `GET /v1/review/orders/{id}/webhooks` shows reviewers each notification's delivery state.
- The merchant view now numbers events 1, 2, 3 ... on its own: gaps in the internal numbering could have
  shown that hidden compliance steps happened.
- The API's service registration moved into `AddGatewayApi`, and the startup-settings tests now run the
  host's own checks on those registrations without starting a host. Starting a host that is meant to fail
  raced with its disposal on another thread and made those tests fail rarely (`ObjectDisposedException`).

## 2026-11-24 — Merchants no longer see compliance detail

- Fixes a tipping-off risk introduced with the review queue: the merchant order API showed statuses such as
  `Frozen` and reasons such as `FROZEN_AND_REPORTED`. Merchants now see one `OnHold` step for review, rejection
  and freezing, and `REFUND_STARTED` for a release. Reviewers still see everything.

## 2026-11-24 — Live-credential guard and partner design (milestone item 4, part)

- `LiveCredentialGuard` refuses, at startup, any provider configured for a non-test environment, a non-test
  or non-HTTPS URL, a live-looking key, or a provider with no installed adapter. Messages never show key
  values. Provider settings live at `Gateway:Providers:<name>`.
- `docs/partner-integration.md`: the proposed partner interface, how the three flows map onto it, questions
  for each candidate, and the environment variables to set. The partner adapter itself waits for the choice
  of partner (decision 11).

## 2026-11-24 — Cumulative limits and country rule (milestone item 3)

- Per-customer limits over a rolling 24 hours and 30 days for each KYC level, on top of the per-payment limit
  (placeholders: basic 1,000 / 2,000 / 5,000 USD, full 10,000 / 20,000 / 50,000 USD). Expired and refunded
  orders do not count. New rejection codes `DAILY_LIMIT_EXCEEDED` and `MONTHLY_LIMIT_EXCEEDED`.
- Order creation is serialised per customer across instances, so simultaneous orders cannot exceed a limit;
  a test races three orders on two instances.
- The hard-coded blocked list is replaced by `Gateway:Compliance:CountryMode` (`AllowList` by default) and
  `Gateway:Compliance:Countries`, which replaces the default (India only) instead of adding to it.
  `BlockedCountries` is gone.
- Compliance settings are validated at startup. Sandbox customers are now Indian residents, plus
  `cust_foreign` (United Kingdom). Migration `004_customer_limits.sql` adds an index for the totals.

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
