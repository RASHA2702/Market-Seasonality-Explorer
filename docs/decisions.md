# Decisions

What has been decided for the crypto payment gateway, what is proposed, and what is still open.

- **ANSWERED**: you decided. Your own words are quoted where you gave them.
- **PROPOSED**: my suggested default and the reason for it. It applies until you change it.
- **OPEN**: needed from you.

**[Lawyer]** and **[Accountant]** mark points to take to your lawyer or accountant. Nothing in this
file is legal or tax advice.

Last updated: 2026-11-25.

## Still needed from you

- Question 11: choose a partner from the shortlist, then create its test-environment API keys. The questions
  to ask each candidate, and the settings you will provide, are in [partner-integration.md](partner-integration.md).
- Question 6: a written AML/KYC policy.
- Question 22: brand name and domain.
- Question 25: renaming the repository.
- Question 27: launch date, out-of-hours cover, and who checks reconciliation differences each morning.
- Question 17: confirm the 24-hour cooling-off period for new settlement wallets, and that risky wallets may be
  approved by you (sanctioned ones never can).
- The points marked **[Lawyer]** and **[Accountant]**.

## ★ Product scope

### 1. Which flows, in what order?
**ANSWERED**: "No just as a payment gateway but with crypto." A payment gateway for merchants, not a
consumer on-ramp.
- **ANSWERED** (2026-11-24): the customer can choose. Customers pay in INR or in stablecoins, and each
  merchant picks whether it is settled in INR or in stablecoins.
- **PROPOSED** build order: (1) customer pays INR, merchant receives stablecoins, which is closest to what
  is built; (2) customer pays stablecoins, merchant receives stablecoins; (3) merchants settled in INR.
- **[Lawyer]** INR in and INR out is ordinary payment aggregation (RBI rules), and paying a merchant INR for
  a customer's crypto is an off-ramp. Each may need its own licence or a licensed partner.

### 2. Who are the customers, and what is the interface?
**ANSWERED** (2026-11-24): merchants, through an API plus a checkout page that the gateway hosts. Their
customers pay on that page.
- Built: signed webhooks tell each merchant about every status change it may see, in order, with retries
  (compliance steps appear only as "on hold").
- Built: the hosted checkout page. Each order links to it; the customer sees the merchant, the amount and the
  deadline, picks a payment method and pays (simulated in the sandbox; the partner's payment step once one is
  chosen). Customers see only waiting, received, refund started, refunded or expired.
- **[Lawyer]** What the checkout page must show a customer (for example terms, privacy notice, refund policy and a
  grievance contact under the rules for payment aggregators and consumer protection), and what a customer may be
  told when a received payment is later refunded after a compliance hold.

### 3. Operate as a regulated operator, or partner with licensed providers first?
**ANSWERED**: "Yes partners." Start by partnering with licensed providers.

### 4. Launch markets and currencies, and the payment partner and price source for each
**ANSWERED** (part): "India for now later all over the world"; currency "INR".
- Payment partner: see question 11.
- Price source: **PROPOSED**: the partner's quote. The partner fills the order, so its rate is the one
  that counts. The sandbox price feed stays for development and tests.

## ★ Legal and compliance (before any real money)

### 5. Where is the company incorporated, and where are the customers?
**ANSWERED**: customers in India now, worldwide later. The company is incorporated in India (2026-11-24).
- **[Lawyer]** Which registrations your company needs (for example with FIU-IND under PMLA as a virtual
  digital asset service provider) depends on what your company itself does when a licensed partner
  handles the money and the crypto.

### 6. Who is the compliance officer (MLRO)? Is there an AML/KYC policy? Who reviews held orders, and how fast?
**ANSWERED** (2026-11-24): you act as the compliance officer for now and review held payments within 24 hours.
- Built: the review queue shows how long each held payment has waited and flags any older than 24 hours;
  you use a reviewer API key, and every decision is logged with your name and reason.
- Written AML/KYC policy: **OPEN**.
- **[Lawyer]** Whether your company must formally appoint officers (for example a principal officer and a
  designated director under PMLA) and what its written policy must cover.

### 7. What is the real sanctions and blocked-country list, and what happens to money from a rejected order?
**ANSWERED** (part): money from a rejected order is frozen and reported ("Freeze and report it").
- Built: a rejected payment's money moves to Frozen when you record the report reference; it can only be
  refunded through a later, reasoned release.
- **ANSWERED** (2026-11-24): Indian residents only. The gateway uses an allow-list containing only India and
  refuses everyone else. This replaces the placeholder blocked list (CU, IR, KP, SY).
- Built: the country rule is an allow-list containing only India by default. Configuration replaces it, for
  example `Gateway__Compliance__Countries=IN,AE`, or switches to a block-list.
- Individual sanctions screening of payers is done by the partner (questions 17 and 18).
- **[Lawyer]** Who files the report, and with whom, when the partner holds the money.

### 8. Travel Rule: which transfers, and which provider?
**ANSWERED** (2026-11-24): the partner handles Travel Rule data on the transfers it makes. Your own provider
can be added later.
- **[Lawyer]** Confirm that the partner's handling covers your obligations.

### 9. Data protection and residency
**ANSWERED** (2026-11-24): personal and payment data is stored in India only.
- When the gateway moves off your machine, its database and backups go in an Indian cloud region, and the
  partner must confirm it also keeps the data in India.
- **[Lawyer]** India's DPDP Act, and any payment-data storage rules that apply to you or your partner.

## ★ Collecting fiat

### 10. Payment methods and local rails
**ANSWERED** (2026-11-24): INR by UPI, debit and credit cards, netbanking, and bank transfer
(IMPS, NEFT, RTGS).
- Cards can be charged back, so the card policy in question 11 applies.
- **PROPOSED**: bank transfers carry a unique payment reference so each transfer is matched to exactly one
  payment; transfers without a matching reference, or for the wrong amount, are refunded.

### 11. Payment partner, and a chargeback and fraud policy for cards
**ANSWERED** (part): "A payment provider." Not chosen yet; you asked for a shortlist (2026-11-24).
Which one: **OPEN**.

Shortlist to contact. Each claim below comes from the provider's own or a comparison website and is
**unverified**. Nothing has been signed up for.

| Candidate | Why it is on the list (unverified) |
|---|---|
| OnMeta | API-first on-ramp and off-ramp; INR by UPI and bank transfer; says it has an FIU-registered entity. |
| Transak | Widget and API; lists UPI for India. |
| TransFi | Merchant checkout taking INR, cards and USDT through one integration. |
| Onramp Money (Buyhatke Internet Pvt Ltd) | Named as FIU-IND registered in comparisons; INR payouts by IMPS, NEFT and RTGS, but with a high minimum (reported 15,000 USDT) for some flows. |
| GSX Solutions | Listed in Circle's partner directory; says it is an FIU-registered INR settlement layer for stablecoins. |

Questions to put to each: proof of FIU-IND registration (check it against the government's published
list; a parliamentary answer reported 54 registered providers as of 9 March 2026); which INR methods
(UPI, cards, netbanking, bank transfer) and which coins and networks from question 13; whether they can
settle merchants in stablecoins and in INR; payer KYC, wallet screening and Travel Rule handling; data kept
in India; quote-lock length; minimum and maximum amounts; fees; and a test environment with API keys.
- Built: daily reconciliation against each provider's settlement report. The chosen partner must provide a
  daily report (by API or file) that lists each movement with the same IDs its webhooks use; ask each candidate
  for its format, timing and the time zone of its day.
- Card policy: **PROPOSED** (cards were chosen in question 10): release stablecoins only after the provider
  confirms the payment is captured and 3-D Secure authenticated, and apply lower limits to card payments,
  because a card payment can be charged back weeks later while a crypto transfer cannot be recalled.

### 12. Price-lock window per payment method, and who absorbs price moves
**ANSWERED** (part): "we will use stable crypto currencies." Stablecoins track the US dollar, so the
remaining exposure is the INR/USD rate.
- **ANSWERED** (2026-11-24): use the partner's price lock. The customer sees the partner's locked rate; if it
  expires before payment, the gateway asks for a new quote. Your company takes no price risk.
- The payment window therefore follows each partner quote's expiry. The sandbox keeps 15 minutes.

## ★ Crypto side

### 13. Coins and networks first
**ANSWERED** (2026-11-24): USDT on Tron; USDT and USDC on Polygon; USDT and USDC on Ethereum; USDC and USDT
on Solana. The final list depends on what the partner supports.
- Network fees on these are paid in TRX, POL, ETH and SOL respectively.
- Built: all seven stablecoins and the four networks are in the sandbox, with each network's address format.
  The sandbox prices, fees and confirmation counts are illustrative; the real ones come from the partner.

### 14. Liquidity venue (exchange or OTC)
**ANSWERED**: "We need rethink or redesign that part from what is there now." Then (2026-11-24): your company
never holds crypto.
- So there are no exchange or OTC accounts of your own: the licensed partner buys, sells and receives the
  stablecoins.

### 15. Custody, with hot and cold wallet limits
**ANSWERED** (2026-11-24): never hold crypto. The partner sends stablecoins, or INR, straight to the
merchant, and receives customers' stablecoins at its own deposit addresses. There are no keys, hot wallets
or cold wallets to manage, so wallet limits do not apply.
- This replaces the sandbox's own liquidity and custody steps (see the plan in Step 3).

### 16. Confirmation counts per network, and the node or data provider
**PROPOSED**: the partner reports when a transfer is final, and the gateway also applies a configurable
confirmation count per network. Starting values, to be confirmed with the partner: Ethereum 12, Polygon 128,
Tron 20, Solana 32 (Solana's "finalized" level). No node or data provider of your own is needed while the
partner sends and receives the transfers.

### 17. Wallet-screening vendor
**ANSWERED**: "Yes" (you want screening vendors). Then (2026-11-24): at launch the partner screens wallets.
Your own vendor (for example Chainalysis, TRM or Elliptic) can be added later behind the gateway's existing
wallet-screening interface.
- Built: merchants' settlement wallets are screened when registered (see question 1: merchants choose stablecoin
  settlement). **PROPOSED** defaults, all configurable:
  - a clean wallet can receive payments only after a **24-hour cooling-off period**, so a merchant can disable a
    wallet it did not add (for example one added with a stolen API key);
  - a risky wallet, or one whose screening could not finish, waits for you to approve or reject it, with a reason;
    a sanctioned wallet can only be rejected;
  - one wallet per network, and a replacement takes over only when it becomes usable;
  - the merchant is notified by webhook of every registration, decision and withdrawal, so it can disable a wallet
    it did not add while that wallet is still cooling off;
  - orders pay out only to the merchant's active wallet for the order's network, which is screened again on every
    order.
- **[Lawyer]** Whether the gateway (or the partner) must prove that a merchant controls its settlement wallet
  (self-hosted wallet rules under the Travel Rule), and what may be said to a merchant whose wallet is declined.
  Merchants currently see only "UnderReview" and "NotAccepted".

## Customer verification

### 18. KYC/KYB vendor
**ANSWERED**: "Yes" (you want verification vendors).
- **PROPOSED**: the licensed partner verifies paying customers (KYC); the gateway verifies merchants (KYB)
  through a vendor chosen later.

### 19. Real tiers and limits, including daily and monthly totals per customer
**ANSWERED**: "Yes" (daily and monthly limits wanted).
- **PROPOSED** placeholder values in US dollars until your compliance policy sets real ones:

  | KYC level | Per payment | Per day | Per month |
  |---|---|---|---|
  | Basic | 1,000 | 2,000 | 5,000 |
  | Full | 10,000 | 20,000 | 50,000 |

  Payments of 5,000 or more are held for a person to review.
- Built: these limits are enforced per customer over a rolling 24 hours and 30 days, ignoring expired and
  refunded orders. Each value is a setting (for example `Gateway__Compliance__BasicKycDailyLimit`).

## Commercials

### 20. Fees, spread, minimum fee, network-fee payer, minimum and maximum order
**ANSWERED**: "none for now" (nothing decided yet).
- **PROPOSED**: everything stays configurable. The sandbox keeps a 1.5% fee and 0.5% spread so the fee
  maths stays tested, and the 10 to 10,000 USD order range stays as a placeholder. The partner's network fee
  is shown to the payer as a separate line.
- **[Accountant]** How fees, spread and any crypto movements are taxed and invoiced, including TDS on
  crypto transfers.

### 21. Expected volume
**ANSWERED**: "none for now" (no estimate yet).
- **PROPOSED**: build for low volume now, in a way that scales: a stateless API with all state in
  PostgreSQL, so more instances can be added later.

## Product and engineering

### 22. Customer experience, brand name and domain
**ANSWERED** (part): a hosted checkout page plus an API for merchants (question 2).
- Brand name and domain: **OPEN**. The checkout page shows the merchant's name and no gateway brand yet; once a
  domain is chosen, it becomes `Gateway:Checkout:BaseUrl`.

### 23. Hosting (cloud, region, budget)
**ANSWERED**: "for now locally but we would have to scale up in the future if needed."
- **PROPOSED**: run locally with Docker Compose. When you move to the cloud, choose a region in India
  (see question 9).

### 24. Confirm the stack (.NET 10 + PostgreSQL)
**ANSWERED** (you asked me to decide): keep .NET 10 and PostgreSQL. The gateway is already built and tested
on them; .NET's `decimal` gives exact money arithmetic; .NET 10 is a long-term support release; and
PostgreSQL's transactions and constraints are what keep the ledger correct.

### 25. Repository: rename or move?
**OPEN**.
- **PROPOSED**: rename `Market-Seasonality-Explorer` on GitHub (Settings, General, Repository name), for
  example to `crypto-payment-gateway`. Only you can do this; GitHub redirects the old links.

### 26. QuantFOPlatform: keep developing it, or pause it?
**ANSWERED** (you asked me to decide): pause it. It is in a stable state (it builds, 60 tests pass and CI is
green on its branch), so nothing is lost, and work can resume at any time.

### 27. Operations and timeline
**OPEN**: launch date, and who covers incidents, reviews and refunds around the clock.
- Built: daily reconciliation records every difference between providers' reports and the books, and merchant
  webhooks record deliveries that failed. Someone needs to check both each morning. A payment missing from the
  books means a customer has paid for an order that has not moved.
