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
| Orders | Paid out only to the merchant's active settlement wallet for the quote's network, which is screened again on every order. Explicit state machine: fiat captured → compliance → buy → send → confirmations. Late or wrong-amount payments are refunded, never converted. |
| Ledger | Double entry, balanced per asset, append-only. Every order leaves the customer liability at exactly zero. |
| Compliance | KYC level with per-payment, rolling 24-hour and rolling 30-day limits per customer; a country allow-list (India only by default) or block-list; sanctions match; wallet screening. Fails closed: a screening outage means human review, never approval. A reviewer queue (with a 24-hour overdue flag) and an append-only log of every decision; money from rejected orders is frozen and reported, and refunded only after an explicit release. |
| API | API-key auth (only SHA-256 hashes are stored), idempotent order creation, signed and deduplicated webhooks, per-client data isolation. |
| Payment processor | The gateway collects rupees with its own processor, which takes cards and UPI. In the sandbox it talks to two simulated networks that accept **test numbers only**: any other card number, even a valid one, is refused, and nothing typed is ever stored or logged (an attempt keeps only the brand and last four digits, or the UPI address). Each try to pay is kept as a payment attempt with its full history, an order is charged once, and a background sweep finishes whatever a crash left half done. |
| Hosted checkout | Every order comes with a signed link to a mobile-first page the gateway hosts. The customer sees the merchant, the amount in rupees and the deadline, and pays by UPI or card. The details go to the gateway's own processor; the page handles the bank's check (a one-time password, or approving a UPI request) and says in plain words why a payment did not go through, with the forms back to try again. Nothing typed is ever put back into a page or an address. Customers see only "waiting for payment", "payment received", "refund started", "refunded" or "expired": compliance holds look like any received payment. The page loads nothing from other sites and cannot be framed. |
| Settlement wallets | Each merchant registers one wallet per network to be paid into. Every new wallet is screened: a clean one can receive payments only after a 24-hour cooling-off period, so a merchant can disable a wallet it did not add; a risky one, or one whose screening could not finish, waits for a reviewer; a sanctioned one can never be approved. Merchants see only neutral statuses. |
| Reconciliation | Every morning (India time), each provider's settlement report for the previous day is compared with the books by the provider's own IDs. Movements missing on either side, different amounts and duplicates are recorded for a person to check; the books are never changed automatically. The day before is checked again, so a movement reported a day late clears. |
| Merchant webhooks | Every order status change a merchant may see, and every change to its settlement wallets, is posted to its webhook URL, signed with its own secret. A notification is saved in the same transaction as the change, so it is never lost or invented; one order's notifications arrive in order; failures are retried with backoff for about 3 hours, then recorded as given up. Reviewers can see each delivery's state and send a given-up notification again. |
| Reviewer console | One page at `/review` for the compliance officer: held payments to decide, frozen payments to release, wallets to review, reconciliation differences, notifications that failed, whether the ledger balances, and the state of the backups and alerts (the last backup, the problems the alert check sees now, and the alert emails sent). Reviewers sign in with their own username, password and a code from an authenticator app; the session lives only in the page's memory and ends after 30 minutes without activity. |
| Reviewer sign-in | One account per person, with two-factor authentication (a password and a 6-digit authenticator code, each code usable once). Five failures in a row pause sign-in for 15 minutes; sign-in requests are rate-limited per address. Sessions end on sign-out, after 30 idle minutes or 12 hours, or when the account changes. Every sign-in, failure and sign-out is logged, and the console tells a reviewer about failed attempts since their last visit. A setup command creates the account's settings and shows the authenticator QR code. |
| Storage | In memory (default, for development) or PostgreSQL: durable, and safe to run as several API instances. The schema enforces the same rules as the code (balanced ledger, legal state transitions, one order per quote). |
| Containers and HTTPS | The API as a small container image (no shell, non-root, read-only), started with PostgreSQL behind an HTTPS proxy (Caddy) by one command. Caddy gets and renews certificates by itself: a local one for this machine, a public one for a real domain. The reviewer console and review API answer only from private networks (configurable), and per-client sign-in limits use the address the proxy saw, which clients cannot fake. Checked end to end in CI. |
| Database backups | Every night (and at start), the database is dumped, restored into a scratch database and checked (the ledger balances, nothing is missing), then encrypted to a key you keep off the server. Old local copies are pruned; new ones can be copied off-site. A restore command recovers any backup into a new database, never over the live one. CI backs up, restores and compares on every push. |
| Email alerts | Every 5 minutes the gateway checks for held payments and new wallets waiting long for review (and again when the 24-hour limit passes), payments stuck between the payment network and their order, reconciliation differences, notifications given up on, and failed or missing backups. Someone is emailed when something new appears, reminded while problems stay open, and told once when all is clear. Emails carry no customer details and link to the console. |
| Tests and CI | 741 .NET tests (44 of them against a real PostgreSQL in Docker), 70 database constraint tests, a check of the whole container stack through the HTTPS proxy, and a secret scan, run on every push. |

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

2. Start the API (in the same shell). For local testing, new settlement wallets are made usable at once instead
   of after the usual 24-hour cooling-off period:

   ```bash
   Gateway__Settlement__CoolingOffHours=0 ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run --project src/FiatCryptoGateway.Api
   ```

3. In a second shell (set `key` to the same value), register the wallet you are paid into, then walk an order
   through:

   ```bash
   curl -s -X POST http://127.0.0.1:5080/v1/settlement/wallets \
     -H "Authorization: Bearer $key" -H "Content-Type: application/json" \
     -d '{"network":"ethereum","address":"0x1111111111111111111111111111111111111111"}'
   ```

   ```bash
   curl -s -X POST http://127.0.0.1:5080/v1/quotes \
     -H "Authorization: Bearer $key" -H "Content-Type: application/json" \
     -d '{"sourceCurrency":"INR","sourceAmount":"10000.00","destinationAsset":"USDC@ethereum"}'
   ```

   ```bash
   curl -s -X POST http://127.0.0.1:5080/v1/orders \
     -H "Authorization: Bearer $key" -H "Content-Type: application/json" \
     -H "Idempotency-Key: $(uuidgen)" \
     -d '{"quoteId":"<quoteId from above>","customerId":"cust_verified"}'
   ```

   Open the order's `checkoutUrl` in a browser to pay as a customer would, or simulate the payment directly:

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

### Reviewer accounts

Reviewers (the compliance officer, and anyone who later shares the work) each have their own account and sign in
with a password and a 6-digit code from an authenticator app (Google Authenticator, Microsoft Authenticator,
1Password and others). There are no reviewer API keys: an API client with `Role=Reviewer` stops the API at startup.

Each reviewer runs the setup command on their own computer:

```bash
dotnet run --project src/FiatCryptoGateway.ReviewerSetup
```

It asks for a username and a password (typed twice, not shown), draws a QR code to scan with the authenticator app,
checks one code from the app, and prints four settings: the username, display name, a hash of the password (never
the password itself) and the authenticator key. Set them where the API runs, then restart it:

```bash
export Gateway__Review__Reviewers__0__Username=<from the setup command>
export Gateway__Review__Reviewers__0__DisplayName=<from the setup command>
export Gateway__Review__Reviewers__0__PasswordHash=<from the setup command>
export Gateway__Review__Reviewers__0__TotpSecret=<from the setup command>
```

For a second reviewer use `__1__` (`-- --index 1` makes the command print that), and so on. The authenticator key
lets anyone make that reviewer's codes: keep these values in a secret store or environment variables, never in a
committed file or a chat. To change a password or replace a lost phone, run the command again and replace the
settings; the reviewer's open sessions end on restart. Removing a reviewer's settings ends their access.

Sign-in rules, each a setting under `Gateway:Review`: five failures in a row pause sign-in for 15 minutes
(`MaxFailedAttempts`, `LockoutMinutes`); each network address may try 10 times a minute (`SignInAttemptsPerMinute`);
a session ends after 30 minutes without use or 12 hours in all (`SessionIdleMinutes`, `SessionMaxHours`). Every
sign-in, failure, pause and sign-out is recorded with the reviewer and the time; with PostgreSQL that log is
append-only.

Sandbox test customers (all in India unless noted): `cust_verified`, `cust_basic` (1,000 USD per payment,
2,000 per day, 5,000 per month), `cust_unverified`, `cust_sanctioned`, `cust_blocked_country` (North Korea)
and `cust_foreign` (United Kingdom, refused by the India-only rule). Special wallet addresses that trigger compliance outcomes
are listed in `src/FiatCryptoGateway.Sandbox/SandboxCompliance.cs`.

### Reviewer console

Open <http://127.0.0.1:5080/review> and sign in with your username, password and the current code from your
authenticator app. After sign-in the page says when you last signed in and warns about failed attempts since then.
One page shows everything waiting for a person, and how the gateway itself is doing, with a count or a state for each:

| Section | What you can do |
|---|---|
| Held payments | Longest-waiting first, with the screening reasons and an "Overdue" flag after 24 hours. Approve or reject; a rejected payment stays here until you freeze it and record the reference of the report you filed. |
| Frozen payments | Release one for refund. |
| Wallets to review | New settlement wallets whose screening found risk or could not finish, with the screening detail. Approve or reject; a sanctioned wallet can only be rejected. |
| Reconciliation | The latest run for each provider and day, with every difference; reconcile a finished day. |
| Failed notifications | Merchant notifications given up on in the last 7 days, with the last error. "Send again" gives one another attempt, for example after the merchant has fixed its endpoint. |
| Ledger | Whether the whole ledger balances. |
| Backups and alerts | The problems the alert check sees right now (urgent first), the state of the database backups (the last successful one, and the last seven attempts with their size and any failure), and the alert emails sent lately with what each reported. The "Open problems" and "Last backup" tiles at the top turn red when something needs you. The open problems are checked each time the page refreshes, so they show even while alert emails are off. |

Each payment shows its full history on request. Every decision needs a reason and is logged with the reviewer's
username and the time, exactly as through the API, because the page works only through the reviewer API.

The session token stays in the page's memory only: it is never saved in the browser, sent in an address or put in
a cookie, and reloading or closing the page signs you out. After 30 minutes without activity the session ends. The
page loads nothing from other sites, cannot be framed, and holds no data of its own.
`Gateway__Review__ConsoleEnabled=false` stops serving it. In production, consider making `/review` (and the
`/v1/review` API) reachable only from your internal network or a VPN.

### Email alerts

The gateway can email you when something needs a person, so the console does not have to be watched. Every 5 minutes
it checks for:

| Problem | When |
|---|---|
| A held payment waiting for a decision | After 18 hours (`ReviewWarnAfterHours`), and again, marked URGENT, when its 24-hour limit passes. |
| A new settlement wallet waiting for review | After 18 hours. |
| A payment stuck between the payment network and its order | URGENT, once it has been stuck for 30 minutes (`StuckPaymentAfterMinutes`): approved but not captured, or captured but not recorded on its order. The gateway's own sweep keeps trying meanwhile. |
| An approval the gateway gave up on | URGENT, for 7 days (`LapsedApprovalReportDays`) after the gateway gave up (24 hours after the approval): the customer may have been debited, so check the payment network's own report. |
| A reconciliation difference | As soon as a run finds one; URGENT when the provider reports money the books do not have. |
| A merchant notification the gateway gave up on | Within the last day. |
| A failed database backup, or none for 26 hours | URGENT (container setup only, where backups are recorded). |

An email is sent when a new problem appears or one gets worse, again after 12 hours while problems stay open
(`RemindAfterHours`), and once with "All clear" when everything is resolved. Emails name orders by their first eight
characters and merchants by ID, never customers, and link to the reviewer console. With several instances, one checks
at a time, and what was sent is recorded, so a restart does not repeat it.

The reviewer console shows the same problems live, the backup history and the emails sent (README, Reviewer console).

Alerts are off until you name who receives them and the mail server that sends them (any provider's SMTP settings;
port 587 uses STARTTLS). In the container setup, put these in `gateway.env`:

```bash
Gateway__Alerts__To=you@example.com
Gateway__Alerts__From=gateway-alerts@example.com
Gateway__Alerts__Smtp__Host=smtp.example.com
Gateway__Alerts__Smtp__Username=<from your email provider>
Gateway__Alerts__Smtp__Password=<from your email provider>
```

Several addresses are separated by commas. How long things may wait is also a setting: `Gateway__Alerts__ReviewWarnAfterHours` (18), `Gateway__Alerts__StuckPaymentAfterMinutes` (30, from 5 to 1440), `Gateway__Alerts__LapsedApprovalReportDays` (7, from 1 to 90) and `Gateway__Alerts__RemindAfterHours` (12). The settings are checked at startup; unencrypted SMTP is accepted only for
a mail server on the same machine. If an email cannot be sent, the next check tries again.

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
steps appear only as `OnHold`.

Every change to a merchant's settlement wallets is notified too, as `settlement_wallet.status_changed`, so a
merchant learns of a wallet it did not register while it is still cooling off and can disable it:

```json
{
  "id": "5b1f0d0e-2f7c-4d8e-9a43-6f0c1e2d3b4a",
  "type": "settlement_wallet.status_changed",
  "createdAt": "2026-11-25T06:00:00+00:00",
  "data": {
    "walletId": "ec05e0b7-9655-4c26-8d3d-a70a427c2437",
    "network": "polygon",
    "address": "0x1111111111111111111111111111111111111111",
    "label": "Treasury",
    "status": "CoolingOff",
    "usableFrom": "2026-11-26T06:00:00+00:00",
    "sequence": 1,
    "occurredAt": "2026-11-25T06:00:00+00:00"
  }
}
```

Wallet notifications carry the merchant's statuses only, never screening results or a reviewer's reasons.

Any 2xx answer counts as delivered; redirects are not followed. Delivery is
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
attempts (about 3 hours), after which it is recorded as given up. A later notification for the same order or
wallet waits until the earlier one is delivered or given up. A reviewer can send a given-up notification again from
the console (or `POST /v1/review/webhooks/{eventId}/retry`), for example once the merchant has fixed its endpoint.
It gets one more attempt and keeps its original `id`, so a merchant that did receive it after all can discard the
copy. It may arrive after later notifications for the same order or wallet; `sequence` says where it belongs. These and other settings are under `Gateway:Webhooks`
(`MaxAttempts`, `BaseRetrySeconds`, `MaxRetrySeconds`, `SendTimeoutSeconds`, `LeaseSeconds`, `BatchSize`,
`IntervalSeconds`, `Enabled`).

### Payment processor

The gateway collects rupees with its own payment processor. In the sandbox it uses two simulated networks, one for
cards and one for UPI, which accept **test instruments only**: any other card number, even one that passes the
checksum, is refused, and nothing typed is stored or logged (an attempt keeps only the brand and last four digits, or
the UPI address). Every try to pay an order is kept as a payment attempt with its full history, and an order can have
only one captured payment.

`Gateway:Fiat:Provider` chooses who collects: `Processor` (the default) or `SandboxPartner`, the earlier simulated
partner that reports payments by webhook (kept for its tests). The settings under `Gateway:Processor` are all checked
at startup:

| Setting | Default | Meaning |
|---|---|---|
| `AttemptWindowMinutes` | 10 | How long a customer has to finish one attempt; never longer than the order's own deadline |
| `AuthenticationTries` | 3 | Tries to answer the bank's check |
| `MaxAttemptsPerOrder` | 10 | Attempts one order may have, however they end |
| `StuckAfterSeconds` | 120 | How long an approved payment may wait before the background sweep captures it |
| `AuthorizationLapseHours` | 24 | After this long, an approval that cannot be captured is given up and flagged |
| `SweepBatchSize` | 100 | The most items one sweep handles |

The sweep runs with the other background work (`Gateway:Processing`). It ends attempts that ran out of time, captures
approved payments and records captured payments on their orders. In the sandbox, `POST /v1/sandbox/orders/{id}/pay`
pays an order through the processor with the test UPI address that is always approved. Wrong-amount and late payments
can only be simulated with `SandboxPartner`.

### Hosted checkout page

Every order's `checkoutUrl` is a link to a page the gateway hosts. The merchant sends its customer there (a redirect
or a link); the customer sees the merchant's name, the amount in rupees (₹1,25,000.00) and how long the price is held,
and pays by UPI or card (netbanking and bank transfer come later). The details go to the gateway's own
[payment processor](#payment-processor), which asks the payment network. If the bank wants a check, the page asks for
the one-time password, or tells the customer to approve the request in their UPI app; it then shows the result and
updates by itself. A payment that does not go through is explained in plain words with the forms back, so the customer
can try again, and a card number or address that is not accepted is never repeated back. Nothing typed is put into a
page, an address or a log.

In the sandbox the page also lists the test instruments below and has a button that pays with the test UPI address.

**Sandbox test instruments.** Only these are accepted: any other card number, even a valid one, and any other UPI
address is refused, and nothing is kept. Use any expiry date in the future, any 3-digit security code (4 digits for
American Express) and the one-time password `123456`.

| Card | What happens |
|---|---|
| `4111 1111 1111 1111` (Visa) | Approved |
| `4242 4242 4242 4242` (Visa), `5555 5555 5555 4444` (Mastercard) | Asks for the one-time password, then approved |
| `3782 8224 6310 005` (American Express) | Approved (four-digit security code) |
| `4000 0000 0000 3220` (Visa) | Asks for the one-time password, which is never accepted |
| `4000 0000 0000 0002`, `4000 0000 0000 9995`, `4000 0000 0000 0069`, `4000 0000 0000 0127` | Declined: by the bank, insufficient funds, expired card, incorrect security code |
| `4000 0000 0000 0259` | The card network is unavailable |

| UPI address | What happens |
|---|---|
| `success@sandbox` | You approve the request, and it is paid |
| `insufficient@sandbox` | You approve the request, and the bank declines: insufficient funds |
| `timeout@sandbox` | The request is never answered and times out |
| `invalid@sandbox` | The address does not exist |
| `unavailable@sandbox` | The UPI network is unavailable |

With the earlier partner (`Gateway:Fiat:Provider=SandboxPartner`) the page has no forms, only a sandbox button that
simulates that partner's payment.

Customers see one of: waiting for payment, payment received, refund started, refunded, or expired. Review,
rejection and freezing all look like "payment received", so nothing tips off a customer under suspicion.

The link is signed (HMAC-SHA256 of the order ID), so it cannot be guessed or changed to another order, and it is the
customer's only credential: no account, no cookie. Settings:

```bash
export Gateway__Checkout__BaseUrl=https://pay.example.com   # the public address the pages are served from
export Gateway__Checkout__LinkSecret=<at least 32 random characters, for example from: openssl rand -hex 32>
export Gateway__ApiKeys__Clients__0__DisplayName="Acme Books"                       # what customers see
export Gateway__ApiKeys__Clients__0__CheckoutReturnUrl=https://acme.example/paid    # optional "Back to" link
```

Without a `LinkSecret`, links use a random secret that changes on every restart; set one for PostgreSQL storage or
more than one instance. The return link gets `?order=<id>` added; anyone can open that address, so the merchant
must confirm the payment through the API or a webhook, never from the redirect alone. Deadlines are shown in
`Gateway:Checkout:TimeZone` (India Standard Time by default).

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

Orders pay out only to the merchant's `Active` wallet for the quote's network; without one, an order is refused
with `NO_SETTLEMENT_WALLET`, and an order request that names its own `destinationAddress` is refused with
`DESTINATION_NOT_ACCEPTED`. Each order screens the wallet again, so one flagged after its approval stops receiving
payments. Only one change per network can be waiting at a time. Reviewers see waiting wallets with their screening at
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

To reconcile a finished day now, and to see past runs, first sign in as a reviewer. This reads the password without
showing it or keeping it in your shell history (needs `jq`):

```bash
read -r -p "Username: " u; read -r -s -p "Password: " p; echo; read -r -p "Code: " c
```

```bash
reviewer_token=$(jq -n --arg u "$u" --arg p "$p" --arg c "$c" '{username:$u,password:$p,code:$c}' | curl -s -X POST http://127.0.0.1:5080/v1/review/sign-in -H "Content-Type: application/json" -d @- | jq -r .token); unset p
```

```bash
curl -s -X POST http://127.0.0.1:5080/v1/review/reconciliation/runs -H "Authorization: Bearer $reviewer_token" -H "Content-Type: application/json" -d '{"day":"2026-11-24"}'
```

```bash
curl -s "http://127.0.0.1:5080/v1/review/reconciliation/runs?day=2026-11-24" -H "Authorization: Bearer $reviewer_token"
```

Settings are under `Gateway:Reconciliation`: `TimeZone` (default `Asia/Kolkata`), `RunAfterHour` (6),
`LookbackDays` (2), `CheckIntervalMinutes` (15) and `Enabled`. In the sandbox, the simulated payment provider
publishes its report from its own records, so a payment made with `/v1/sandbox/orders/{id}/pay` matches. Those
records are kept in memory: after a restart with PostgreSQL storage, payments from before the restart show as
`MISSING_IN_REPORT`. A real provider's reports do not forget.

## Run it in containers, behind HTTPS

Docker with Compose runs PostgreSQL, the API and an HTTPS proxy (Caddy) together. The API's container is never
published directly: everything goes through the proxy, which also redirects plain HTTP to HTTPS.

1. Settings, in two files that are never committed:

   ```bash
   cp .env.example .env
   ```

   ```bash
   cp gateway.env.example gateway.env
   ```

   In `.env`, choose a database password. In `gateway.env`, put your merchant key's hash (step 1 of "Run it
   locally"), a checkout link secret (`openssl rand -hex 32`), and the four lines the reviewer setup command
   printed, without `export`. The API refuses to start while a placeholder is left in.

2. Build and start:

   ```bash
   docker compose --profile gateway up -d --build --wait
   ```

3. Open <https://localhost/review>. The certificate comes from Caddy's own local authority, so the browser warns
   about it the first time. To have this machine trust it, copy it out and add it to your system's trusted
   certificates (on macOS, open the file in Keychain Access and mark it trusted):

   ```bash
   docker compose --profile gateway cp proxy:/data/caddy/pki/authorities/local/root.crt caddy-local-root.crt
   ```

4. Check the whole setup from outside, as CI does on every push:

   ```bash
   ./deploy/smoke-test.sh
   ```

`docker compose --profile gateway down` stops everything; the database and certificates stay in Docker volumes
(`down --volumes` deletes them). `docker compose up -d` without the profile still starts only PostgreSQL, for
running the API with `dotnet run`.

What runs:

| Container | Reachable from | Notes |
|---|---|---|
| `proxy` (Caddy 2.10) | `127.0.0.1:80` and `:443` on this machine (`GATEWAY_BIND`, `GATEWAY_HTTP_PORT`, `GATEWAY_HTTPS_PORT`) | Terminates TLS, adds HSTS, limits request bodies to 1 MB, answers `/review` and `/v1/review` only from `GATEWAY_REVIEW_FROM` (private networks by default) and keeps checkout links out of its access log. |
| `api` | The proxy only | Non-root, read-only file system, no Linux capabilities. Trusts `X-Forwarded-For` only from the proxy's fixed address, so sign-in limits apply per real client. |
| `postgres` | The API, and `127.0.0.1:5433` for your own tools | Its network with the API has no way out to the internet. |

On a server, with your domain's DNS pointing at it and ports 80 and 443 open, set in `.env`:
`GATEWAY_DOMAIN=<your domain>`, `GATEWAY_TLS=<an email address for certificate notices>` (Caddy then gets a
public certificate from Let's Encrypt and renews it), `GATEWAY_BIND=0.0.0.0`, and `GATEWAY_REVIEW_FROM=<your
office or VPN ranges>`. Once the site must only ever be reached over HTTPS, set `GATEWAY_HSTS_SECONDS=31536000`.
On a real server, keep the values from `gateway.env` in the hosting platform's secret store, and back up the
database volume. The gateway is still sandbox-only there: it refuses to start in any other mode.

### Database backups

The `backup` container backs up the database every night at 02:30 India time, and once whenever it starts. Each
backup is restored into a scratch database and checked (the ledger balances, every journal entry is whole, the
append-only protections are in place, and no rows are missing) before it is kept. It is then encrypted with
[age](https://age-encryption.org) to your public key, so a copy on this server, or anywhere else, can only be read
with the private key that you keep elsewhere.

1. On your own computer, make the key pair (install age with `brew install age`, `apt install age` or
   `winget install FiloSottile.age`):

   ```bash
   age-keygen -o backup-key.txt
   ```

   Keep `backup-key.txt` off the server: in your password manager, and printed in a safe place. Without it no
   backup can ever be read, by you or anyone else. A second key for a second trusted person can be added later.

2. Put the public key (the `age1...` line it printed) in `.env`, then start or restart the stack:

   ```bash
   BACKUP_AGE_RECIPIENTS=age1...
   ```

Backups land in `./backups` (`BACKUP_DIR`), with a checksum file each, and are deleted there after 14 days
(`BACKUP_KEEP_DAYS`). `docker compose --profile gateway ps backup` shows `unhealthy` if the last backup failed or
none has succeeded for 26 hours, and the container's log says why. To back up at once:

```bash
docker compose --profile gateway exec backup gateway-backup
```

**Restore drill (monthly is reasonable).** This restores a backup into a new database, checks it, and never
touches the live one:

```bash
docker compose --profile gateway run --rm -v "$PWD/backup-key.txt:/run/secrets/backup-key:ro" backup gateway-restore gateway-20261126T210000Z.dump.age
```

It prints the new database's name and how to remove it afterwards. In a real recovery, stop the API, point its
connection string at the restored database (or rename the databases), and start it again. Anything after the
backup's time is not in it; for real money, add the managed database's continuous point-in-time recovery as well.

**Off-site copies.** A backup on the same disk as the database does not survive losing that disk. Set
`BACKUP_COPY_TO` to an [rclone](https://rclone.org) destination, such as S3-compatible storage in an India region,
and put its credentials in `backup.env` (never committed), for example:

```bash
RCLONE_CONFIG_OFFSITE_TYPE=s3
RCLONE_CONFIG_OFFSITE_PROVIDER=AWS
RCLONE_CONFIG_OFFSITE_REGION=ap-south-1
RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID=<from your storage provider>
RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY=<from your storage provider>
```

with `BACKUP_COPY_TO=offsite:<bucket>/gateway` in `.env`. Copies are only ever added there; set how long the
storage keeps them with its own retention rules.

## API

| Method and path | Purpose |
|---|---|
| `GET /health`, `GET /v1/environment` | Liveness; confirms `Sandbox` and `realFundsMovement: false`. No key needed. |
| `GET /v1/assets` | Supported currencies and crypto assets. |
| `POST /v1/quotes` | Price a fiat amount into a crypto asset. |
| `POST /v1/orders` | Accept a quote (`quoteId`, `customerId`). Pays out to the merchant's active settlement wallet for the quote's network. Requires an `Idempotency-Key` header. |
| `GET /v1/orders/{id}`, `GET /v1/orders/{id}/ledger` | Order status and history; its journal entries. |
| `POST /v1/webhooks/sandbox-fiat` | Payment-provider webhook (signature-verified, no API key). |
| `POST /v1/sandbox/orders/{id}/pay`, `/confirm` | Simulate the customer's payment and on-chain confirmations. |
| `GET /v1/review/queue`, `GET /v1/review/orders/{id}` | Reviewers only: held payments, longest-waiting first, with an overdue flag; full screening reasons and the review log. |
| `POST /v1/review/orders/{id}/approve`, `/reject`, `/freeze`, `/release` | Reviewer decisions. Each needs a `reason`; `/freeze` also needs the `reportReference` of the report filed. |
| `GET /v1/review/orders/{id}/webhooks` | Reviewers only: each merchant notification for the order, with its delivery state, attempts and last error. |
| `GET /v1/settlement/wallets`, `POST /v1/settlement/wallets` | The merchant's settlement wallets, newest first; register one (`network`, `address`, optional `label`). |
| `POST /v1/settlement/wallets/{id}/disable` | Withdraw one of the merchant's wallets. |
| `GET /v1/review/settlement-wallets`, `GET /v1/review/settlement-wallets/{id}` | Reviewers only: wallets waiting for review, with their screening and history. |
| `GET /v1/review/settlement-wallets/{id}/webhooks` | Reviewers only: the merchant notifications about a wallet, with their delivery state. |
| `POST /v1/review/settlement-wallets/{id}/approve`, `/reject`, `/disable` | Reviewer decisions on a wallet, each with a `reason`. A sanctioned wallet cannot be approved. |
| `GET /v1/review/ledger/balances` | Reviewers only: whole-ledger balances across all merchants. |
| `GET /v1/review/system` | Reviewers only: the state of the database backups (`Ok`, `Failed`, `Stale`, `WaitingForFirst` or `NotSetUp`, with the latest successful attempt and the last seven), the problems the alert check sees now, and the alert emails sent lately. |
| `GET /v1/review/frozen` | Reviewers only: frozen payments, oldest first, with the reference of the report filed. |
| `GET /v1/review/webhooks/failed?days=7` | Reviewers only: merchant notifications given up on in the last 1 to 90 days (7 by default), newest first. Merchants without a webhook URL are not listed. |
| `POST /v1/review/webhooks/{eventId}/retry` | Reviewers only: send a given-up notification again (`DELIVERY_NOT_FAILED` if it was delivered or is still being retried). |
| `GET /review` | The reviewer console (no data in the page itself; reviewers sign in on it). |
| `POST /v1/review/sign-in` | A reviewer signs in (`username`, `password`, `code`) and gets a session `token` for `Authorization: Bearer`, with when it expires. Any mistake gives the same `SIGN_IN_FAILED`; too many attempts from one address give `TOO_MANY_ATTEMPTS` (429). |
| `POST /v1/review/sign-out` | Ends the reviewer's session. |
| `GET /checkout/{link}`, `GET /checkout/{link}/status` | The customer's payment page and its status (no key: the signed link is the credential). |
| `POST /checkout/{link}/pay` | Sandbox only: the customer's simulated payment, then back to the page. |
| `GET /v1/review/reconciliation/runs?day=yyyy-MM-dd&limit=30` | Reviewers only: reconciliation runs, newest first, with every difference found. Both parameters are optional. |
| `POST /v1/review/reconciliation/runs` | Reviewers only: reconcile a finished day now (`{"day":"yyyy-MM-dd"}`). |

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
| `src/FiatCryptoGateway.Api` | HTTP API, API-key and reviewer-session authentication, the checkout page, the reviewer console, background workers. |
| `src/FiatCryptoGateway.ReviewerSetup` | The command that creates a reviewer account's settings and authenticator QR code. |
| `Dockerfile`, `docker-compose.yml`, `deploy/` | The container image, the database + API + HTTPS proxy + backup stack, the proxy's configuration (`deploy/Caddyfile`), the backup image and scripts (`deploy/backup`), and the container and backup tests. |
| `db/` | PostgreSQL migration, runner and constraint tests. |
| `docs/` | [Architecture](docs/ARCHITECTURE.md), [decisions](docs/decisions.md), the plan for [our own payment gateway](docs/own-gateway.md), the [compliance backlog](docs/compliance-backlog.md) (what stands between the sample and real money, with an owner for each item) and the older [partner integration design](docs/partner-integration.md). |
