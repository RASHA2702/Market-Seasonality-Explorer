# Compliance backlog: what stands between the sample and real money

**WORKING ASSUMPTIONS pending written advice from an Indian fintech lawyer and a chartered accountant. Not legal or tax
advice.** Every rule, figure and period below is as you gave it (see [own-gateway.md](own-gateway.md) and
[decisions.md](decisions.md)); I have not checked any of them. These are **backlog items, not code**, unless the status
says that something is built.

Last updated: 2026-11-28.

## How to read it

- **Owner** is who is accountable for getting the item done, then who must help. Owners are **roles until you name
  people**; each name is yours to fill in.
  - **You**: the founder, and the compliance officer for now (decisions.md, question 6).
  - **Lawyer** and **Accountant**: the Indian fintech lawyer and the chartered accountant you will engage.
  - **Board**: the board of directors, which approves policies.
  - **Engineering**: code and tests in this repository, which I do.
  - **Assessor**: a PCI DSS assessor, to be appointed.
  - **Partner**: the bank or licensed payment aggregator, if that route is chosen.
- **Blocks** is what may not happen until the item is done: **real money** (any real card, UPI or bank payment), **real
  crypto** (any real stablecoin settlement), **real merchants**, **public use** (a public name, domain or announcement) or
  **launch**.
- **Status** says what exists today, so nothing is claimed that is not built.
- No item is done until the adviser named in its owner column has confirmed it in writing.

## A. Moving rupees

| ID | Launch blocker | Blocks | Owner | Status and next step |
|----|----------------|--------|-------|----------------------|
| A1 | **Payment aggregator route.** Either RBI payment aggregator authorisation (non-banks: ₹15 crore net worth at application and ₹25 crore by the end of year 3, as you gave them), or a licensed payment aggregator or bank as merchant of record that holds the money. We do not hold customer money ourselves. | Real money | You (choose the route); Lawyer (confirm the category, figures and dates) | Decision needed. The guards that keep the gateway in the sandbox are built (section G). |
| A2 | **PCI isolation.** Card fields on an isolated, certified origin, or a certified provider's hosted fields. Our own card fields count as handling card data. | Real money (cards) | Engineering (build, once A1 is chosen); Assessor (confirm the scope) | Not started. Today the card form is on our own page and takes only published test cards; any other number is refused and never stored. |
| A3 | **Escrow account with a bank**, only if we ever hold funds ourselves. Not needed on the licensed-partner route. | Real money, if we hold funds | You; Lawyer; the bank | Not started. |
| A4 | **Settlement timelines**, only if we hold funds ourselves: how soon merchants must be paid, and what we promise them. | Real money, if we hold funds | Lawyer (what the rules require); You (decide); Engineering (settings) | Not started. Payout scheduling is milestone M4. |
| A5 | **Grievance officer.** A named person, with a published contact and response times, shown on the checkout page and in merchant documentation. | Real money | You (name the person); Lawyer (what must be published) | Not started. The checkout page shows no grievance contact today. |
| A6 | **Incident reporting.** A written process: what counts as an incident, who is told (regulator, partner, merchants, customers), how fast, and a record of each. | Real money | You (name an incident lead); Lawyer (what, to whom, how fast) | Not started. Alerts email people about stuck payments (built 2026-11-28), failed backups and the like; that is detection, not reporting. |
| A7 | **Merchant verification (KYB).** Business checks on every merchant before it takes real payments, with a higher limit tier only after full KYB. | Real merchants | You (policy); Engineering (tier settings in M2, onboarding in M6); a KYB vendor to choose (question 18) | Not started. |

## B. Crypto settlement (VDA)

| ID | Launch blocker | Blocks | Owner | Status and next step |
|----|----------------|--------|-------|----------------------|
| B1 | **FIU-IND registration** as a virtual digital asset service provider (assumed). | Real crypto | You (apply); Lawyer (confirm that it applies, and the steps) | Not started. |
| B2 | **Principal officer**, named. | Real crypto | You (appoint); Lawyer (what the role requires) | Not started. Today you act as compliance officer for review decisions (question 6). |
| B3 | **Suspicious-transaction process:** how a case is spotted, reviewed, reported and recorded. | Real crypto | You (as compliance officer); Lawyer (what is filed, with whom, how fast); Engineering (tooling) | Partly built: held payments, the reviewer console, freeze-and-report with a report reference, and a log that cannot be changed. Filing a report is not built. |
| B4 | **KYC of paying customers on the crypto leg, and sanctions screening.** | Real crypto | Lawyer (who must do it when a partner sends the crypto); Partner or a vendor to choose (question 18) | Partly built: simulated KYC levels with limits, and simulated wallet and sanctions screening. No real vendor. |
| B5 | **TDS under section 194S** (1%, may arise on the crypto leg): who deducts it in our flow, and a configurable deduction line on the ledger, off in sandbox. | Real crypto | Accountant (who deducts, when, how it is filed); Engineering (the ledger line) | Not started. The question to the accountant is open. |
| B6 | **Merchant tax note:** merchants bear 30% tax on VDA income with no loss set-off, stated in merchant documentation as information, not advice. | Real merchants | Accountant (the wording); Engineering (the documentation) | Not started. |
| B7 | **Auditable conversion record:** each conversion's rate and time recorded and kept. | Real crypto | Engineering (check and complete); Accountant (what must be kept) | To check. Quotes carry a rate and a time; to confirm they are kept with each conversion in the ledger work. |

## C. Policy, privacy and data

| ID | Launch blocker | Blocks | Owner | Status and next step |
|----|----------------|--------|-------|----------------------|
| C1 | **AML/KYC policy** (question 6): board approved; KYC, KYB, sanctions screening, monitoring, suspicious-transaction reporting and 5-year record keeping, drafted from the RBI KYC Direction. | Real money; real crypto | Board (approve); Lawyer (review); Engineering (first draft, when you ask) | Not drafted. |
| C2 | **DPDP privacy notice**, shown to customers and merchants. | Real merchants; public use | Lawyer (content); You (publish); Engineering (links) | Not started. |
| C3 | **DPDP retention schedule:** what is kept, for how long and why. It has to fit the 5-year AML period and the 8-year accounting period. | Real merchants | Lawyer and Accountant (the periods); You; Engineering (enforcement) | Not started. Today the database keeps everything, and backups on the server are deleted after 14 days. |
| C4 | **DPDP deletion process:** how a person asks, what is deleted and what must be kept. | Real merchants | Lawyer (what is required); You (owns the process); Engineering (tooling) | Not started. Open point: the ledger, order history and logs cannot be changed or deleted by design, so a deletion duty may need another way, for example removing who it was but keeping the record. |
| C5 | **Payment data kept in India:** the database, the backups and every vendor that touches payment data. | Real money | You (choose hosts and vendors); Lawyer (confirm what counts) | Not started. The gateway runs locally today. |
| C6 | **Hosting in India:** provider and region, and whether to use its managed database and secret store (question 23). | Real money | You (choose); Engineering (deploy) | Decision needed. The container and HTTPS setup is built and tested. |
| C7 | **Encrypted off-site backups in India,** with the `age` private key held by two people. | Real money | You (name the two holders and choose the storage); Engineering (set up the off-site copy) | Partly built: nightly encrypted backups, each restored and checked before it is kept, with an optional off-site copy. No host, and no named key holders. |
| C8 | **A restore test every quarter,** with a record of each. | Real money | You (name who runs it); Engineering (a record of drills) | Partly built: the restore command exists. Nothing is scheduled and no drill is recorded. |
| C9 | **Review of customer-facing wording:** declines, holds, refunds, terms. | Public use | Lawyer | "Nothing was charged" is now said only where we know it is true (built 2026-11-28). The wording itself is for the lawyer to review. |

## D. Accounting and tax

| ID | Launch blocker | Blocks | Owner | Status and next step |
|----|----------------|--------|-------|----------------------|
| D1 | **Client funds are a liability, never revenue; our revenue is only the fee.** Ledger accounts "funds held for customers" and "payable to merchants", kept apart from our own funds. | Real money | Accountant (signs off the chart of accounts); Engineering (build) | Built 2026-11-28: the two liability accounts replace the old single one, and a test checks that customer money reaches revenue only through the named fee lines. The accountant still signs off the chart of accounts. |
| D2 | **GST at 18% on our fees,** the fee and the GST as separate lines on every settlement report and invoice. | Real money | Accountant (confirm the rate, the base, the invoice format and the registration); Engineering (settings and lines) | Not started; built after D1. Open: on top of the fee or inside it, and the spread. |
| D3 | **Credit notes:** a refund reverses the fee and its GST in the same period, with a credit-note record. | Real money | Accountant; Engineering | Not started. |
| D4 | **Interest or float income** treated as taxable service income, to be confirmed. | Real money, if we hold funds or earn interest | Accountant | The question is open. |
| D5 | **Records kept immutable for at least 8 years** (the accountant to confirm). | Real money | Accountant (confirm the period and what immutable must mean); Engineering (archive and retention); You | Partly built: the ledger, order and attempt histories, review decisions and logs cannot be changed or deleted in the database. No archive, no retention setting, and backups on the server are deleted after 14 days. |
| D6 | **Month-end reconciliation report:** customer funds in = merchant payable + fees + GST + refunds in flight. | Real money | Engineering (build); Accountant (format and sign-off) | Not started. The daily reconciliation against the processor's report exists. |

## E. Naming, cover and launch

| ID | Launch blocker | Blocks | Owner | Status and next step |
|----|----------------|--------|-------|----------------------|
| E1 | **Trademark search** before any public use of the name or domain (question 22). | Public use | Lawyer; You | Not started. The placeholder `gateway-processor` stays until it is cleared. |
| E2 | **Repository rename,** only after E1, keeping a redirect (question 25). | Public use | You | Waits for E1. |
| E3 | **Alert cover:** alerts to at least two people, a named out-of-hours owner and an escalation path (question 27). | Launch | You (name the people and the path); Engineering (settings) | Partly built: email alerts with reminders and an all-clear. No people are named and nothing escalates. |
| E4 | **Morning check owner:** who looks at reconciliation differences, failed notifications and backup status each day. | Launch | You | Partly built: the console shows all three on one page. Nobody is named. |
| E5 | **Launch date.** None until A1 is settled. | Launch | You | Not set, on purpose. |

## F. Questions to put to your advisers in writing

**[Lawyer]**

1. Is the company a payment aggregator under the RBI Directions 2025 for the rupee flow? Which route fits: its own
   authorisation, or a licensed partner as merchant of record? Are the net-worth figures and dates right?
2. If we hold funds ourselves: which escrow, settlement-timeline, grievance-officer and incident-reporting rules apply?
3. Is the company a virtual digital asset service provider under the anti-money-laundering law when a licensed partner sends
   the crypto? What do FIU-IND registration, the principal officer and suspicious-transaction reporting require, and how
   fast?
4. PCI DSS: what is in scope, and does a certified provider's hosted card fields avoid it for real cards?
5. DPDP: what the privacy notice, the retention schedule and the deletion process must say, how they fit the 5-year AML
   period and the 8-year accounting period, and what must stay in India.
6. What the checkout page must show (terms, privacy notice, refund policy, grievance contact), and what a customer may be
   told when a received payment is refunded after a hold.
7. Review of the draft AML/KYC policy, and the trademark search for the name and domain.

**[Accountant]**

1. Who deducts the 1% TDS under section 194S in our flow, when, and how is it filed?
2. GST at 18% on our fees: on top of the fee or inside it? Does it apply to the spread? Which invoice format and
   registration? How are credit notes shown?
3. Is interest or float income taxable service income?
4. How should customer money in transit and merchant payables appear in the books, and which chart of accounts for the two
   ledger accounts?
5. Is 8 years right, and what does immutable have to mean?
6. The wording of the 30% VDA tax note for merchants.
7. The format of the month-end reconciliation report.

## G. Safeguards that stay

You asked that no sandbox-only safeguard be removed. Each is built and has tests, so a test fails if one stops working.

| Safeguard | What it does | Tests |
|-----------|--------------|-------|
| Safety gate | Startup refuses any `Gateway:Mode` but Sandbox, and any `Gateway:AllowRealFundsMovement` but false. | `StartupSettingsTests.ProductionSettings_StopStartup` |
| Live credential guard | Startup refuses a provider setting that is not Test, is not on its declared HTTPS test host, or carries a key that looks live. | `LiveCredentialGuardTests`, `StartupSettingsTests.LiveProviderSettings_StopStartup` |
| Payment rail guard | Startup refuses any payment network that is not a simulated one, two for one method, or none. | `ProcessorSettingsTests` (`ARailThatIsNotASandboxRail_IsRefused_ByName`, `TheGateway_RefusesToStart_WithARailThatIsNotASandboxRail`) |
| Test instruments only | Only the published test cards and UPI addresses are accepted. Any other number, even a valid-looking one, is refused before it is stored or logged. | `SandboxPaymentRailTests`, `PaymentProcessorTests`, `CheckoutPaymentTests` |
| No card number kept | A stored payment instrument holds a brand, the last four digits and a label; the code refuses a label or brand that looks like a card number. A test pays with a card and searches every response and log line for what was typed. | `CheckoutPaymentTests` |
