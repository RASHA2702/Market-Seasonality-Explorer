# Fiat-to-Crypto Gateway (sandbox foundation)

A payment gateway that lets a customer pay in a fiat currency and receive crypto in their own wallet
(an "on-ramp"). This repository is the **sandbox-only foundation**: the money handling, safety rules
and API are real and tested, but every bank, exchange, custodian and KYC provider is simulated.
**No real money can move**: the API refuses to start in any mode other than `Sandbox`, and refuses any
provider setting that looks live (a non-test environment or URL, or a live-looking key).

> This repository was created as `Market-Seasonality-Explorer`. Consider renaming it on GitHub
> (Settings → General → Repository name), for example to `fiat-crypto-gateway`.

## What works today

| Area | Status |
|---|---|
| Currencies | All 156 circulating ISO 4217 currencies with official minor units (JPY 0 decimals, USD 2, KWD 3). Sandbox prices exist for 14 of them; the rest need a real FX source. |
| Crypto assets | Identified by symbol **and** network (USDC on Ethereum ≠ USDC on Solana). Sandbox: the stablecoins chosen for launch (USDT on Tron; USDT and USDC on Polygon, Ethereum and Solana), each network's native coin (TRX, POL, ETH, SOL), and BTC. |
| Quotes | Price route with freshness and no-look-ahead checks, service fee (rounded up), spread, network fee, crypto rounded down, min/max limits, 60-second expiry. |
| Orders | Explicit state machine: fiat captured → compliance → buy → send → confirmations. Late or wrong-amount payments are refunded, never converted. |
| Ledger | Double entry, balanced per asset, append-only. Every order leaves the customer liability at exactly zero. |
| Compliance | KYC level with per-payment, rolling 24-hour and rolling 30-day limits per customer; a country allow-list (India only by default) or block-list; sanctions match; wallet screening. Fails closed: a screening outage means human review, never approval. A reviewer queue (with a 24-hour overdue flag) and an append-only log of every decision; money from rejected orders is frozen and reported, and refunded only after an explicit release. |
| API | API-key auth (only SHA-256 hashes are stored), idempotent order creation, signed and deduplicated webhooks, per-client data isolation. |
| Settlement wallets | Each merchant registers one wallet per network to be paid into. Every new wallet is screened: a clean one can receive payments only after a 24-hour cooling-off period, so a merchant can disable a wallet it did not add; a risky one, or one whose screening could not finish, waits for a reviewer; a sanctioned one can never be approved. Merchants see only neutral statuses. |
| Reconciliation | Every morning (India time), each provider's settlement report for the previous day is compared with the books by the provider's own IDs. Movements missing on either side, different amounts and duplicates are recorded for a person to check; the books are never changed automatically. The day before is checked again, so a movement reported a day late clears. |
| Merchant webhooks | Every status change a merchant may see is posted to its webhook URL, signed with its own secret. A notification is saved in the same transaction as the change, so it is never lost or invented; one order's notifications arrive in order; failures are retried with backoff for about 3 hours, then recorded as given up. Reviewers can see each delivery's state. |
| Storage | In memory (default, for development) or PostgreSQL: durable, and safe to run as several API instances. The schema enforces the same rules as the code (balanced ledger, legal state transitions, one order per quote). |
| Tests and CI | 282 .NET tests (18 of them against a real PostgreSQL in Docker), 39 database constraint tests, and a secret scan, run on every push. |

What it does **not** do yet, and what is needed to get there, is in
[docs/decisions.md](docs/decisions.md).

## Run it locally

Prerequisites: .NET SDK 10.

1. Create an API key and its hash. The gateway stores only the hash.

   ```bash
   key=$(openssl rand -hex 32)
   echo "Your sandbox API key: $key"
   export Gateway__ApiKeys__Clients__0__ClientId=local-dev
   export Gateway__ApiKeys__Clients__0__KeySha256=$(printf '%s' "$key" | sha256sum | cut -d' ' -f1)
   ```

2. Start the API (in the same shell):

   ```bash
   ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run --project src/FiatCryptoGateway.Api
   ```

3. In a second shell (set `key` to the same value), walk an order through:

   ```bash
   curl -s -X POST http://127.0.0.1:5080/v1/quotes \
     -H "Authorization: Bearer $key" -H "Content-Type: application/json" \
     -d '{"sourceCurrency":"INR","sourceAmount":"10000.00","destinationAsset":"USDC@ethereum"}'
   ```

   ```bash
   curl -s -X POST http://127.0.0.1:5080/v1/orders \
     -H "Authorization: Bearer $key" -H "Content-Type: application/json" \
     -H "Idempotency-Key: $(uuidgen)" \
     -d '{"quoteId":"<quoteId from above>","customerId":"cust_verified","destinationAddress":"0x1111111111111111111111111111111111111111"}'
   ```

   ```bash
   curl -s -X POST http://127.0.0.1:5080/v1/sandbox/orders/<orderId>/pay -H "Authorization: Bearer $key"
   ```

   ```bash
   curl -s -X POST http://127.0.0.1:5080/v1/sandbox/orders/<orderId>/confirm -H "Authorization: Bearer $key"
   ```

   ```bash
   curl -s http://127.0.0.1:5080/v1/orders/<orderId>/ledger -H "Authorization: Bearer $key"
   ```

### Keep data in PostgreSQL

By default everything is lost when the API stops. To keep it, start the local database and point the API
at it (same shell as step 1):

```bash
cp .env.example .env
docker compose up -d
```

```bash
export Gateway__Storage__Provider=Postgres
export ConnectionStrings__Gateway="Host=127.0.0.1;Port=5433;Username=gateway_app;Password=<POSTGRES_PASSWORD from .env>;Database=gateway"
```

The API applies database migrations at startup; set `Gateway__Storage__ApplyMigrationsOnStartup=false` to
apply them yourself with `db/migrate.sh` instead. Several API instances can share one database.

### Reviewer key

The review endpoints need a key with the `Reviewer` role. Create one the same way as in step 1 and add it
as a second client:

```bash
export Gateway__ApiKeys__Clients__1__ClientId=compliance-officer
export Gateway__ApiKeys__Clients__1__KeySha256=<sha256 of the reviewer key>
export Gateway__ApiKeys__Clients__1__Role=Reviewer
```

Sandbox test customers (all in India unless noted): `cust_verified`, `cust_basic` (1,000 USD per payment,
2,000 per day, 5,000 per month), `cust_unverified`, `cust_sanctioned`, `cust_blocked_country` (North Korea)
and `cust_foreign` (United Kingdom, refused by the India-only rule). Special wallet addresses that trigger compliance outcomes
are listed in `src/FiatCryptoGateway.Sandbox/SandboxCompliance.cs`.

### Merchant webhooks

To have a merchant notified of status changes, give its client a webhook URL and a signing secret. Generate
the secret yourself (for example `openssl rand -hex 32`), share it with the merchant privately, and supply it
through an environment variable or secret store, never a committed file:

```bash
export Gateway__ApiKeys__Clients__0__WebhookUrl=https://merchant.example/gateway-events
export Gateway__ApiKeys__Clients__0__WebhookSecret=<at least 32 random characters>
```

The URL must be HTTPS (plain HTTP is accepted only for `localhost`, for testing). Each notification is a
`POST` with two headers, `X-Gateway-Event-Id` and `X-Gateway-Signature` (`t=<unix seconds>,v1=<hex>`), and a
JSON body:

```json
{
  "id": "0d9a2c6e-5f4b-4e8a-9c1d-2b7e8f3a6c51",
  "type": "order.status_changed",
  "createdAt": "2026-11-24T12:01:00+00:00",
  "data": {
    "orderId": "8d5ec9aa-c585-4c92-a010-94b991bf4394",
    "sequence": 2,
    "status": "FiatReceived",
    "previousStatus": "AwaitingFiatPayment",
    "reason": "FIAT_CAPTURED",
    "occurredAt": "2026-11-24T12:01:00+00:00"
  }
}
```

`sequence` numbers the order's steps as the merchant sees them, matching `GET /v1/orders/{id}`. Compliance
steps appear only as `OnHold`. Any 2xx answer counts as delivered; redirects are not followed. Delivery is
at least once, so the merchant should ignore an `id` it has already processed. The merchant verifies the
signature over the raw body before parsing it, for example in Python:

```python
import hashlib, hmac, time

def verify(secret: str, header: str, raw_body: bytes, tolerance_seconds: int = 300) -> bool:
    parts = [part.split("=", 1) for part in header.split(",") if "=" in part]
    timestamp = next((int(value) for key, value in parts if key == "t" and value.isdigit()), None)
    if timestamp is None or abs(time.time() - timestamp) > tolerance_seconds:
        return False
    expected = hmac.new(secret.encode(), f"{timestamp}.".encode() + raw_body, hashlib.sha256).hexdigest()
    return any(key == "v1" and hmac.compare_digest(expected, value) for key, value in parts)
```

A failed delivery is retried after 30 seconds, then 1, 2, 4, 8, 16 and 32 minutes, then hourly, up to 10
attempts (about 3 hours), after which it is recorded as given up. A later notification for the same order waits
until the earlier one is delivered or given up. These and other settings are under `Gateway:Webhooks`
(`MaxAttempts`, `BaseRetrySeconds`, `MaxRetrySeconds`, `SendTimeoutSeconds`, `LeaseSeconds`, `BatchSize`,
`IntervalSeconds`, `Enabled`).

### Settlement wallets

Each merchant registers the wallet it is paid into, one per network (Tron, Polygon, Ethereum and Solana by
default), with its merchant key:

```bash
curl -s -X POST http://127.0.0.1:5080/v1/settlement/wallets -H "Authorization: Bearer $key" -H "Content-Type: application/json" -d '{"network":"polygon","address":"0x...","label":"Treasury"}'
```

Every new wallet is screened. The merchant sees one of these statuses, never the screening detail:

| Status | Meaning |
|---|---|
| `CoolingOff` | Screened clean; it can receive payments from `usableFrom` (24 hours after registration by default). A merchant that did not register it should disable it at once. |
| `UnderReview` | Waiting for a reviewer: screening found risk or could not finish. A sanctioned wallet stays here until a reviewer rejects it; it can never be approved. |
| `Active` | Receives the merchant's payments on its network. |
| `Replaced` | A newer wallet for the network took over when it became usable. Until then this one stayed active. |
| `NotAccepted`, `Disabled` | Declined by a reviewer, or withdrawn by the merchant (`POST /v1/settlement/wallets/{id}/disable`) or a reviewer. |

Only one change per network can be waiting at a time. Reviewers see waiting wallets with their screening at
`GET /v1/review/settlement-wallets` and decide with `/approve`, `/reject` or `/disable`, each with a `reason`.
Settings are under `Gateway:Settlement`: `CoolingOffHours` (24) and `Networks` (`tron,polygon,ethereum,solana`).
For local testing, `Gateway__Settlement__CoolingOffHours=0` makes clean wallets usable at once.

### Daily reconciliation

Each morning after 06:00 India time, the API compares every provider's settlement report for the previous
day (midnight to midnight, India time) with the books, matching movements by the provider's own ID, and
records the result. It checks the day before again, so a movement a provider reports a day late stops showing
as a difference. A run never changes the books; each difference is for a person to investigate:

| Code | Meaning | Typical cause |
|---|---|---|
| `MISSING_IN_LEDGER` | The provider reports a movement the books do not have. | A payment whose webhook never arrived: the customer has paid, but the order has not moved. The most urgent. |
| `MISSING_IN_REPORT` | The books have a movement the provider does not report. | The provider placed it on another day, or a recording error. |
| `AMOUNT_MISMATCH` | Both have it, with different amounts or currencies. | A partial capture, a fee taken out of the amount, or a recording error. |
| `DUPLICATE_IN_LEDGER`, `DUPLICATE_IN_REPORT` | One movement appears twice on one side. | PostgreSQL refuses to book a movement twice, so a duplicate in the books means data was changed outside the gateway. |

To reconcile a finished day now, and to see past runs (reviewer key):

```bash
curl -s -X POST http://127.0.0.1:5080/v1/review/reconciliation/runs -H "Authorization: Bearer $reviewer_key" -H "Content-Type: application/json" -d '{"day":"2026-11-24"}'
```

```bash
curl -s "http://127.0.0.1:5080/v1/review/reconciliation/runs?day=2026-11-24" -H "Authorization: Bearer $reviewer_key"
```

Settings are under `Gateway:Reconciliation`: `TimeZone` (default `Asia/Kolkata`), `RunAfterHour` (6),
`LookbackDays` (2), `CheckIntervalMinutes` (15) and `Enabled`. In the sandbox, the simulated payment provider
publishes its report from its own records, so a payment made with `/v1/sandbox/orders/{id}/pay` matches. Those
records are kept in memory: after a restart with PostgreSQL storage, payments from before the restart show as
`MISSING_IN_REPORT`. A real provider's reports do not forget.

## API

| Method and path | Purpose |
|---|---|
| `GET /health`, `GET /v1/environment` | Liveness; confirms `Sandbox` and `realFundsMovement: false`. No key needed. |
| `GET /v1/assets` | Supported currencies and crypto assets. |
| `POST /v1/quotes` | Price a fiat amount into a crypto asset. |
| `POST /v1/orders` | Accept a quote. Requires an `Idempotency-Key` header. |
| `GET /v1/orders/{id}`, `GET /v1/orders/{id}/ledger` | Order status and history; its journal entries. |
| `POST /v1/webhooks/sandbox-fiat` | Payment-provider webhook (signature-verified, no API key). |
| `POST /v1/sandbox/orders/{id}/pay`, `/confirm` | Simulate the customer's payment and on-chain confirmations. |
| `GET /v1/review/queue`, `GET /v1/review/orders/{id}` | Reviewer keys only: held payments, longest-waiting first, with an overdue flag; full screening reasons and the review log. |
| `POST /v1/review/orders/{id}/approve`, `/reject`, `/freeze`, `/release` | Reviewer decisions. Each needs a `reason`; `/freeze` also needs the `reportReference` of the report filed. |
| `GET /v1/review/orders/{id}/webhooks` | Reviewer keys only: each merchant notification for the order, with its delivery state, attempts and last error. |
| `GET /v1/settlement/wallets`, `POST /v1/settlement/wallets` | The merchant's settlement wallets, newest first; register one (`network`, `address`, optional `label`). |
| `POST /v1/settlement/wallets/{id}/disable` | Withdraw one of the merchant's wallets. |
| `GET /v1/review/settlement-wallets`, `GET /v1/review/settlement-wallets/{id}` | Reviewer keys only: wallets waiting for review, with their screening and history. |
| `POST /v1/review/settlement-wallets/{id}/approve`, `/reject`, `/disable` | Reviewer decisions on a wallet, each with a `reason`. A sanctioned wallet cannot be approved. |
| `GET /v1/review/ledger/balances` | Reviewer keys only: whole-ledger balances across all merchants. |
| `GET /v1/review/reconciliation/runs?day=yyyy-MM-dd` | Reviewer keys only: reconciliation runs, newest first, with every difference found. |
| `POST /v1/review/reconciliation/runs` | Reviewer keys only: reconcile a finished day now (`{"day":"yyyy-MM-dd"}`). |

Errors are `{"error":{"code":"...","message":"..."}}` with stable codes such as `QUOTE_EXPIRED`,
`KYC_REQUIRED` or `BELOW_MINIMUM_AMOUNT`. Amounts are strings so JSON clients never lose precision.

## Tests

```bash
dotnet test FiatCryptoGateway.sln
```

The PostgreSQL tests start their own database in Docker, so Docker must be running.

Database scripts (need `psql` and a PostgreSQL server, for example `docker compose up -d` after copying
`.env.example` to `.env`):

```bash
PGHOST=127.0.0.1 PGPORT=5433 PGUSER=gateway_app PGDATABASE=gateway PGPASSWORD=your-local-password ./db/migrate.sh
```

```bash
for test in db/tests/*.sql; do PGHOST=127.0.0.1 PGPORT=5433 PGUSER=gateway_app PGDATABASE=gateway PGPASSWORD=your-local-password psql --set ON_ERROR_STOP=1 --file "$test"; done
```

## Layout

| Path | Contents |
|---|---|
| `src/FiatCryptoGateway.Domain` | Money, assets, quotes, the order state machine, ledger and compliance rules. No I/O. |
| `src/FiatCryptoGateway.Application` | Order orchestration, provider interfaces, configuration, the sandbox-only safety gate. |
| `src/FiatCryptoGateway.Sandbox` | Simulated providers and in-memory stores. Real adapters will sit beside it. |
| `src/FiatCryptoGateway.Persistence.Postgres` | PostgreSQL stores, cross-instance order locks, and the migration runner. |
| `src/FiatCryptoGateway.Api` | HTTP API, API-key authentication, background order processing. |
| `db/` | PostgreSQL migration, runner and constraint tests. |
| `docs/` | [Architecture](docs/ARCHITECTURE.md), [decisions](docs/decisions.md) and the [partner integration design](docs/partner-integration.md). |
