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
| ComplianceRejected | RefundPending | A person decides the money may be refunded |
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
- Rejected-after-payment funds are held. Refunding them, or freezing and reporting them, is a person's
  decision (`ApproveRefundAfterRejection`) because the law may forbid a refund.
- Limits are per order. Cumulative daily or monthly limits are a policy decision (see docs/decisions.md, question 19).

## Idempotency and webhooks

- `POST /v1/orders` requires `Idempotency-Key`. The same key and body replays the first response
  (`Idempotent-Replayed: true`); the same key with a different body returns `IDEMPOTENCY_KEY_REUSED`.
- Webhook signatures: HMAC-SHA256 over `timestamp.body`, 5-minute tolerance, constant-time comparison,
  several signatures allowed during secret rotation. Each event ID is processed once; if processing
  fails, the claim is released so the provider's retry is not lost.
- Liquidity, custody and refund calls must be idempotent per order ID: a retried send never sends twice.
  The sandbox enforces this and a test reprocesses completed orders to prove it.

## Persistence

The sandbox API keeps data in memory (lost on restart). `db/migrations/001_gateway_foundation.sql`
defines the durable schema, enforcing in the database what the domain enforces in code:

- journal entries balance per asset (checked at commit) and have at least two lines;
- ledger and order history are append-only (no update, delete or truncate);
- order events follow allowed transitions and continue from the previous event;
- a quote backs one order, a provider payment settles one order, quote figures add up.

Connecting the application to this schema is the first item of the next milestone.

## Deliberately not built yet

- Off-ramp (crypto → fiat) and merchant checkout flows.
- Any real provider adapter, real KYC, real prices.
- PostgreSQL-backed stores, admin and reviewer UI, cumulative limits, Travel Rule data exchange,
  outbound merchant webhooks, reconciliation against provider statements, multi-instance locking.
