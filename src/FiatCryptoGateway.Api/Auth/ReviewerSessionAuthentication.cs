using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FiatCryptoGateway.Application.Reviewers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Auth;

/// <summary>
/// Authenticates <c>Authorization: Bearer rvs_…</c>: the session a reviewer opened by signing in with a password and an
/// authenticator code. The session is checked on every request, so signing out, idling or a changed account ends it
/// at once.
/// </summary>
public sealed class ReviewerSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ReviewerAccessService access)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ReviewerSession";
    public const string SessionClaim = "reviewer_session";
    public const string ExpiresClaim = "reviewer_session_expires";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = GatewayAuthentication.BearerToken(Request);
        if (!ReviewerAccessService.IsSessionToken(token))
        {
            return AuthenticateResult.NoResult();
        }

        var reviewer = await access.AuthenticateAsync(token, Context.RequestAborted);
        if (reviewer is null)
        {
            return AuthenticateResult.Fail("The reviewer session has ended.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ApiKeyAuthenticationHandler.ClientIdClaim, reviewer.Username),
                new Claim(ClaimTypes.Role, ApiPolicies.Reviewer),
                new Claim(ClaimTypes.Name, reviewer.DisplayName),
                new Claim(SessionClaim, reviewer.SessionId.ToString()),
                new Claim(ExpiresClaim, reviewer.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

/// <summary>Chooses how a request is authenticated: a reviewer session token, or else a merchant's API key.</summary>
public static class GatewayAuthentication
{
    public const string Scheme = "Gateway";

    public static string? BearerToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string prefix = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..].Trim() : null;
    }

    /// <summary>The signed-in reviewer behind a request authorised with the Reviewer policy.</summary>
    public static ReviewerIdentity GetReviewer(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new ReviewerIdentity(
            user.GetClientId(),
            user.FindFirstValue(ClaimTypes.Name) ?? user.GetClientId(),
            Guid.Parse(user.FindFirstValue(ReviewerSessionAuthenticationHandler.SessionClaim)
                ?? throw new InvalidOperationException("Only a reviewer session carries a session ID.")),
            DateTimeOffset.Parse(user.FindFirstValue(ReviewerSessionAuthenticationHandler.ExpiresClaim)!, CultureInfo.InvariantCulture));
    }
}
