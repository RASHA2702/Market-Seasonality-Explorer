using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Orders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Checkout;

public sealed class CheckoutOptions
{
    public const string SectionName = "Gateway:Checkout";

    /// <summary>The public address checkout pages are served from, used to build the links merchants send customers to.</summary>
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:5080");

    /// <summary>
    /// Signs checkout links so they cannot be guessed or altered: at least 32 characters, from an environment variable
    /// or secret store. If empty, a random secret is made at startup, so links stop working after a restart and differ
    /// between instances; set one whenever more than one instance runs or orders are kept in PostgreSQL.
    /// </summary>
    public string LinkSecret { get; set; } = string.Empty;

    /// <summary>The time zone customers see the payment deadline in. India Standard Time by default.</summary>
    public string TimeZone { get; set; } = "Asia/Kolkata";

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

public sealed class CheckoutOptionsValidator : IValidateOptions<CheckoutOptions>
{
    public ValidateOptionsResult Validate(string? name, CheckoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.BaseUrl is not { IsAbsoluteUri: true } url ||
            !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            failures.Add("Checkout BaseUrl must be an absolute HTTPS address without credentials, query or fragment (HTTP only for localhost).");
        }

        if (options.LinkSecret.Length is > 0 and < 32)
        {
            failures.Add("Checkout LinkSecret must have at least 32 characters when set.");
        }

        try
        {
            _ = options.Zone;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            failures.Add($"Checkout TimeZone '{options.TimeZone}' is not a known time zone; use an IANA name such as Asia/Kolkata.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Builds and checks the links customers pay through. A link is the order ID plus an HMAC-SHA256 of it under a
/// secret, so it cannot be guessed or changed to point at another order, and nothing needs to be stored. The link
/// itself is the customer's only credential: there is no login and no cookie.
/// </summary>
public sealed partial class CheckoutLinks
{
    private const string Purpose = "checkout-link\n";
    private readonly byte[] _secret;
    private readonly Uri _baseUrl;

    public CheckoutLinks(IOptions<CheckoutOptions> options, ILogger<CheckoutLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.LinkSecret;
        if (string.IsNullOrEmpty(configured))
        {
            LogRandomSecret(logger);
            _secret = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            _secret = Encoding.UTF8.GetBytes(configured);
        }

        var baseUrl = options.Value.BaseUrl.ToString();
        _baseUrl = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    }

    public string Token(Guid orderId) => $"{orderId:N}.{Base64Url.EncodeToString(Mac(orderId))}";

    public Uri Url(Guid orderId) => new(_baseUrl, $"checkout/{Token(orderId)}");

    /// <summary>The order a link points to, if the link is one this gateway issued and was not altered.</summary>
    public bool TryRead(string? token, out Guid orderId)
    {
        orderId = Guid.Empty;
        var separator = token?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (token is null || separator != 32 || token.Length > 128 ||
            !Guid.TryParseExact(token.AsSpan(0, separator), "N", out var candidate) ||
            !Base64Url.IsValid(token.AsSpan(separator + 1)))
        {
            return false;
        }

        var presented = Base64Url.DecodeFromChars(token.AsSpan(separator + 1));
        if (!CryptographicOperations.FixedTimeEquals(presented, Mac(candidate)))
        {
            return false;
        }

        orderId = candidate;
        return true;
    }

    private byte[] Mac(Guid orderId) => HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(Purpose + orderId.ToString("N")));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Gateway:Checkout:LinkSecret is not set, so checkout links use a random secret and stop working after a restart.")]
    private static partial void LogRandomSecret(ILogger logger);
}

/// <summary>Finds the order behind a checkout link. The link is the credential, so no merchant key is involved.</summary>
public sealed class CheckoutService(CheckoutLinks links, IOrderStore orders)
{
    public async Task<PaymentOrder?> FindOrderAsync(string? token, CancellationToken cancellationToken) =>
        links.TryRead(token, out var orderId) ? await orders.FindAsync(orderId, cancellationToken) : null;
}
