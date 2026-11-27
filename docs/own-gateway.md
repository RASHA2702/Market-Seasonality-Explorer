# Our own payment gateway (PROPOSED)

**Direction decided 2026-11-27:** "we should be independent of [the payment partner] and create our very own payment
gateway. For now scale it as a sample project; we can scale it as a startup once we can complete the project."
Everything below is **PROPOSED**: it applies until you change it. It replaces the partner-first plan for collecting
rupees (questions 3 and 11 in [decisions.md](decisions.md)); [partner-integration.md](partner-integration.md) is kept
for later.

## What it means

- **A sample project first.** The gateway stays **sandbox only**: no real money, no real card numbers, no live
  credentials. That rule from your brief does not change.
- **The software is entirely ours.** Checkout, payment methods, customer authentication, risk checks, authorising and
  capturing payments, refunds, disputes, merchant balances and settlement, reconciliation and the admin console are
  built and tested here, with our own rules and our own ledger. Nothing in the design waits for an outside company.
- **One thing software cannot do alone.** Reaching real money networks (UPI, card networks, banks) needs an agreement
  with a bank or licensed provider and the right authorisations, whoever writes the code. So each network is a *rail*:
  one small interface, with a simulated sandbox rail now. Going live later means writing one real adapter per rail and
  getting the licences (see "Beyond the sample" below), not rebuilding the gateway.

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
- **Card details stay on our page.** Merchants never see them. Later, the card form moves onto its own isolated origin,
  as hosted card fields do, to keep the part of the system that touches card data as small as possible.
- **Sandbox rails only.** A rail declares whether it is a sandbox one, and startup refuses any that is not, as it
  already refuses any mode but Sandbox.

## Milestones

Each is built in small commits with tests, mutation checks and a real run, and ends with a summary of what changed
and what depends on you.

| # | Milestone | Done when |
|---|---|---|
| M1 | **Processor core, with cards and UPI (test mode).** Payment attempts and their history; test-card and UPI catalogues; sandbox card and UPI rails; an OTP-style authentication step; the processor as the payment provider; checkout collecting payment details; its own settlement report. | A customer pays a rupee order by test card (with the OTP step) or UPI on our own checkout and the order proceeds exactly as today. Declines and failed authentication are handled. Reconciliation matches. |
| M2 | **Risk rules and declines.** Velocity and amount limits, country and card rules, a clear set of decline reasons, and risky payments held for the reviewer. | A suspicious payment is held, shown in the console and decided like any other. |
| M3 | **Refunds, partial captures and disputes.** Chargeback intake, evidence, and outcomes handled in the console. | A disputed payment is traced from the customer's claim to its outcome in the ledger. |
| M4 | **Merchant balances, fees and payout schedule.** Our fee rules, what each merchant is owed and when, settled as built (stablecoins). | A merchant's balance, fees and next settlement are shown and reconcile to the ledger. |
| M5 | **Netbanking and bank transfer.** Two more rails. | All four methods you chose work end to end. |
| M6 | **Merchant onboarding and dashboard.** Business verification, API keys and settings managed in the product, not in settings files. | A new merchant is onboarded, goes through verification and takes a test payment without anyone editing configuration. |
| M7 | **Startup readiness pack.** Security review, load test, monitoring, and the licensing and compliance checklist below turned into a plan. | A written list of what remains between the sample and a first real merchant. |

## Beyond the sample: for your lawyer and accountant

These are questions to take to them, not advice and not decisions. None blocks the sample project.

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

## Assumptions I am working on

Tell me if any is wrong; each changes what I build next.

1. **Sandbox only** (test cards and addresses; real numbers refused), as above.
2. **The crypto side stays simulated for now**, as built; only the rupee collection becomes ours.
3. **Cards and UPI first** (M1), because they are the most common in India and exercise the most (tokens, OTP,
   approval); netbanking and bank transfer follow in M5.
4. **A placeholder name** for the processor (provider ID `gateway-processor`) until you choose a brand (question 22).
5. **The existing simulated partner provider stays** for the current tests and examples until the processor replaces
   it, then is retired.
