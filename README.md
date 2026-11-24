# Fiat-to-Crypto Gateway (sandbox foundation)

A payment gateway that lets a customer pay in a fiat currency and receive crypto in their own wallet
(an "on-ramp"). This repository is the **sandbox-only foundation**: the money handling, safety rules
and API are real and tested, but every bank, exchange, custodian and KYC provider is simulated.
**No real money can move**: the API refuses to start in any mode other than `Sandbox`.

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
| Compliance | KYC level and limits, blocked countries, sanctions match, wallet screening, manual review. Fails closed: a screening outage means human review, never approval. |
| API | API-key auth (only SHA-256 hashes are stored), idempotent order creation, signed and deduplicated webhooks, per-client data isolation. |
| Database | PostgreSQL schema that enforces the same rules (balanced ledger, legal state transitions, one order per quote), with tests. The sandbox API itself still stores data in memory. |
| Tests and CI | 126 .NET tests, 15 database constraint tests, and a secret scan, run on every push. |

What it does **not** do yet, and what is needed to get there, is in
[docs/OPEN_QUESTIONS.md](docs/OPEN_QUESTIONS.md).

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

Sandbox test customers: `cust_verified`, `cust_basic` (1,000 USD limit), `cust_unverified`,
`cust_sanctioned`, `cust_blocked_country`. Special wallet addresses that trigger compliance outcomes
are listed in `src/FiatCryptoGateway.Sandbox/SandboxCompliance.cs`.

## API

| Method and path | Purpose |
|---|---|
| `GET /health`, `GET /v1/environment` | Liveness; confirms `Sandbox` and `realFundsMovement: false`. No key needed. |
| `GET /v1/assets` | Supported currencies and crypto assets. |
| `POST /v1/quotes` | Price a fiat amount into a crypto asset. |
| `POST /v1/orders` | Accept a quote. Requires an `Idempotency-Key` header. |
| `GET /v1/orders/{id}`, `GET /v1/orders/{id}/ledger` | Order status and history; its journal entries. |
| `POST /v1/webhooks/sandbox-fiat` | Payment-provider webhook (signature-verified, no API key). |
| `POST /v1/sandbox/orders/{id}/pay`, `/confirm`, `/review` | Simulate payment, on-chain confirmations, and a compliance reviewer. |
| `GET /v1/sandbox/ledger/balances` | Whole-ledger balances (operator view). |

Errors are `{"error":{"code":"...","message":"..."}}` with stable codes such as `QUOTE_EXPIRED`,
`KYC_REQUIRED` or `BELOW_MINIMUM_AMOUNT`. Amounts are strings so JSON clients never lose precision.

## Tests

```bash
dotnet test FiatCryptoGateway.sln
```

Database (needs `psql` and a PostgreSQL server, for example `docker compose up -d` after copying
`.env.example` to `.env`):

```bash
PGHOST=127.0.0.1 PGPORT=5433 PGUSER=gateway_app PGDATABASE=gateway PGPASSWORD=your-local-password ./db/migrate.sh
```

```bash
PGHOST=127.0.0.1 PGPORT=5433 PGUSER=gateway_app PGDATABASE=gateway PGPASSWORD=your-local-password psql --set ON_ERROR_STOP=1 --file db/tests/001_gateway_constraints.sql
```

## Layout

| Path | Contents |
|---|---|
| `src/FiatCryptoGateway.Domain` | Money, assets, quotes, the order state machine, ledger and compliance rules. No I/O. |
| `src/FiatCryptoGateway.Application` | Order orchestration, provider interfaces, configuration, the sandbox-only safety gate. |
| `src/FiatCryptoGateway.Sandbox` | Simulated providers and in-memory stores. Real adapters will sit beside it. |
| `src/FiatCryptoGateway.Api` | HTTP API, API-key authentication, background order processing. |
| `db/` | PostgreSQL migration, runner and constraint tests. |
| `docs/` | [Architecture](docs/ARCHITECTURE.md) and [open questions](docs/OPEN_QUESTIONS.md). |
