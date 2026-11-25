using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using FiatCryptoGateway.Application.OnRamp;

namespace FiatCryptoGateway.Api.Checkout;

/// <summary>What the checkout page shows. Every text here is HTML-encoded when rendered.</summary>
public sealed record CheckoutPageModel(
    string MerchantName,
    string Reference,
    string Amount,
    CustomerPaymentStatus Status,
    string Deadline,
    long SecondsRemaining,
    string Token,
    Uri? ReturnUrl,
    bool Sandbox);

/// <summary>
/// Renders the hosted checkout page as one self-contained document: no third-party scripts, fonts or images, so the
/// payment link never leaks to another site and a strict content security policy holds. The page works without
/// JavaScript; the small script only counts down the deadline and refreshes when the payment status changes.
/// </summary>
public static class CheckoutPage
{
    // Escapes markup characters (< > & " ') but leaves other text as it is, so ₹ and names in Indian scripts stay readable.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    public static IResult Render(HttpContext http, CheckoutPageModel model)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(model);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        ApplySecurityHeaders(http.Response, nonce);
        return Results.Content(Document($"Pay {model.MerchantName}", Body(model), nonce), "text/html; charset=utf-8");
    }

    /// <summary>The same page for an unknown and an altered link, so neither can be told apart.</summary>
    public static IResult NotFound(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        ApplySecurityHeaders(http.Response, nonce);
        var body = $"""
            <main data-status="NotFound">
              {StatusBox("neutral", ClockIcon, "Payment link not found", "Check the link, or ask the merchant for a new one.")}
            </main>
            """;
        return Results.Content(Document("Payment link not found", body, nonce), "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>Headers for every checkout response: nothing cached, framed, sniffed, indexed or sent on as a referrer.</summary>
    public static void ApplySecurityHeaders(HttpResponse response, string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var headers = response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'none'; " +
            (nonce is null ? string.Empty : $"style-src 'nonce-{nonce}'; script-src 'nonce-{nonce}'; ") +
            "img-src 'self' data:; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
        headers["X-Robots-Tag"] = "noindex, nofollow";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    }

    private static string Body(CheckoutPageModel model)
    {
        var merchant = Html.Encode(model.MerchantName);
        var token = Html.Encode(model.Token);
        var sandboxBanner = model.Sandbox ? """<p class="sandbox" role="note">Sandbox: no real money moves.</p>""" : string.Empty;
        var content = model.Status switch
        {
            CustomerPaymentStatus.AwaitingPayment => PaymentForm(model, token),
            CustomerPaymentStatus.PaymentReceived => StatusBox("ok", CheckIcon, "Payment received", $"Thank you. {merchant} has been told about your payment."),
            CustomerPaymentStatus.Expired => StatusBox(
                "neutral", ClockIcon, "This payment link has expired", $"If you already paid, your money will be returned. Ask {merchant} for a new payment link."),
            CustomerPaymentStatus.RefundStarted => StatusBox("neutral", ClockIcon, "Refund started", "Your payment is being returned to the account you paid from."),
            CustomerPaymentStatus.Refunded => StatusBox("ok", CheckIcon, "Payment refunded", "Your payment has been returned to the account you paid from."),
            _ => throw new ArgumentOutOfRangeException(nameof(model), model.Status, "Unknown customer status."),
        };
        var returnLink = model.ReturnUrl is { } url
            ? $"""<a class="return" href="{Html.Encode(url.ToString())}">{(model.Status == CustomerPaymentStatus.AwaitingPayment ? "Cancel and return to " : "Back to ")}{merchant}</a>"""
            : string.Empty;
        var live = model.Status is CustomerPaymentStatus.AwaitingPayment or CustomerPaymentStatus.RefundStarted
            ? """<p class="note" aria-live="polite">This page updates by itself.</p>"""
            : string.Empty;
        return $"""
            <main data-status="{model.Status}" data-status-url="{token}/status" data-seconds-left="{model.SecondsRemaining.ToString(CultureInfo.InvariantCulture)}">
              {sandboxBanner}
              <p class="eyebrow">Payment to</p>
              <h1>{merchant}</h1>
              <p class="amount">{Html.Encode(model.Amount)}</p>
              <p class="meta">Reference {Html.Encode(model.Reference)}</p>
              {content}
              {returnLink}
              {live}
            </main>
            """;
    }

    private static string PaymentForm(CheckoutPageModel model, string token) => $"""
        <p class="deadline">Pay within <strong id="countdown">{Countdown(model.SecondsRemaining)}</strong>, by {Html.Encode(model.Deadline)}</p>
        <form method="post" action="{token}/pay">
          <fieldset>
            <legend>How would you like to pay?</legend>
            {Method("upi", "UPI", "Any UPI app", true)}
            {Method("card", "Card", "Debit or credit card", false)}
            {Method("netbanking", "Netbanking", "Your bank's website", false)}
            {Method("bank_transfer", "Bank transfer", "IMPS, NEFT or RTGS", false)}
          </fieldset>
          <button type="submit" id="pay">Pay {Html.Encode(model.Amount)}</button>
        </form>
        {(model.Sandbox ? """<p class="note">In the sandbox this button simulates the payment. Once a payment partner is connected, you will pay on its page.</p>""" : string.Empty)}
        """;

    private static string Method(string value, string title, string detail, bool selected) => $"""
        <label class="method"><input type="radio" name="method" value="{value}"{(selected ? " checked" : string.Empty)}><span>{title}<small>{detail}</small></span></label>
        """;

    private static string StatusBox(string tone, string icon, string title, string text) => $"""
        <section class="status {tone}" role="status"><span aria-hidden="true">{icon}</span><div><h2>{title}</h2><p>{text}</p></div></section>
        """;

    private static string Countdown(long seconds) =>
        $"{(seconds / 60).ToString(CultureInfo.InvariantCulture)}:{(seconds % 60).ToString("00", CultureInfo.InvariantCulture)}";

    private const string CheckIcon =
        """<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="m8 12 3 3 5-6"/></svg>""";

    private const string ClockIcon =
        """<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 7v5l3 2"/></svg>""";

    private static string Document(string title, string body, string nonce) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex, nofollow">
        <title>{{Html.Encode(title)}}</title>
        <style nonce="{{nonce}}">
        :root { color-scheme: light dark; --bg: #f3f4f6; --card: #ffffff; --text: #14161b; --muted: #596070; --line: #dfe2e8; --accent: #1d4ed8; --on-accent: #ffffff; --ok: #0f7a45; --warn: #8a4b00; }
        @media (prefers-color-scheme: dark) { :root { --bg: #0e1014; --card: #171a20; --text: #eef0f4; --muted: #a3a9b5; --line: #2b303a; --accent: #6d94ff; --on-accent: #0a0c10; --ok: #45c98b; --warn: #f2b25c; } }
        * { box-sizing: border-box; }
        body { margin: 0; min-height: 100vh; display: flex; justify-content: center; align-items: flex-start; padding: 24px 16px; background: var(--bg); color: var(--text); font: 16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, "Noto Sans", sans-serif; }
        main { width: 100%; max-width: 420px; background: var(--card); border: 1px solid var(--line); border-radius: 16px; padding: 24px; }
        .sandbox { margin: 0 0 20px; padding: 8px 12px; border-radius: 8px; background: color-mix(in srgb, var(--warn) 14%, transparent); color: var(--warn); font-size: 13px; font-weight: 600; }
        .eyebrow { margin: 0; color: var(--muted); font-size: 14px; }
        h1 { margin: 2px 0 12px; font-size: 22px; line-height: 1.3; overflow-wrap: anywhere; }
        .amount { margin: 0; font-size: 36px; font-weight: 700; letter-spacing: -0.02em; font-variant-numeric: tabular-nums; }
        .meta { margin: 2px 0 20px; color: var(--muted); font-size: 14px; }
        .deadline { margin: 0 0 16px; font-size: 15px; }
        .deadline strong { font-variant-numeric: tabular-nums; }
        fieldset { border: 0; margin: 0 0 16px; padding: 0; display: grid; gap: 8px; }
        legend { font-weight: 600; margin-bottom: 8px; padding: 0; }
        label.method { display: flex; align-items: center; gap: 12px; padding: 12px 14px; border: 1px solid var(--line); border-radius: 12px; cursor: pointer; }
        label.method:has(input:checked) { border-color: var(--accent); box-shadow: inset 0 0 0 1px var(--accent); }
        label.method small { display: block; color: var(--muted); font-size: 13px; }
        input[type=radio] { accent-color: var(--accent); width: 18px; height: 18px; margin: 0; flex: none; }
        button { width: 100%; padding: 14px 16px; border: 0; border-radius: 12px; background: var(--accent); color: var(--on-accent); font: inherit; font-weight: 700; cursor: pointer; }
        button:disabled { opacity: 0.6; cursor: progress; }
        :focus-visible { outline: 3px solid var(--accent); outline-offset: 2px; }
        .note { margin: 12px 0 0; color: var(--muted); font-size: 13px; }
        .status { display: flex; gap: 12px; align-items: flex-start; padding: 16px; border-radius: 12px; }
        .status.ok { background: color-mix(in srgb, var(--ok) 12%, transparent); color: var(--ok); }
        .status.neutral { background: color-mix(in srgb, var(--muted) 14%, transparent); color: var(--muted); }
        .status h2 { margin: 0; font-size: 17px; color: var(--text); }
        .status p { margin: 2px 0 0; color: var(--muted); font-size: 14px; }
        a.return { display: block; margin-top: 16px; padding: 12px; border: 1px solid var(--line); border-radius: 12px; color: var(--text); text-align: center; text-decoration: none; font-weight: 600; }
        </style>
        </head>
        <body>
        {{body}}
        <script nonce="{{nonce}}">
        (() => {
          const main = document.querySelector('main');
          const status = main.dataset.status;
          let left = Number(main.dataset.secondsLeft || 0);
          const countdown = document.getElementById('countdown');
          if (countdown) {
            const show = () => { countdown.textContent = Math.floor(left / 60) + ':' + String(left % 60).padStart(2, '0'); };
            const timer = setInterval(() => { left = Math.max(0, left - 1); show(); if (left === 0) { clearInterval(timer); location.reload(); } }, 1000);
          }
          if (status === 'AwaitingPayment' || status === 'RefundStarted') {
            setInterval(async () => {
              try {
                const response = await fetch(main.dataset.statusUrl, { cache: 'no-store', headers: { Accept: 'application/json' } });
                if (response.ok && (await response.json()).status !== status) { location.reload(); }
              } catch { /* try again on the next tick */ }
            }, 5000);
          }
          const form = document.querySelector('form');
          if (form) {
            form.addEventListener('submit', () => { const button = form.querySelector('button'); button.disabled = true; button.textContent = 'Processing…'; });
          }
        })();
        </script>
        </body>
        </html>
        """;
}
