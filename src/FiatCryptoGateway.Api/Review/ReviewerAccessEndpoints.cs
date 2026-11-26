using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using FiatCryptoGateway.Api.Auth;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Reviewers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Review;

/// <summary>Reviewers sign in with a username, a password and a code from their authenticator app, and sign out.</summary>
public static class ReviewerAccessEndpoints
{
    public const string SignInLimit = "reviewer-sign-in";

    /// <summary>
    /// Limits sign-in requests per network address, before any password is checked, so guessing across usernames is
    /// slow and password hashing cannot be used to load the server. Behind a proxy every request shares the proxy's
    /// address; limit at the proxy too.
    /// </summary>
    public static IServiceCollection AddReviewerSignInLimit(this IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(SignInLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IOptions<ReviewerAccessOptions>>().Value.SignInAttemptsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ErrorResponse(new ErrorDetail("TOO_MANY_ATTEMPTS", "Too many sign-in attempts from this address. Wait a minute and try again.")),
                    cancellationToken);
            };
        });

    public static IEndpointRouteBuilder MapReviewerAccess(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/v1/review/sign-in", SignIn).AllowAnonymous().RequireRateLimiting(SignInLimit);
        app.MapPost("/v1/review/sign-out", SignOut).RequireAuthorization(ApiPolicies.Reviewer);
        return app;
    }

    private static async Task<IResult> SignIn(
        ReviewerSignInRequest? request,
        HttpContext http,
        ReviewerAccessService access,
        IOptions<ReviewerAccessOptions> options,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        var settings = options.Value;
        var signIn = await access.SignInAsync(request?.Username, request?.Password, request?.Code, cancellationToken);
        if (signIn is null)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    "SIGN_IN_FAILED",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Sign-in failed. Check your username, your password and the current code from your authenticator app. " +
                        $"After {settings.MaxFailedAttempts} failed attempts in a row, sign-in pauses for {settings.LockoutMinutes} minutes."))),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new ReviewerSignInResponse(
            signIn.Token,
            signIn.Reviewer.ExpiresAt,
            settings.SessionIdleMinutes,
            new ReviewerResponse(signIn.Reviewer.Username, signIn.Reviewer.DisplayName),
            signIn.PreviousSignInAt,
            signIn.FailedAttemptsSincePrevious));
    }

    private static async Task<IResult> SignOut(ClaimsPrincipal user, ReviewerAccessService access, CancellationToken cancellationToken)
    {
        await access.SignOutAsync(user.GetReviewer(), cancellationToken);
        return Results.NoContent();
    }
}
