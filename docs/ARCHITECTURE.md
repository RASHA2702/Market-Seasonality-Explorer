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
- Clients see `KYC_REQUIRED`, `KYC_LIMIT_EXCEEDED` or `COUNTRY_NOT_SUPPORTED`. Sanctions and wallet
  findings appear only as `ORDER_NOT_PERMITTED`, so a suspect is not tipped off.
- Money from a rejected order is frozen and reported: a reviewer records the report's reference. It can
  only be refunded later through an explicit, reasoned release. There is no path from rejected straight to
  refund, in the code or in the database.
- Reviewers use their own API keys (role `Reviewer`). The queue lists held orders longest-waiting first and
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
- ledger and order history are append-only (no update, delete or truncate);
- order events follow allowed transitions and continue from the previous event;
- a quote backs one order, a provider payment settles one order, quote figures add up.


## Deliberately not built yet

- Off-ramp (crypto → fiat) and merchant checkout flows.
- Any real provider adapter, real KYC, real prices.
- Admin and reviewer UI, cumulative limits, Travel Rule data exchange, outbound merchant webhooks,
  reconciliation against provider statements.
