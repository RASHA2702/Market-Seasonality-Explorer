# Our own payment gateway

**Status, 2026-11-28:** milestone 1 is accepted, and the five assumptions at the end of this file are **confirmed**. The
legal, crypto-settlement and accounting positions under "Working assumptions" are **WORKING ASSUMPTIONS**: defaults to
design and build against, **pending written advice from an Indian fintech lawyer and a chartered accountant**. They are
**not legal or tax advice**. The rules and figures in them are as you gave them; I have not checked them against the rules
themselves, and nothing in them is final until the lawyer and the accountant confirm it in writing.

**Direction decided 2026-11-27:** "we should be independent of [the payment partner] and create our very own payment
gateway. For now scale it as a sample project; we can scale it as a startup once we can complete the project."
The plan below applies until you change it. It replaces the partner-first plan for collecting rupees (questions 3 and 11
in [decisions.md](decisions.md)); [partner-integration.md](partner-integration.md) is kept for later. What stands between
the sample and real money is listed, with owners, in [compliance-backlog.md](compliance-backlog.md).

## What it means

- **A sample project first.** The gateway stays **sandbox only**: no real money, no real card numbers, no live
  credentials. That rule from your brief does not change, and since 2026-11-28 it is a **hard rule**: no real card, UPI or
  bank money is accepted until either (a) we hold RBI payment aggregator authorisation, or (b) a licensed payment
  aggregator or bank is the merchant of record and holds the money. The startup guards that refuse any mode but Sandbox,
  live credentials and any payment network that is not a simulated one all stay. No sandbox-only safeguard is removed.
- **The software is entirely ours.** Checkout, payment methods, customer authentication, risk checks, authorising and
  capturing payments, refunds, disputes, merchant balances and settlement, reconciliation and the admin console are
  built and tested here, with our own rules and our own ledger. Nothing in the design waits for an outside company.
- **One thing software cannot do alone.** Reaching real money networks (UPI, card networks, banks) needs an agreement
  with a bank or licensed provider and the right authorisations, whoever writes the code. So each network is a *rail*:
  one small interface, with a simulated sandbox rail now. Going live later means writing one real adapter per rail and
  getting the licences (see "Beyond the sample" below), not rebuilding the gateway. The preferred route is a real bank's or
  licensed payment aggregator's adapter behind that interface, so the licensed partner holds the customer's money (see
  the legal position under "Working assumptions").

## What we keep and what changes

| Keep (already built and tested) | Change |
|---|---|
| Quotes, orders and the order state machine; the double-entry ledger | The "payment provider" stops being a simulation of a third party. Our own **processor** takes its place (`IFiatPaymentProvider`), with its own records. |
| Compliance checks, held payments, reviewer console and sign-in, alerts, backups | The hosted checkout collects the customer's payment details and authenticates the payment, instead of a Pay button that stamps a signed event. |
| Merchant settlement wallets, webhooks to merchants, reconciliation | A captured payment is recorded on the order directly, in-process. The signed webhook endpoint stays for any external provider added later. |
| The crypto side (simulated liquidity and custody, wallet screening) | The processor publishes its own daily settlement report, built from its records, for the existing reconciliation. |

The crypto side (questions 8, 14, 15 and 17) is unchanged for now: simulated, and still never held by the company. It
is the next place a licensed partner may be needed in a startup.

## Shape

```
Customer ─► hosted checkout (card | UPI | netbanking | bank transfer)
              │
              ▼
        Processor  ── instrument capture and tokenising ─► risk rules ─► customer authentication
              │
              ▼
        Rail (one per method; sandbox now, a real bank or network adapter later): authorise, capture, refund, report
              │
              ▼
        Processor records the captured payment on the order ─► the order flow as built (compliance, crypto settlement)
        Processor ledger: customer money in transit, merchant balance, fees ─► daily report ─► reconciliation
```

## Safety rules for payment details (sample)

- **Test cards only.** The processor accepts only a published list of test card numbers and test UPI addresses. Any
  other number, even a valid-looking one, is refused before it is stored or logged.
- **Never store or log a card number or security code.** Only a token, the last four digits and the brand are kept.
- **Card details stay on our page, for the sample only.** Merchants never see them. Our own card fields count as
  handling card data, so **no real card is ever accepted until the card fields sit on an isolated, certified origin, or
  on a certified provider's hosted fields.** That is a **hard launch blocker**; before 2026-11-28 it was "left out".
- **Card numbers, security codes and PINs are never stored**, and the design in which the gateway cannot store a card
  number stays. All payment data is kept in India.
- **Sandbox rails only.** A rail declares whether it is a sandbox one, and startup refuses any that is not, as it
  already refuses any mode but Sandbox and any live credential.

## Milestones

Each is built in small commits with tests, mutation checks and a real run, and ends with a summary of what changed
and what depends on you.

| # | Milestone | Done when |
|---|---|---|
| M1 | **Processor core, with cards and UPI (test mode).** Payment attempts and their history; test-card and UPI catalogues; sandbox card and UPI rails; an OTP-style authentication step; the processor as the payment provider; checkout collecting payment details; its own settlement report. | A customer pays a rupee order by test card (with the OTP step) or UPI on our own checkout and the order proceeds exactly as today. Declines and failed authentication are handled. Reconciliation matches. |
| M2 | **Risk rules and declines.** Velocity and amount limits, country and card rules, a clear set of decline reasons, and risky payments held for the reviewer. Every threshold is a setting. Limits start conservative: a low cap per payment, a daily cap per merchant, a velocity limit, and a higher tier only after full business verification (KYB). The accountant and lawyer set the final numbers. | A suspicious payment is held, shown in the console and decided like any other. |
| M3 | **Refunds, partial captures and disputes.** Chargeback intake, evidence, and outcomes handled in the console. | A disputed payment is traced from the customer's claim to its outcome in the ledger. |
| M4 | **Merchant balances, fees and payout schedule.** Our fee rules, what each merchant is owed and when, settled as built (stablecoins). | A merchant's balance, fees and next settlement are shown and reconcile to the ledger. |
| M5 | **Netbanking and bank transfer.** Two more rails. | All four methods you chose work end to end. |
| M6 | **Merchant onboarding and dashboard.** Business verification, API keys and settings managed in the product, not in settings files. | A new merchant is onboarded, goes through verification and takes a test payment without anyone editing configuration. |
| M7 | **Startup readiness pack.** Security review, load test, monitoring, and the licensing and compliance checklist below turned into a plan. | A written list of what remains between the sample and a first real merchant. |

### M1 result (built 2026-11-27, accepted 2026-11-28)

M1 is built, tested and accepted; M2 is next. It was done in small commits, each with tests, mutation checks and a
real run.

- **Payment attempts.** Every try to pay an order is kept with an append-only history in PostgreSQL. An order has one
  captured payment: the code and the database both refuse a second.
- **Test instruments only.** Simulated card and UPI networks accept the published test cards and addresses; anything
  else, even a valid-looking number, is refused and never kept. Startup refuses any rail that is not a sandbox rail.
- **The processor.** It takes what the customer typed, asks the network, handles the bank's one-time password or the UPI
  approval, takes the money and tells the order. A background sweep finishes what a crash or an outage left half
  done, and no step books a payment twice.
- **The payment provider by default.** Refunds, the daily settlement report and reconciliation all work through it. The
  earlier simulated partner is still selectable (`Gateway:Fiat:Provider=SandboxPartner`) and keeps its tests.
- **The hosted checkout.** It takes typed UPI and card payments, explains declines in plain words, and never puts what
  was typed back into a page, an address or a log.

Left out on purpose, and where it goes:

- Netbanking and bank transfer: M5.
- A currency check on each rail (a real UPI rail takes rupees only): when the first real rail is built.
- An email when a payment has been stuck for long: built 2026-11-28 (see the change log). Before, it was only logged.
- Card details on their own isolated origin, like hosted card fields: a **hard launch blocker**. No real card number is
  accepted before it is done ([compliance-backlog.md](compliance-backlog.md)).

## Beyond the sample: for your lawyer and accountant

These are questions to take to them, not advice and not decisions. None blocks the sample project. Several now have a
working position to design around (next section) until they answer in writing.

- **[Lawyer]** Whether collecting rupees from customers and settling merchants makes the company a *payment aggregator*
  that needs RBI authorisation, as opposed to a technology-only gateway; what capital, escrow or nodal-account
  rules would then apply.
- **[Lawyer]** How the company would reach UPI, cards and netbanking (usually through a sponsor bank or licensed
  provider), and what each agreement requires.
- **[Lawyer]** Merchant verification (KYB), anti-money-laundering duties and grievance handling that apply to a gateway,
  on top of the crypto-side points already listed.
- **[Lawyer]** Where payment data must be stored (see question 9), and chargeback and dispute rules.
- **[Lawyer]** PCI DSS: it applies to whoever stores, processes or transmits card data. Whether and at what level it
  would apply, and whether to avoid it by using a certified card-vaulting provider for real card data.
- **[Accountant]** GST on gateway fees; how customer money in transit differs from revenue in the books; the tax
  treatment of settling merchants in crypto, including any tax deducted at source on virtual digital asset transfers.

## Working assumptions (recorded 2026-11-28)

**WORKING ASSUMPTIONS pending written advice from an Indian fintech lawyer and a chartered accountant. Not legal or tax
advice.** The rules, figures and section numbers below are as you gave them. I have not checked them, and I am not
qualified to. Each one is a default to design and build against. Each has an owner and a status in
[compliance-backlog.md](compliance-backlog.md); **[Lawyer]** and **[Accountant]** mark who confirms it. The working
answers to questions 6, 17, 19, 22, 23, 25 and 27 are in [decisions.md](decisions.md).

### Legal position to design around

- **Payment aggregator activity.** Collecting rupees from customers and settling merchants is assumed to be payment
  aggregator (PA-O) activity under the RBI Directions 2025. Non-banks need authorisation, ₹15 crore net worth at
  application, and ₹25 crore by the end of year 3. **[Lawyer]** Confirm the category, the figures and the dates.
- **Preferred route to market.** Plug a real bank's or licensed payment aggregator's adapter into the existing rail port
  (`IPaymentRail`), so the licensed partner holds customer funds. **We do not hold customer money ourselves.**
- **If we ever hold funds ourselves**, these are needed before launch, each as a backlog item and not as code: an escrow
  account with a bank, merchant KYC/KYB, settlement timelines, a grievance officer, and incident reporting.
- **Data stays in India.** All payment data is kept in India. Card numbers, CVV and PINs are never stored; the existing
  design in which the gateway cannot store a card number stays.
- **PCI DSS.** Our own card fields count as handling card data. No real card is ever accepted until the card fields sit on
  an isolated, certified origin, or on a certified provider's hosted fields. This moved from "left out" to a **hard launch
  blocker**.
- **Privacy.** Users' data is treated as personal data under the DPDP Act. A privacy notice, a retention schedule and a
  deletion process are backlog items.
- **Customer wording.** "Nothing was charged" is said only where we know it is true. Otherwise the page says "we could not
  confirm a charge; if you were debited, it will be reversed by your bank".

### Crypto settlement (VDA) position

- **A VDA service provider.** Assume the company is a virtual digital asset service provider under India's
  anti-money-laundering law: register with FIU-IND, name a principal officer, and run KYC and a suspicious-transaction
  process. This is a **launch blocker for any real crypto settlement**. **[Lawyer]** Confirm, including whether it still
  applies when a licensed partner sends the crypto.
- **Tax deducted at source.** 1% TDS under section 194S may arise on the crypto leg. It is to be modelled as a
  configurable deduction line on the ledger, **off in sandbox**. **[Accountant]** Who deducts it in our flow: the gateway,
  a partner, the customer, or nobody?
- **Merchant tax.** Merchants bear 30% tax on VDA income, with no loss set-off. This goes in merchant documentation as
  information, not as advice. **[Accountant]** Confirm the wording.
- **Auditable conversion record.** Each conversion's rate and time is recorded and kept.

### Accounting treatment (default)

- **Customer money in transit is a liability** (client funds), never revenue. Our revenue is only the fee.
- **Customer funds are kept apart from our own** in the ledger, with an account for "payable to merchants" and one for
  "funds held for customers".
- **GST at 18% on our fees**, with the fee and the GST as separate lines on every settlement report and invoice. Interest
  or float income is treated as taxable service income, **to be confirmed**. **[Accountant]** Whether the GST is added on
  top of the fee (my default, so the merchant pays the fee plus GST) or included in it, and whether it applies to the
  spread as well as the service fee (my default: the service fee only).
- **Refunds reverse the fee and its GST in the same period**, with a credit-note record.
- **Immutable records for at least 8 years** (the accountant to confirm).
- **A month-end reconciliation report** shows: customer funds in = merchant payable + fees + GST + refunds in flight.

Building these into the ledger is under way: the two customer-money accounts are built (2026-11-28); the GST lines, credit
notes and the month-end report follow. What is built and what is not is in the change log and in
[compliance-backlog.md](compliance-backlog.md).

## The five assumptions: confirmed (2026-11-28)

1. **Sandbox only: confirmed, and a hard rule.** No real card, UPI or bank money is accepted until (a) we hold RBI payment
   aggregator authorisation, or (b) a licensed payment aggregator or bank is the merchant of record and holds the money.
   The startup guard that refuses non-sandbox payment networks stays.
2. **The crypto side stays simulated: confirmed.** No real crypto transfer happens until the VDA/AML position above is
   signed off. Only the rupee collection is ours.
3. **Cards and UPI first: confirmed** (M1), because they are the most common in India and exercise the most (tokens, OTP,
   approval); netbanking and bank transfer follow in M5.
4. **The placeholder name** `gateway-processor` stays. No brand or domain is used publicly until question 22 is decided.
5. **The simulated partner provider stays**, selectable by setting (`Gateway:Fiat:Provider=SandboxPartner`), until you say
   to retire it.
