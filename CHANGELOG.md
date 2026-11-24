# Change log

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
