# Security policy

## Safety boundary

This build is sandbox-only. Every provider is simulated, and the API validates at startup that
`Gateway:Mode` is `Sandbox` and `Gateway:AllowRealFundsMovement` is `false`; anything else stops the
process. Connecting real money requires new, separately reviewed provider adapters, licences and an
explicit change to this policy.

## Secrets

- Never commit API keys, webhook secrets, provider credentials, private keys, wallet seeds or `.env` files.
  CI runs a secret scan on every push and pull request.
- API keys are stored only as SHA-256 hashes (`Gateway:ApiKeys`), compared in constant time. Issue a
  separate key per client and rotate by adding the new hash before removing the old one.
- Give each reviewer their own key with the `Reviewer` role, so the review log shows who decided. The reviewer
  console (`/review`) keeps the key only in the page's memory. In production, consider making `/review` and
  `/v1/review` reachable only from an internal network or a VPN.
- Webhook secrets come from configuration. Signatures are verified over the raw body before parsing, with
  a 5-minute replay window; several signatures may be active during rotation.
- In any deployed environment, keep secrets in a managed secret store, not in files.

## Data

- Store references to KYC results, not identity documents; those stay with the KYC provider.
- Do not log full request bodies, API keys, signatures, wallet seeds or personal data.
- Sanctions and wallet-screening findings are never returned to API clients in detail (`ORDER_NOT_PERMITTED`),
  to avoid tipping off a customer under suspicion.

## Money integrity

- The ledger and order history are append-only, enforced by database triggers; corrections are reversing
  entries. The production database user should additionally be denied UPDATE, DELETE and TRUNCATE on
  history tables.
- Provider calls that move value (buy, send, refund) must be idempotent per order ID.

## Reporting a vulnerability

Do not open a public issue for a vulnerability or include secrets in one. Report it privately to the
repository owner, rotate any credential that may be exposed, and keep only redacted evidence.
