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
| Crypto assets | Identified by symbol **and** network (USDC on Ethereum ≠ USDC on Solana). Sandbox: BTC, ETH, SOL, USDC and USDT on Ethereum, USDC on Solana. |
| Quotes | Price route with freshness and no-look-ahead checks, service fee (rounded up), spread, network fee, crypto rounded down, min/max limits, 60-second expiry. |
| Orders | Explicit state machine: fiat captured → compliance → buy → send → confirmations. Late or wrong-amount payments are refunded, never converted. |
| Ledger | Double entry, balanced per asset, append-only. Every order leaves the customer liability at exactly zero. |
| Compliance | KYC level with per-payment, rolling 24-hour and rolling 30-day limits per customer; a country allow-list (India only by default) or block-list; sanctions match; wallet screening. Fails closed: a screening outage means human review, never approval. A reviewer queue (with a 24-hour overdue flag) and an append-only log of every decision; money from rejected orders is frozen and reported, and refunded only after an explicit release. |
| API | API-key auth (only SHA-256 hashes are stored), idempotent order creation, signed and deduplicated webhooks, per-client data isolation. |
| Merchant webhooks | Every status change a merchant may see is posted to its webhook URL, signed with its own secret. A notification is saved in the same transaction as the change, so it is never lost or invented; one order's notifications arrive in order; failures are retried with backoff for about 3 hours, then recorded as given up. Reviewers can see each delivery's state. |
| Storage | In memory (default, for development) or PostgreSQL: durable, and safe to run as several API instances. The schema enforces the same rules as the code (balanced ledger, legal state transitions, one order per quote). |
| Tests and CI | 208 .NET tests (13 of them against a real PostgreSQL in Docker), 28 database constraint tests, and a secret scan, run on every push. |

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
| `GET /v1/review/ledger/balances` | Reviewer keys only: whole-ledger balances across all merchants. |

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
