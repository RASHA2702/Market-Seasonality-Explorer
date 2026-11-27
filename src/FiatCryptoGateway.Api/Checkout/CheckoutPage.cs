using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;

namespace FiatCryptoGateway.Api.Checkout;

/// <summary>Where the customer's latest payment attempt stands, in the terms the page needs.</summary>
public enum CheckoutAttemptStep
{
    /// <summary>Nothing is waiting: the customer chooses how to pay.</summary>
    None,

    /// <summary>The bank wants a one-time password.</summary>
    EnterCode,

    /// <summary>A request was sent to the customer's UPI app.</summary>
    ApproveInUpiApp,

    /// <summary>The bank has approved; the money is being taken.</summary>
    Finishing,

    /// <summary>The last attempt did not succeed: the customer chooses again, and is told why.</summary>
    Ended,
}

/// <summary>The customer's latest attempt. <c>Label</c> is the brand and last four digits, or the UPI address.</summary>
public sealed record CheckoutAttemptModel(
    CheckoutAttemptStep Step, Guid AttemptId, string Label, int TriesLeft, string? DeclineCode, string Status);

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
    bool Sandbox,
    IReadOnlyList<PaymentMethod> Methods,
    CheckoutAttemptModel? Attempt = null,
    string? Problem = null);

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
    public static void ApplySecurityHeaders(HttpResponse response, string? nonce = null, string formAction = "'self'")
    {
        ArgumentNullException.ThrowIfNull(response);
        var headers = response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'none'; " +
            (nonce is null ? string.Empty : $"style-src 'nonce-{nonce}'; script-src 'nonce-{nonce}'; ") +
            $"img-src 'self' data:; connect-src 'self'; form-action {formAction}; frame-ancestors 'none'; base-uri 'none'";
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
            CustomerPaymentStatus.AwaitingPayment => PaymentSection(model, token),
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
            <main data-status="{model.Status}" data-attempt="{Html.Encode(model.Attempt?.Status ?? string.Empty)}" data-status-url="{token}/status" data-seconds-left="{model.SecondsRemaining.ToString(CultureInfo.InvariantCulture)}">
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

    private static string PaymentSection(CheckoutPageModel model, string token)
    {
        var attempt = model.Attempt;
        var deadline = $"""<p class="deadline">Pay within <strong id="countdown">{Countdown(model.SecondsRemaining)}</strong>, by {Html.Encode(model.Deadline)}</p>""";
        var ways = WaysToPay(model, token);
        var body = attempt?.Step switch
        {
            CheckoutAttemptStep.EnterCode => CodeForm(model, token, attempt) + AnotherWay(ways),
            CheckoutAttemptStep.ApproveInUpiApp => UpiRequest(model, token, attempt) + AnotherWay(ways),
            CheckoutAttemptStep.Finishing => StatusBox("neutral", ClockIcon, "Finishing your payment", "Your bank has approved it. This takes a moment."),
            _ => ways,
        };
        return deadline + Notice(model) + body + SandboxHelp(model, token);
    }

    /// <summary>Why the last try did not work, in the words of <see cref="CustomerMessages"/>. Nothing typed is ever shown back.</summary>
    private static string Notice(CheckoutPageModel model)
    {
        var text = model.Problem is { } problem
            ? CustomerMessages.ForRefusal(problem)
            : model.Attempt is { Step: CheckoutAttemptStep.Ended } ended ? CustomerMessages.ForDecline(ended.DeclineCode) : null;
        return text is null ? string.Empty : $"""<p class="notice" role="alert">{Html.Encode(text)}</p>""";
    }

    private static string WaysToPay(CheckoutPageModel model, string token)
    {
        var upi = model.Methods.Contains(PaymentMethod.Upi) ? UpiForm(model, token) : string.Empty;
        var card = model.Methods.Contains(PaymentMethod.Card) ? CardForm(model, token) : string.Empty;
        return upi + card;
    }

    private static string AnotherWay(string ways) =>
        $"""<details class="other"><summary>Pay another way</summary>{ways}</details>""";

    // No field ever carries a value: what was typed is never put back into a page.
    private static string UpiForm(CheckoutPageModel model, string token) => $"""
        <section class="option">
          <h2>Pay with UPI</h2>
          <form method="post" action="{token}/pay">
            <input type="hidden" name="method" value="upi">
            <label class="field" for="upi-address">UPI address</label>
            <input id="upi-address" name="upi_address" type="text" inputmode="email" autocomplete="off" autocapitalize="off" spellcheck="false" placeholder="name@bank" maxlength="80" required>
            <button type="submit">Pay {Html.Encode(model.Amount)}</button>
          </form>
        </section>
        """;

    private static string CardForm(CheckoutPageModel model, string token) => $"""
        <section class="option">
          <h2>Pay with a card</h2>
          <form method="post" action="{token}/pay">
            <input type="hidden" name="method" value="card">
            <label class="field" for="card-number">Card number</label>
            <input id="card-number" name="card_number" type="text" inputmode="numeric" autocomplete="cc-number" autocapitalize="off" spellcheck="false" placeholder="1234 1234 1234 1234" maxlength="23" required>
            <div class="row">
              <div>
                <label class="field" for="card-expiry">Expiry</label>
                <input id="card-expiry" name="card_expiry" type="text" inputmode="numeric" autocomplete="cc-exp" placeholder="MM/YY" maxlength="7" required>
              </div>
              <div>
                <label class="field" for="card-code">Security code</label>
                <input id="card-code" name="card_code" type="password" inputmode="numeric" autocomplete="cc-csc" placeholder="123" maxlength="4" required>
              </div>
            </div>
            <label class="field" for="card-name">Name on card (optional)</label>
            <input id="card-name" name="card_name" type="text" autocomplete="cc-name" maxlength="60">
            <button type="submit">Pay {Html.Encode(model.Amount)}</button>
          </form>
        </section>
        """;

    private static string CodeForm(CheckoutPageModel model, string token, CheckoutAttemptModel attempt)
    {
        var tries = attempt.TriesLeft.ToString(CultureInfo.InvariantCulture);
        var text = $"Your bank sent a one-time password to confirm {attempt.Label}. You have {tries} {(attempt.TriesLeft == 1 ? "try" : "tries")} left.";
        var hint = model.Sandbox ? $"""<p class="note">In the sandbox the code is <code>{SandboxTestInstruments.Otp}</code>.</p>""" : string.Empty;
        return $"""
            {StatusBox("neutral", ClockIcon, "Enter the code from your bank", Html.Encode(text))}
            <form method="post" action="{token}/authenticate">
              <input type="hidden" name="attempt" value="{attempt.AttemptId}">
              <label class="field" for="otp">One-time password</label>
              <input id="otp" name="code" type="text" inputmode="numeric" autocomplete="one-time-code" autocapitalize="off" spellcheck="false" maxlength="10" required>
              <button type="submit">Confirm payment</button>
            </form>
            {hint}
            """;
    }

    private static string UpiRequest(CheckoutPageModel model, string token, CheckoutAttemptModel attempt)
    {
        var text = $"We sent a request for {model.Amount} to {attempt.Label}. Open your UPI app and approve it. This page updates by itself.";
        var standIn = model.Sandbox
            ? $"""
              <form method="post" action="{token}/authenticate" class="buttons">
                <input type="hidden" name="attempt" value="{attempt.AttemptId}">
                <button type="submit" name="approve" value="yes">Approve (sandbox)</button>
                <button type="submit" name="approve" value="no" class="secondary">Decline (sandbox)</button>
              </form>
              <p class="note">There is no phone in the sandbox, so these buttons stand in for your UPI app.</p>
              """
            : string.Empty;
        return $"""
            {StatusBox("neutral", ClockIcon, "Approve the request in your UPI app", Html.Encode(text))}
            {standIn}
            """;
    }

    /// <summary>The sandbox's published test instruments, and a shortcut for those who just want to see a payment succeed.</summary>
    private static string SandboxHelp(CheckoutPageModel model, string token)
    {
        if (!model.Sandbox)
        {
            return string.Empty;
        }

        var cards = string.Concat(SandboxTestInstruments.Cards.Select(card =>
            $"<tr><td><code>{Html.Encode(card.Display)}</code><small>{Html.Encode(card.Brand)}</small></td><td>{Html.Encode(card.Meaning)}</td></tr>"));
        var addresses = string.Concat(SandboxTestInstruments.UpiAddresses.Select(address =>
            $"<tr><td><code>{Html.Encode(address.Address)}</code></td><td>{Html.Encode(address.Meaning)}</td></tr>"));
        return $"""
            <details class="sandbox-help">
              <summary>Sandbox: test cards and UPI addresses</summary>
              <p class="note">Use any expiry date in the future, any 3-digit security code (4 digits for American Express) and the one-time password <code>{SandboxTestInstruments.Otp}</code>. Real cards are refused, and nothing you type is kept.</p>
              <div class="scroll"><table><thead><tr><th>Card or address</th><th>What happens</th></tr></thead><tbody>{cards}{addresses}</tbody></table></div>
              <form method="post" action="{token}/sandbox-pay"><button type="submit" class="secondary">Skip the form: pay with the test UPI address</button></form>
            </details>
            """;
    }

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
        .option { margin: 0 0 16px; padding: 16px; border: 1px solid var(--line); border-radius: 12px; }
        .option h2 { margin: 0 0 12px; font-size: 16px; }
        label.field { display: block; margin: 0 0 4px; font-size: 14px; font-weight: 600; }
        input[type=text], input[type=password] { display: block; width: 100%; padding: 12px 14px; margin: 0 0 12px; border: 1px solid var(--line); border-radius: 10px; background: var(--card); color: var(--text); font: inherit; }
        .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
        .notice { margin: 0 0 16px; padding: 12px 14px; border-radius: 10px; background: color-mix(in srgb, var(--warn) 14%, transparent); color: var(--warn); font-size: 14px; font-weight: 600; }
        details.other { margin: 16px 0 0; }
        details > summary { cursor: pointer; font-weight: 600; }
        details.other > summary { margin-bottom: 12px; }
        details.sandbox-help { margin: 16px 0 0; font-size: 14px; }
        .scroll { overflow-x: auto; margin: 0 0 12px; }
        table { border-collapse: collapse; width: 100%; font-size: 13px; }
        th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
        td small { display: block; color: var(--muted); }
        code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 13px; white-space: nowrap; }
        .buttons { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; margin: 12px 0 0; }
        button.secondary { background: transparent; color: var(--text); border: 1px solid var(--line); }
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
          const attempt = main.dataset.attempt || '';
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
                if (response.ok) {
                  const now = await response.json();
                  if (now.status !== status || (now.attempt || '') !== attempt) { location.reload(); }
                }
              } catch { /* try again on the next tick */ }
            }, 5000);
          }
          // The button is turned off after the browser has read the form, so a button's own name and value are still sent.
          document.querySelectorAll('form').forEach((form) => {
            form.addEventListener('submit', () => {
              const button = form.querySelector('button[type=submit]');
              if (button) { setTimeout(() => { button.disabled = true; button.textContent = 'Processing…'; }, 0); }
            });
          });
        })();
        </script>
        </body>
        </html>
        """;
}
