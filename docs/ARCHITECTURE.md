# Architecture

## Principles

1. **No real money until the business is ready for it.** The build is sandbox-only and refuses to
   start with `Gateway:Mode=Production` or `Gateway:AllowRealFundsMovement=true`.
2. **Money is exact.** `decimal` everywhere, every amount at its asset's scale, and every rounding
   has an explicit direction.
3. **Every movement is double-entry.** The ledger balances separately for every asset and is append-only.
4. **Fail closed.** A screening outage holds an order for a person; an unknown wallet risk is never
   treated as low; a late or wrong payment is refunded, not converted at a stale price.
5. **At-least-once outside, exactly-once inside.** Providers retry webhooks and clients retry requests,
   so webhook events, order creation and every provider call are idempotent.
6. **Partners behind interfaces.** Each external party is a port in `Application/Ports`. The sandbox
   implements them all; a real adapter (payment provider, exchange, custodian, KYC vendor) implements
   the same interface.

## Components

| Port | Sandbox implementation | Real-world counterpart |
|---|---|---|
| `IPriceFeed` | Fixed illustrative prices for 14 fiat currencies and 6 crypto assets | Exchange or FX rate feeds |
| `IFiatPaymentProvider` | Fake checkout sessions, signed webhooks, refunds | Card acquirer, bank-transfer or local-rail provider |
| `ILiquidityProvider` | Fills at the sandbox market price | Exchange or OTC desk |
| `ICustodyProvider` | Format-checks addresses, fakes broadcasts | Custodian or self-custody wallet service |
| `ICustomerDirectory` | Five seeded test customers | KYC/KYB vendor |
| `IWalletScreening` | Three special addresses | Blockchain-analytics vendor |
| Stores | In memory | PostgreSQL (`db/migrations`) |

## Order lifecycle (on-ramp)

| From | To | When |
|---|---|---|
| AwaitingFiatPayment | FiatReceived | Fiat captured before the deadline, exact amount and currency |
| AwaitingFiatPayment | RefundPending | Payment late, or for a different amount or currency |
| AwaitingFiatPayment | Expired | Payment window elapsed without payment |
| Expired | RefundPending | A payment arrives after expiry |
| FiatReceived | ReadyToSettle | Screening approves |
| FiatReceived | ComplianceReview | Screening needs a person (or the screening provider is down) |
| FiatReceived, ComplianceReview | ComplianceRejected | Screening or a reviewer rejects; the money is held |
| ComplianceReview | ReadyToSettle | A reviewer approves |
| ComplianceRejected | Frozen | A reviewer freezes the money and records the reference of the report filed |
| Frozen | RefundPending | A reviewer releases the money, with a reason (for example an authority's instruction) |
| ReadyToSettle | CryptoPurchased | The exchange fills |
| CryptoPurchased | CryptoSent | The transfer is broadcast |
| CryptoSent | Completed | Required confirmations reached |
| ReadyToSettle, CryptoPurchased, CryptoSent | Failed | Unrecoverable provider problem; manual handling |
| RefundPending | Refunded | The refund is issued |

Completed, Refunded and Failed are terminal. The same transitions are stored in
`gateway.order_transition`; a unit test fails if the C# and SQL versions differ.

## Money rules

- Fiat scale comes from ISO 4217 (list published 2026-01-01, embedded in `Domain/Assets/iso4217.csv`).
- Fees the gateway collects round **up**; crypto it delivers rounds **down**, so it never promises a
  fraction it cannot pay.
- Order limits are in the pivot currency (USD by default) so they mean the same in every currency.
- Prices carry `observedAt` and `availableAt`; a quote never uses a price before it was available or
  older than `MaximumPriceAgeSeconds` (30 s by default).

### Worked example (sandbox prices)

10,000 INR into USDC on Ethereum, 1.5% fee, 0.5% spread, INR→USD 0.0118:

| Step | Value |
|---|---|
| Service fee: 10,000 × 1.5%, rounded up to paise | 150.00 INR |
| Converted | 9,850.00 INR |
| Customer rate: 0.0118 × (1 − 0.5%) | 0.011741 USDC per INR |
| Bought for the customer: 9,850 × 0.011741, rounded down to 6 decimals | 115.648850 USDC |
| Network fee charged | 1.500000 USDC |
| Delivered | 114.148850 USDC |

## Ledger postings for that order

| Entry | Debit | Credit |
|---|---|---|
| FIAT_RECEIVED | psp_clearing 10,000.00 INR | customer_orders 10,000.00 INR |
| CRYPTO_PURCHASED | customer_orders 150.00 INR | service_fees 150.00 INR |
| | customer_orders 9,850.00 INR | liquidity_provider 9,850.00 INR |
| | hot_wallet 116.230000 USDC | customer_orders 115.648850 USDC |
| | | spread 0.581150 USDC |
| CRYPTO_SENT | customer_orders 115.648850 USDC | hot_wallet 114.148850 USDC |
| | | network_fee_recovery 1.500000 USDC |
| | network_fees 0.0004 ETH | hot_wallet 0.0004 ETH |

Each entry balances per asset. After completion `customer_orders` is zero in INR and USDC. Gas is paid
in the network's own asset (ETH), which is why a crypto asset records its `NetworkFeeAsset`. If the
exchange fills worse than quoted, the shortfall becomes a debit to `spread`: the gateway absorbs it,
not the customer.

## Compliance

- Screening runs **before** the customer pays (so money is never collected for an order that cannot
  complete) and again **after** payment, bound to the order ID.
- Outcomes: any rejection reason rejects; otherwise any review reason holds the order; only a clean
  result approves.
- Clients see `KYC_REQUIRED`, `KYC_LIMIT_EXCEEDED`, `DAILY_LIMIT_EXCEEDED`, `MONTHLY_LIMIT_EXCEEDED` or
  `COUNTRY_NOT_SUPPORTED`. Sanctions and wallet findings appear only as `ORDER_NOT_PERMITTED`, so a suspect is
  not tipped off.
- After payment, merchants see review, rejection and freezing as one `OnHold` step (reason `ON_HOLD`), and a
  later release as `REFUND_STARTED`; the reviewer API shows the full detail.
- Money from a rejected order is frozen and reported: a reviewer records the report's reference. It can
  only be refunded later through an explicit, reasoned release. There is no path from rejected straight to
  refund, in the code or in the database.
- Reviewers sign in with their own accounts and two-factor authentication (see Reviewer sign-in). The queue lists held orders longest-waiting first and
  flags any waiting longer than `Gateway:Compliance:ReviewSlaHours` (24). Every decision (approve, reject,
  freeze, release) needs a reason and is kept in an append-only log with the reviewer and time.
- Limits per KYC level, in the pivot currency: per payment, per rolling 24 hours and per rolling 30 days
  (placeholders until your policy sets them; docs/decisions.md, question 19). Expired and refunded orders
  do not count. Order creation is serialised per customer, so simultaneous orders cannot slip past a limit.
- Countries: `Gateway:Compliance:CountryMode` is `AllowList` (default, India only) or `BlockList`, and
  `Gateway:Compliance:Countries` (for example `IN,AE`) replaces the default list.

## Idempotency and webhooks

- `POST /v1/orders` requires `Idempotency-Key`. The same key and body replays the first response
  (`Idempotent-Replayed: true`); the same key with a different body returns `IDEMPOTENCY_KEY_REUSED`.
- Webhook signatures: HMAC-SHA256 over `timestamp.body`, 5-minute tolerance, constant-time comparison,
  several signatures allowed during secret rotation. Each event ID is processed once; if processing
  fails, the claim is released so the provider's retry is not lost.
- Liquidity, custody and refund calls must be idempotent per order ID: a retried send never sends twice.
  The sandbox enforces this and a test reprocesses completed orders to prove it.

## Hosted checkout

- **Signed links.** A checkout link is the order ID plus an HMAC-SHA256 of it under `Gateway:Checkout:LinkSecret`.
  It cannot be guessed or edited to reach another order, needs no storage, and is checked in constant time. It is
  the customer's only credential: there is no login and no cookie, so there is nothing for cross-site request forgery
  to ride on. An unknown and an altered link get the same "not found" page.
- **Customer view.** `CustomerView` maps every order status to what a paying customer may know: waiting, received,
  refund started, refunded, expired. Compliance states and the merchant's settlement all read as "payment received".
  An unpaid order past its deadline is shown as expired at once, and the page takes no payment for it.
- **Page.** One server-rendered, mobile-first document; every value is HTML-encoded. It loads nothing from other
  sites, and its one style and one script carry a per-response nonce under `default-src 'none'`. Responses are not
  cached, framed (`frame-ancestors 'none'`), sniffed, indexed or sent on as a referrer. It works without JavaScript;
  the script only counts down and reloads when the status changes.
- **Paying.** In the sandbox, the Pay button posts to the gateway, which simulates the provider's signed payment
  event, then redirects back to the page (post, redirect, get), so a refresh or a double click pays once. With a
  partner, this step hands over to the partner's payment page or UPI details instead.
- **Return link.** Per merchant, set by the operator (HTTPS only), never taken from a request, so the page cannot be
  used to redirect customers to arbitrary sites.

## Merchant notifications

Merchants are told about every order status change they may see and every change to their settlement wallets,
through the transactional outbox pattern. Each notification belongs to one aggregate (an order or a wallet):

- **Saved with the change.** When an order step or a merchant's settlement settings are saved, a notification for
  each new merchant-visible event is written to `ops.outbox_message` in the same transaction. If the change is
  rolled back or refused (for example a stale copy), no notification exists; if it commits, the notification does.
  A unique key on (aggregate, event type, event number) stops an event from being queued twice.
- **Merchant view only.** Notifications are built from `MerchantView`: review, rejection and freezing appear
  as one `OnHold` step, and the merchant's events are numbered 1, 2, 3 ... on their own, so gaps in the
  internal numbering cannot reveal hidden steps.
- **Claiming.** A background worker claims due messages with `FOR UPDATE SKIP LOCKED` and a lease (it moves
  `next_attempt_at` forward). Two instances never claim the same message, and a message whose sender died
  comes back once the lease ends. A batch is sent one message at a time, so startup refuses a lease shorter
  than `BatchSize x SendTimeoutSeconds`.
- **Order.** Only the earliest undelivered message of each order or wallet can be claimed, so its notifications
  arrive in order. A message that is given up on no longer holds the order back.
- **Retries.** Failures (a non-2xx answer, a redirect, a timeout, a connection error) are retried with
  exponential backoff; after `MaxAttempts` the message is marked as given up with its last error. Delivery is
  at least once; merchants deduplicate on the event ID.
- **Sending again.** A reviewer can return a given-up message to the queue. Only a message that was given up on
  and never delivered can be returned; it keeps its ID and attempt count, so it gets exactly one more attempt. A
  merchant without a webhook URL is recorded as `NO_WEBHOOK_CONFIGURED`, which is not a failure and is not listed.
- **Signing.** Each message is signed when it is sent (not when the batch was claimed), with the merchant's
  own secret, using the same scheme as incoming webhooks.
- **Outbound requests.** Webhook URLs come from the operator's configuration, not from merchants, and must
  be HTTPS (HTTP only to localhost). The HTTP client follows no redirects, keeps no cookies and does not log
  URLs. Before merchants can register their own URLs, outbound requests must also refuse private and
  internal addresses (server-side request forgery).

## Settlement wallets

Each merchant's settlement settings are one unit (`MerchantSettlement`), loaded and saved together with a version
check, and changed under a per-merchant lock on every instance.

- **Screening.** A wallet is screened when it is registered. A clean wallet is approved but can receive payments
  only from `UsableFrom`, the end of a cooling-off period (24 hours by default), so a merchant can disable a wallet
  added without its knowledge, for example with a stolen API key. Any other result, including a screening outage,
  sends the wallet to a reviewer. A blocked (sanctioned) wallet can only be rejected; the database refuses an
  approved blocked wallet too.
- **One wallet per network.** At most one wallet per network is in use, and at most one change per network can be
  waiting. A replacement takes over the moment it becomes usable: the old wallet's `RetiredAt` is set to that
  moment, so there is never a gap or an overlap. If the replacement is withdrawn before it takes over, the old
  wallet simply stays in use; if it is withdrawn after, the network has no wallet until a new one is usable.
- **Evidence.** A wallet's address, network, label and screening never change once stored (a database trigger
  refuses it), a closed wallet stays closed, wallets are never deleted, and every registration, decision and
  withdrawal is kept in an append-only history with who acted and why. Reviewer decisions require a reason.
- **What merchants see.** Neutral statuses only (`CoolingOff`, `UnderReview`, `Active`, `Replaced`,
  `NotAccepted`, `Disabled`); the screening result is for reviewers.
- **Orders.** An order takes its destination from the merchant's active wallet for the quote's network and records
  it on the order; an API request cannot name a destination. The order's compliance screening checks that wallet
  again, before payment and after it, so a wallet flagged since its approval stops receiving payments.

## Reconciliation

- **What is compared.** Each journal entry that records a provider movement carries the provider's own
  record of it: provider, the provider's ID, amount and the provider's time. A provider's settlement report
  lists the same movements. Lines and entries are matched by kind and provider ID.
- **Which day.** Days are calendar days in `Gateway:Reconciliation:TimeZone` (India Standard Time). An entry
  is expected in a day's report if its provider time falls in that day. A report line matches an entry with
  the same ID whatever day the entry falls in, so a movement placed just across midnight by either side still
  matches. The daily run checks yesterday and re-checks the day before, so a movement a provider reports a day
  late clears on the next run.
- **Results.** Each run records its counts and every discrepancy: missing in the books, missing in the report,
  amount or currency mismatch, and duplicates on either side. Runs are append-only evidence; the latest run for a
  day is its current state. Nothing is corrected automatically.
- **Several instances.** A run holds a lock per provider and day, and the daily run skips a day already
  reconciled today, so instances do not repeat each other's work.
- **Sources.** One `ISettlementReportSource` per provider. The sandbox payment provider builds its report from
  its own records of captures and refunds, independent of the gateway's books. A partner adapter will fetch the
  partner's report and map its transaction types to the entry kinds.
- **Not covered yet.** The sandbox exchange and custodian publish no report (in the partner design, the
  partner's report covers purchases and transfers), and provider payouts to the company's bank account are not
  modelled.

## Reviewer console

`GET /review` serves the compliance officer's console: one page, embedded in the API, that holds no data.

- **Same rules as the API.** After sign-in the page calls the reviewer endpoints like any other client, with the
  session token as a bearer token. The `Reviewer` role check, the required reasons, the rule that a sanctioned wallet
  cannot be approved and the append-only review log all apply unchanged; the page adds no authority of its own.
- **The session token.** Held in a JavaScript variable only: never in browser storage, a cookie or an address. With
  no cookie there is nothing for cross-site request forgery to ride on, and reloading or closing the page forgets the
  token. Signing out, or 30 minutes without activity, also ends the session on the server.
- **Lockdown.** The checkout page's headers (`default-src 'none'`, a per-response nonce for the one style and the
  one script, `connect-src 'self'`, `frame-ancestors 'none'`, no caching, no referrer, no indexing), plus
  `form-action 'none'`: no form ever submits by itself, so a typed password can never end up in an address bar or
  a server log. Everything from the API is written into the page as text, never as HTML, so a merchant name, label
  or error message cannot add markup or script.
- **What it shows.** Held and frozen payments, wallets under review, the latest reconciliation run per provider and
  day with its differences, notifications given up on in the last 7 days (listed newest first from a partial index
  on `dead_at`) and whether the ledger balances. `Gateway:Review:ConsoleEnabled=false` turns the page off; the
  API is unaffected.

## Reviewer sign-in

Each reviewer is a person with their own account; there are no reviewer API keys (an API client with the `Reviewer`
role stops startup). Accounts are settings under `Gateway:Review:Reviewers`, made by the setup command
(`src/FiatCryptoGateway.ReviewerSetup`) on the reviewer's own computer.

- **Two factors.** A password, stored only as PBKDF2-HMAC-SHA256 with a random salt and 600,000 iterations (startup
  refuses weaker hashes), and a time-based code (RFC 6238: HMAC-SHA1, 6 digits, 30 seconds) from an authenticator app
  holding a 160-bit key. Codes from one step either side of now are accepted for clock drift, but each step works
  once: the last step used is stored, and using it is an atomic database update, so the same code cannot open two
  sessions even on two instances at the same moment.
- **Failures.** Every failure answers the same `SIGN_IN_FAILED`: unknown username, wrong password or code, a reused
  code or a paused sign-in. An unknown username is checked against a decoy hash, so it takes as long as a real one,
  and what was typed as a username is never logged (it may be a password). Five failures in a row pause sign-in for
  15 minutes; attempts during the pause are logged but do not lengthen it. Before any password is checked, each
  network address may make 10 sign-in requests a minute, which also stops password hashing being used to load the
  server.
- **Sessions.** A session token is `rvs_` and 256 random bits; only its SHA-256 is stored. Requests with a session
  token are authenticated by the session handler, everything else by the API-key handler, and the token is checked
  against the store on every request. A session ends on sign-out, after 30 minutes without use, after 12 hours, or
  when the reviewer's settings are removed or their password hash or authenticator key changes (the session records a
  fingerprint of both). Reviewer principals carry only the `Reviewer` role, so they cannot use merchant endpoints.
- **Evidence.** Sign-ins, failures, pauses, refused attempts and sign-outs go to an append-only log; sessions are
  kept, and the database lets a session only be used and then ended once. A reviewer's decisions are recorded under
  their username. After sign-in the console shows the previous sign-in and how many attempts failed since, so a
  reviewer notices someone else trying.

## Persistence

Storage is chosen with `Gateway:Storage:Provider`: `InMemory` (default; lost on restart, one instance only)
or `Postgres` (connection string from `ConnectionStrings:Gateway`).

- **One transaction per step.** Each change to an order is saved with its new history rows and the
  journal entries it caused, so a crash can never leave a ledger entry without the state change (or the
  reverse). Retrying a step is safe because providers are idempotent per order.
- **Several API instances.** A per-order PostgreSQL advisory lock is held for the whole step, including the
  provider call, so two instances never call a provider for the same order at once. The save itself locks
  the row (`SELECT … FOR UPDATE`) and checks the version the order was loaded at; a stale copy is refused.
  A test runs two instances against one database and checks that crypto is bought and sent only once.
- **Migrations.** The SQL files in `db/migrations` are embedded in the persistence project. The API applies
  pending ones at startup (instances take turns), or `db/migrate.sh` applies them; both record versions in
  `ops.schema_migration`.

The schema enforces in the database what the domain enforces in code:

- journal entries balance per asset (checked at commit) and have at least two lines;
- an entry that records a provider movement names it completely (provider, the provider's ID, amount, time),
  and the same movement can be booked only once per kind of entry;
- ledger and order history are append-only (no update, delete or truncate);
- order events follow allowed transitions and continue from the previous event;
- a quote backs one order, a provider payment settles one order, quote figures add up.


## Deployment

The container setup (`Dockerfile`, `docker-compose.yml`, `deploy/`) puts the API behind an HTTPS proxy:

- **Image.** Built from source with the .NET SDK and run on Microsoft's chiseled ASP.NET Core runtime with time zone
  and culture data (`-chiseled-extra`): no shell or package manager, a non-root user. Image versions are pinned and
  should be raised regularly for security fixes. The container's file system is read-only and it holds no Linux
  capabilities. Its health check runs the API's own `--health-check` mode, which asks `/health` on the local port.
- **Proxy.** Caddy terminates TLS with certificates it obtains and renews itself (a local authority for localhost,
  Let's Encrypt for a domain), redirects HTTP to HTTPS, sets HSTS, removes its `Server` header, caps request bodies
  at 1 MB, and answers the reviewer console and review API only from `GATEWAY_REVIEW_FROM` (private networks by
  default). Its access log replaces checkout links, which are customers' credentials, with `/checkout/[link]`;
  authorisation headers are never logged. Responses are not compressed, so no secret shares a compressed response
  with attacker-chosen text.
- **Client addresses.** Behind the proxy every connection comes from the proxy. The proxy overwrites any
  `X-Forwarded-For` a client sends with the address it saw, and the API reads that header only from addresses in
  `Gateway:Proxy:TrustedNetworks` (the proxy's fixed address in Compose), using only the last entry. With no trusted
  networks configured the API ignores the header entirely; ASP.NET Core would otherwise trust every sender when its
  lists are empty. The sign-in limit therefore counts each real client separately, and no client can pose as another.
- **Networks.** The proxy and API share one network, which can reach the internet (for merchant webhooks). The API
  and database share another with no outside access. The API is not published on the host; the database is, on
  127.0.0.1 only, for local tools.

## Deliberately not built yet

- Off-ramp (crypto → fiat).
- Any real provider adapter, real KYC, real prices.
- An admin UI for merchants and their keys, and for reviewer accounts (all configured through settings for now):
  reviewers changing their own password or authenticator, recovery codes, and a reviewer's role limits (for example
  who may release frozen money).
- Travel Rule data exchange.
- Settlement to a merchant's bank account (flow 3), and proof that a merchant controls a registered wallet
  (for example by signing a message), if the partner or the law requires it.
- Reconciling provider payouts to the company's bank account.
- Merchants registering their own webhook URLs (needs the private-address protection above).
