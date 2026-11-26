using System.Security.Cryptography;
using System.Text.Encodings.Web;
using FiatCryptoGateway.Api.Checkout;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.Reconciliation;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Review;

public sealed class ReviewConsoleOptions
{
    public const string SectionName = "Gateway:Review";

    /// <summary>Serve the compliance console at <c>/review</c>. In production, consider serving it only on an internal network.</summary>
    public bool ConsoleEnabled { get; set; } = true;
}

/// <summary>
/// The compliance officer's console: one page where a reviewer signs in with a username, a password and an
/// authenticator code, then works through the reviewer API. The session token stays in the page's memory (no cookie,
/// so no cross-site request forgery, and nothing left behind when the page closes). The page itself holds no data;
/// everything comes from the API under the reviewer's session.
/// </summary>
public static class ReviewConsole
{
    private const string ResourceName = "FiatCryptoGateway.Api.Review.ReviewConsole.html";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(ReviewConsole).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The review console page {ResourceName} is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static IEndpointRouteBuilder MapReviewConsole(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/review", Show);
        return app;
    }

    private static IResult Show(
        HttpContext http,
        IOptions<ReviewConsoleOptions> options,
        IOptions<ReconciliationOptions> reconciliation,
        IOptions<GatewaySafetyOptions> safety)
    {
        if (!options.Value.ConsoleEnabled)
        {
            return Results.NotFound();
        }

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        // No form ever submits by itself: the page sends everything through the API, so a password typed into a form
        // can never end up in an address bar or a server log.
        CheckoutPage.ApplySecurityHeaders(http.Response, nonce, formAction: "'none'");
        var html = Template.Value
            .Replace("__NONCE__", nonce, StringComparison.Ordinal)
            .Replace("__TIME_ZONE__", HtmlEncoder.Default.Encode(reconciliation.Value.TimeZone), StringComparison.Ordinal)
            .Replace("__SANDBOX__", safety.Value.Mode == GatewayMode.Sandbox ? "true" : "false", StringComparison.Ordinal);
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
