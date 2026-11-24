# What is needed to go from sandbox to real money

The code can quote, account for and track orders correctly, but moving real money depends on
decisions, licences and partners that only the business can provide. Items marked ★ block the next
milestone. Nothing here is legal advice; confirm licensing and tax questions with a lawyer and an
accountant in each country you operate in.

## 1. Product scope ★

1. **Which flows, in what order?**
   - On-ramp: the customer pays fiat and receives crypto in their wallet. *(Built in the sandbox.)*
   - Off-ramp: the customer sends crypto and receives fiat in their bank account.
   - Merchant checkout: a shop's customer pays and the shop receives the other side (fiat or crypto).
2. **Who are the customers?** Consumers, businesses that integrate your API, or both? Will they use a
   hosted checkout page or widget, your own app, or the API only?
3. **Operate or partner?**
   - (a) Become the regulated operator: you hold the licences, bank accounts, liquidity and custody.
   - (b) Start by putting your brand and API in front of licensed on-ramp providers (for example
     MoonPay, Transak, Banxa, Ramp or Stripe's crypto onramp; check which serve your markets), then add
     your own rails later.

   Recommendation: (b) first. It can launch in weeks rather than the year or more that licensing,
   banking and custody usually take, and the provider interfaces in this code support either path.
4. **Launch markets and currencies.** The catalogue already knows all 156 ISO 4217 currencies, but each
   currency you accept needs a payment partner that collects it and a price source for it. Which
   countries and currencies come first?

## 2. Legal and compliance ★ (before any real money)

5. **Where is the company incorporated, and where will customers be?** This decides the licences.
   Examples to confirm with counsel:
   - India: registration with FIU-IND as a virtual digital asset service provider under PMLA, plus tax
     rules on crypto transfers (such as TDS) and RBI rules if you also act as a payment aggregator.
   - European Union: authorisation as a crypto-asset service provider under MiCA.
   - United States: FinCEN money services business registration and state money-transmitter licences.
   - United Kingdom: FCA registration for cryptoasset businesses.
6. **Compliance ownership.** Who is the compliance officer (MLRO)? Is there an AML/KYC policy? Who reviews
   orders held for review, and within what time?
7. **Sanctions and blocked countries.** The code blocks CU, IR, KP and SY as a placeholder. What is the
   real list? What happens to money from a rejected order: refund, or freeze and report?
8. **Travel Rule.** Which transfers must carry originator and beneficiary information, and through
   which provider?
9. **Data protection and residency.** Which privacy laws apply (for example GDPR, India's DPDP Act), and
   must data stay in a specific country?

## 3. Collecting fiat ★

10. **Payment methods.** Cards, bank transfers, local rails (UPI or IMPS in India, SEPA in the EU, ACH or
    RTP in the US, Faster Payments in the UK, PIX in Brazil, and so on)?
11. **Payment partner.** Do you have, or can you get, accounts with a provider or bank that allows crypto
    purchases? Many banks and card schemes restrict them. Card payments can be charged back months later
    while crypto cannot be recovered, so cards need strong fraud controls or a delay before release.
12. **Price risk.** The sandbox locks the price for 15 minutes and refunds anything later or of the wrong
    amount. Is that window right for your payment methods (bank transfers can take days)? Who absorbs a
    price move: you, or the customer through a re-quote?

## 4. The crypto side ★

13. **Which coins and networks first?** For example USDT on Tron or Ethereum, USDC on Ethereum, Solana or
    Polygon, BTC, ETH.
14. **Liquidity.** Which exchange or OTC desk will you buy from, with accounts in the company's name?
15. **Custody.** A third-party custodian (for example Fireblocks, BitGo or Coinbase Prime) or self-custody
    (HSM/MPC key management, key ceremonies, insurance)? Limits for hot versus cold wallets?
16. **Confirmations and chain data.** How many confirmations before an order counts as complete on each
    network (the sandbox uses 3 for Bitcoin, 12 for Ethereum, 1 for Solana)? Which node or data provider?
17. **Wallet screening.** Which blockchain-analytics vendor (for example Chainalysis, TRM Labs, Elliptic)?

## 5. Customer verification

18. **KYC/KYB vendor**, such as Sumsub, Onfido, Persona or Jumio, or a local provider.
19. **Tiers and limits.** The sandbox uses: basic KYC up to 1,000 USD per order, full KYC up to 10,000 USD,
    and human review from 5,000 USD. These are per order only. What are the real limits, and the daily
    and monthly totals per customer?

## 6. Commercials

20. **Fees.** Service fee (sandbox 1.5%), spread (0.5%), any minimum fee, and who pays the network fee
    (the sandbox charges the customer). Minimum and maximum order (sandbox 10 to 10,000 USD).
21. **Expected volume**: orders per day and typical size. This drives partner pricing tiers and
    infrastructure.

## 7. Product and engineering

22. **Customer experience.** Hosted checkout page, embeddable widget, mobile app or API only? Brand name
    and domain?
23. **Hosting.** Cloud provider and region (see data residency), and budget.
24. **Stack.** The gateway uses .NET 10 and PostgreSQL, the same as QuantFOPlatform. Is that what you want?
25. **Repository.** The gateway lives in the `Market-Seasonality-Explorer` repository. Rename it, or give
    me another repository to move it to.
26. **QuantFOPlatform.** Separate product to keep developing, or paused while the gateway is built?
27. **Operations and timeline.** Target launch date, and who handles incidents, reviews and refunds
    outside office hours (payments run 24/7)?

## Credentials

Once partners are chosen, their sandbox or test-mode API keys will be needed. Please do not paste keys
into chat or commit them: they can be added as environment secrets (I can ask for them through a secure
form), and the code only reads them from configuration.

## Next milestone once the starred questions are answered

Still no real money:

1. PostgreSQL-backed stores using the existing schema, with row locking for multiple API instances.
2. A reviewer queue and audit trail for held orders and rejected-payment decisions.
3. Cumulative KYC limits and the real blocked-country list.
4. The first real adapter in its provider's test mode (for example a payment provider's test
   environment and a custodian on a test network), behind the same interfaces.
5. Outbound, signed merchant webhooks from the outbox, and daily reconciliation against provider reports.
