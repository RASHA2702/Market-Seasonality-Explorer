# Partner integration design (PROPOSED)

How the gateway will work with a licensed partner, following the decisions in [decisions.md](decisions.md):
partner first (question 3), never holding crypto (questions 14 and 15), the partner's price lock (question 12),
and the partner screening wallets and handling the Travel Rule (questions 8 and 17). Nothing here is built yet
beyond the live-credential guard; the adapter waits for your partner choice (question 11).

## What changes from today's sandbox

The sandbox models the gateway buying crypto from an exchange and sending it from its own wallet. With a
partner, the partner does both, so two sandbox interfaces (`ILiquidityProvider`, `ICustodyProvider`) are
replaced by one:

```csharp
public interface IPaymentPartner
{
    string Name { get; }

    // A locked price: rate, the partner's fees and network fee, and when the lock expires.
    Task<PartnerQuote> QuoteAsync(PartnerQuoteRequest request, CancellationToken cancellationToken);

    // Creates the partner's order for one payment and returns where the customer pays:
    // a partner checkout URL, UPI or bank details, or a stablecoin deposit address.
    Task<PartnerOrder> CreateOrderAsync(PartnerOrderRequest request, CancellationToken cancellationToken);

    // Verifies the partner's webhook signature and turns its payload into gateway events:
    // payment received, held for review, crypto or INR delivered (with transaction hash), failed, refunded.
    PartnerWebhookResult ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now);

    // The partner's settlement report for one day. Built as ISettlementReportSource: the daily
    // reconciliation compares it with the books, matching movements by the partner's IDs.
    Task<SettlementReport> GetSettlementReportAsync(DateOnly day, CancellationToken cancellationToken);
}
```

Flows, in the build order from question 1:

| Flow | Customer pays | Merchant receives | Partner does |
|---|---|---|---|
| 1 | INR (UPI, card, netbanking, bank transfer) | Stablecoins in its own wallet | Collects INR, buys stablecoins, sends them to the merchant's wallet |
| 2 | Stablecoins | Stablecoins | Gives a deposit address, confirms receipt, forwards to the merchant's wallet |
| 3 | INR or stablecoins | INR in its bank account | Collects, converts if needed, pays out INR |

Other changes:

- **Merchant settlement settings**: each merchant registers where it is paid: a wallet per network (screened
  when registered) or, for flow 3, a bank account. Payments go there, never to the gateway.
- **Ledger**: the gateway's books mirror what the partner reports (amounts collected, delivered, fees), in
  partner-clearing accounts, with no company wallet accounts. The daily reconciliation (built) compares the two.
- **Order states**: payment received, then delivered by the partner (with the transaction hash), then
  confirmed. Whether a held-for-review step fits between payment and delivery depends on the partner (see
  the questions below).
- **Compliance**: the gateway's own checks (country, KYC level, limits) still run before the customer is sent
  to pay; the partner's KYC, wallet screening and Travel Rule apply on its side.

## Questions to ask each candidate

1. FIU-IND registration: the registered entity and its reference, to check against the official list.
2. Which INR methods (UPI, cards, netbanking, IMPS/NEFT/RTGS) and which coins and networks from question 13.
3. Settlement: stablecoins to a merchant's own wallet, and INR to a merchant's bank account?
4. **Can a paid order be held for your review before the crypto is sent?** If not, the gateway's review step
   (question 6) must happen before payment, and the partner's compliance decides after it.
5. Quote lock: how long, and what happens when the customer pays after it expires.
6. Customer KYC: done by the partner, and what the gateway receives back (status, level, references).
7. Wallet screening and Travel Rule on its transfers; data kept in India (question 9).
8. Webhooks: events, signature scheme, retries; and a daily settlement report: format, timing, the time zone of
   its day, and whether each line carries the same IDs as the webhooks (reconciliation matches on them).
9. Fees and minimum or maximum amounts per method.
10. A test environment: base URL, test API keys, test webhooks, test networks.

## Settings you will provide once a partner is chosen

Set these as environment variables or in a secret store, **never in the repository and never in chat**
(`<partner>` is the adapter's name, for example `onmeta`):

```bash
export Gateway__Providers__<partner>__Environment=Test
export Gateway__Providers__<partner>__BaseUrl=<the partner's test API URL>
export Gateway__Providers__<partner>__ApiKey=<test API key>
export Gateway__Providers__<partner>__WebhookSecret=<test webhook signing secret>
```

In a cloud session I can request the key and secret through the secure credential form instead.

## The live-credential guard (built)

`LiveCredentialGuard` runs at startup and refuses any provider setting that could reach real money: an
environment other than `Test`, a base URL that is not one of the adapter's declared HTTPS test hosts, a key
that looks live (for example `sk_live_`, or the adapter's own live prefix), or settings for a provider with no
installed adapter. Its messages never include a key's value. Each adapter declares its test hosts and live key
prefixes in a `ProviderEnvironmentProfile`.
