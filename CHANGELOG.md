# Change log

## 2026-11-27 — Our own gateway, milestone 1 (part 2): the payment processor

- **The gateway now collects rupees itself.** A payment processor takes what a customer typed, asks the simulated card
  or UPI network, handles the bank's check (a one-time code, or approval in a UPI app), takes the money and tells the
  order. `Gateway:Fiat:Provider` chooses `Processor` (the default) or `SandboxPartner`, the earlier simulated partner,
  kept for its tests. Only the chosen one is reconciled.
- **One live attempt per order, and one charge.** Starting a new attempt ends the one still waiting; no new attempt
  can start while an approved payment is being completed; an order allows 10 attempts; an attempt never outlives the
  order's own deadline. Two customers (or two gateway instances) paying one order at once are charged once.
- **A crash or outage never loses a payment or books it twice.** Each step is saved before the next outside call. A
  background sweep ends attempts that ran out of time, captures payments the network approved, and records captured
  payments on their orders, checking first whether the order already has them. An approval that cannot be captured
  for 24 hours is given up and flagged. A customer whose payment was taken while the exchange was down still sees it
  as paid.
- **Two locks, never held together.** The order's payment lock is held while the network is asked and let go before
  the order is told; refunds take no payment lock. A test fails on any nesting.
- **Refunds and the daily report.** Refunds go back through the processor, the same refund when repeated. The
  processor's own records feed the daily report that reconciliation compares with the books; a payment the books do
  not have yet shows as a difference until the sweep records it.
- **Sandbox only, checked at startup.** The API refuses to start if any payment network is not a simulated one, if
  two serve one method, or if the new `Gateway:Processor` settings are out of range.
- The sandbox "pay" shortcuts (the merchant API and the checkout page button) now pay through the processor with the
  test UPI address that is always approved. Wrong-amount and late payments can only be simulated with `SandboxPartner`.
- A small migration (013) adds an index for finding approved payments that were not captured.

## 2026-11-27 — Our own gateway, milestone 1 (part 1): payment attempts and test networks

- Every try to pay an order is now a **payment attempt** with its full history: started, waiting for the customer's
  bank check, approved, captured, declined, expired, refunded. Only legal steps are allowed, a finished attempt never
  changes, and an order can have only one captured payment (the database refuses a second one).
- **No card number or security code is ever stored.** An attempt keeps the brand and last four digits (or the UPI
  address). The code refuses anything that looks like a card number, and so does the database, a second time.
- Two simulated payment networks, one for cards and one for UPI, take **test numbers only**. A real card number is
  refused even if it is valid, and the refusal never repeats what was typed.
- Attempts are saved in PostgreSQL (migration 012) together with their history, in one transaction. A stale copy
  cannot overwrite a newer one, and a reused attempt, payment or refund identifier is refused loudly instead of being
  mistaken for a lost race.
- Nothing uses these yet: the processor that ties attempts to orders and the checkout forms come next in this milestone.
- README test counts corrected: 574 .NET tests (36 against PostgreSQL) and 70 database checks.

## 2026-11-27 — Direction: build our own payment gateway

- Decision recorded: instead of waiting for a payment partner (question 11), we build our own payment gateway as a
  sample project, sandbox only, and scale it as a startup later. [docs/own-gateway.md](docs/own-gateway.md) holds the
  plan: what it means, what we keep and change, safety rules for payment details (test cards only; card numbers never
  stored), seven milestones, the points for your lawyer and accountant, and the assumptions to confirm.
- The partner design is kept for later. No code changed.

## 2026-11-27 — Backups and alerts in the reviewer console

- The console has a "Backups and alerts" section and two new tiles. "Open problems" counts what the alert check sees
  right now (urgent first, listed in words, checked live even while alert emails are off). "Last backup" shows how long
  ago the last one succeeded, or turns red for a failed or overdue one, with when the last good one was.
- The section lists the last seven backup attempts (result, file, size, the failure note), says whether alert emails
  are on and to how many addresses, and lists the last ten alert emails with what each reported.
- New `GET /v1/review/system` (reviewers only). Backup runs now record their size.
- Console tables keep words and badges whole on a phone and scroll sideways inside their card instead of squeezing
  columns; a tile showing text (such as an age) is no longer mistaken for a count.

## 2026-11-26 — Email alerts

- Every 5 minutes the gateway checks for held payments and new wallets waiting long for review (again, as URGENT,
  when the 24-hour limit passes), reconciliation differences, notifications given up on in the last day, and failed
  or missing backups. It emails when a problem appears or gets worse, reminds after 12 hours while problems stay
  open, and says "All clear" once when everything is resolved.
- Emails go through any SMTP server (MailKit), carry no customer details, and link to the reviewer console. Settings
  are under `Gateway:Alerts` (`To`, `From`, `Smtp:*`, intervals), checked at startup; alerts are off until `To` is set.
- The backup container records every attempt in the database, and every alert email sent is logged, so restarts and
  several instances never repeat an email (migration `011_alerts_and_backup_runs`).

## 2026-11-26 — Encrypted database backups

- A `backup` container in the Compose stack backs up the database every night at 02:30 India time and once at
  start. Each dump is restored into a scratch database and checked (balanced ledger, whole journal entries, schema,
  append-only protections, no missing rows) before it is kept, then encrypted with age to `BACKUP_AGE_RECIPIENTS`;
  the private key stays off the server. Checksums are recorded, local copies pruned after 14 days, and new ones
  optionally copied off-site with rclone (`BACKUP_COPY_TO`).
- `gateway-restore` restores a backup into a new database and checks it, for drills and recovery; it refuses the
  live database, an existing database, a wrong key and a damaged or altered file.
- The container is unhealthy after a failed backup or 26 hours without a success. CI makes a paid order, backs up,
  restores with a throwaway key and compares the copy with the live database.

## 2026-11-26 — Container image and HTTPS proxy

- `docker compose --profile gateway up -d --build --wait` starts PostgreSQL, the API in a container and an HTTPS
  proxy (Caddy) in front of it. Caddy gets and renews certificates by itself (a local authority for localhost, Let's
  Encrypt for a real domain), redirects HTTP, sets HSTS, caps request sizes, keeps checkout links out of its log, and
  answers the reviewer console and review API only from configured networks (private ones by default).
- The image runs on Microsoft's chiseled runtime with time zone data: no shell, non-root, read-only file system, no
  Linux capabilities, versions pinned. It checks its own health with `--health-check`.
- New setting `Gateway:Proxy:TrustedNetworks`: the API takes a client's address from `X-Forwarded-For` only when the
  request comes from these networks, so sign-in limits apply per real client and cannot be dodged by sending the
  header. Without it the header is ignored.
- `deploy/smoke-test.sh` checks a running stack from outside; CI builds the image, starts the stack and runs it on
  every push.

## 2026-11-26 — Reviewer sign-in with two-factor authentication

- Reviewers now have their own accounts and sign in with a password and a 6-digit code from an authenticator app.
  Reviewer API keys are no longer accepted: an API client with `Role=Reviewer` stops the API at startup, with a
  message pointing to the new setup.
- `dotnet run --project src/FiatCryptoGateway.ReviewerSetup` creates an account's settings on the reviewer's own
  computer: it shows the authenticator QR code, checks one code, and prints the username, display name, password hash
  (PBKDF2-SHA256, 600,000 iterations) and authenticator key to set under `Gateway:Review:Reviewers`.
- Every failure looks the same; five in a row pause sign-in for 15 minutes; each code works once, across instances;
  sign-in requests are rate-limited per address. Sessions end on sign-out, after 30 idle minutes or 12 hours, or when
  the account's settings change. Sign-ins, failures, pauses and sign-outs are logged (append-only with PostgreSQL,
  migration `010_reviewer_access`), and the console shows the previous sign-in and any failed attempts since.
- New endpoints: `POST /v1/review/sign-in` and `POST /v1/review/sign-out`. Review decisions are now recorded under
  the reviewer's username.
- Fixed: the console showed its Refresh and Sign out buttons before sign-in.

## 2026-11-26 — Reviewer console

- `/review` is one page for the compliance officer: held payments to approve, reject or freeze and report; frozen
  payments to release for refund; settlement wallets waiting for review; reconciliation differences, with a button
  to reconcile a finished day; merchant notifications given up on in the last 7 days; and whether the ledger
  balances. Each section shows a count, and held payments past 24 hours are flagged.
- It signs in with a reviewer key and works only through the reviewer API, so the same role check, reasons and
  review log apply. The key stays in the page's memory (no cookie, no storage), and the page signs out after 30
  minutes without activity. It loads nothing from other sites, cannot be framed, and no form submits by itself.
  `Gateway:Review:ConsoleEnabled=false` turns it off.
- New reviewer endpoints: `GET /v1/review/frozen`, `GET /v1/review/webhooks/failed` and
  `POST /v1/review/webhooks/{eventId}/retry`, which gives a given-up notification one more attempt with its
  original ID. Migration `009_failed_deliveries` adds the index behind the failed list.

## 2026-11-25 — Hosted checkout page

- Every order's `checkoutUrl` now links to a page the gateway hosts. The customer sees the merchant, the amount in
  rupees (Indian digit grouping) and the deadline in IST, chooses UPI, card, netbanking or bank transfer, and pays;
  in the sandbox the payment is simulated through the same signed provider event a real one produces. The page then
  shows the result and refreshes itself while a payment or refund is under way.
- Customers see only waiting, received, refund started, refunded or expired. Compliance holds look like any
  received payment. An unpaid order past its deadline is shown as expired and takes no payment.
- Links are HMAC-signed order IDs (no storage, no cookies). The page loads nothing from other sites, uses a strict
  content security policy with per-response nonces, and is never cached, framed, indexed or sent as a referrer.
- New settings: `Gateway:Checkout` (`BaseUrl`, `LinkSecret`, `TimeZone`) and, per merchant, `DisplayName` and
  `CheckoutReturnUrl`; all checked at startup.

## 2026-11-25 — Merchants are notified of settlement wallet changes

- Every registration, review decision and withdrawal of a settlement wallet sends the merchant a signed
  `settlement_wallet.status_changed` notification, written in the same transaction as the change. A merchant thus
  learns of a wallet it did not register while it is still cooling off. Notifications carry the merchant's statuses
  only, never screening results or a reviewer's reasons.
- The outbox now holds notifications for any aggregate (orders and wallets), each delivered in order.
  `GET /v1/review/settlement-wallets/{id}/webhooks` shows a wallet's deliveries.

## 2026-11-25 — Orders pay out only to registered wallets

- An order's crypto now goes to the merchant's active settlement wallet for the quote's network. Without one (none
  registered, or still cooling off or under review) the order is refused with `NO_SETTLEMENT_WALLET`. The order API
  no longer takes `destinationAddress`; a request that sends one is refused with `DESTINATION_NOT_ACCEPTED`.
- Each order screens the wallet again, so a wallet flagged after its approval stops receiving payments.
- This closes a gap from the consumer on-ramp model, where any API caller could name any destination per order.

## 2026-11-25 — Merchant settlement wallets

- Each merchant registers the wallet it is paid into, one per network (`/v1/settlement/wallets`). Every new wallet
  is screened. A clean one can receive payments only after a 24-hour cooling-off period, so a merchant can disable a
  wallet it did not add (for example one added with a stolen key); a risky one, or one whose screening could not
  finish, waits for a reviewer; a sanctioned one can never be approved. Merchants see neutral statuses only.
- A replacement wallet takes over the moment it becomes usable; withdrawing it before then leaves the old wallet in
  use. Only one change per network can be waiting at a time.
- Reviewers see waiting wallets with their screening and decide at `/v1/review/settlement-wallets`, each decision
  with a reason. Wallet history is append-only, and the database refuses edits to a stored wallet's address or
  screening, reopening a closed wallet, and approving a blocked one (migration `008_settlement_wallets`).
- Settings under `Gateway:Settlement` (`CoolingOffHours`, `Networks`) are checked at startup.

## 2026-11-25 — Launch stablecoins in the sandbox

- The sandbox now has every stablecoin chosen in question 13: USDT on Tron, and USDT and USDC on Polygon,
  Ethereum and Solana, with the native coins that pay their network fees (TRX, POL). Tron and Polygon addresses
  are checked in their own formats. Prices, fees and confirmation counts are illustrative.

## 2026-11-25 — Daily reconciliation (milestone item 5, part 3)

- Every morning after 06:00 India time, each provider's settlement report for the previous day is compared with
  the books by the provider's own IDs, and the day before is checked again so a movement reported a day late
  clears. Differences (missing in the books, missing in the report, amount mismatch, duplicates) are recorded for
  a person to check; the books are never changed automatically.
- Reviewers can reconcile a finished day on demand and see past runs: `POST` and `GET /v1/review/reconciliation/runs`.
- The sandbox payment provider keeps its own record of captures and refunds and publishes a daily report from it,
  so a payment whose webhook never arrives shows up as `MISSING_IN_LEDGER`.
- Runs and their discrepancies are stored append-only (migration `007_reconciliation`). Settings under
  `Gateway:Reconciliation` are checked at startup.

## 2026-11-24 — Provider references on ledger entries (milestone item 5, part 2)

- Each journal entry that records a provider's money movement now carries the provider's own record of it:
  provider, the provider's ID (payment, refund, execution or transaction), the amount and the provider's time.
  This is what daily reconciliation matches against provider reports.
- The database refuses to book the same provider movement twice for the same kind of entry, so a payment can
  never be credited twice even if a bug tried (migration `006_ledger_references`). Entries written before this
  migration keep no reference.

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
