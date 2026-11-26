using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Reviewers;

public enum ReviewerAccessEventKind
{
    SignedIn,
    SignInFailed,
    LockedOut,
    SignInBlocked,
    SignedOut,
}

/// <summary>One entry in the append-only log of reviewer sign-ins.</summary>
public sealed record ReviewerAccessEvent(string Username, ReviewerAccessEventKind Kind, DateTimeOffset OccurredAt, Guid? SessionId);

/// <summary>A reviewer's sign-in state: failures in a row, any pause, and the last authenticator step used.</summary>
public sealed record ReviewerGuard(int FailedAttempts, DateTimeOffset? LockedUntil, long LastTotpStep)
{
    public static readonly ReviewerGuard Fresh = new(0, null, 0);

    public bool IsLockedAt(DateTimeOffset now) => LockedUntil > now;
}

/// <summary>A signed-in reviewer. Only the SHA-256 of the token is kept.</summary>
public sealed record ReviewerSession(
    Guid SessionId,
    byte[] TokenSha256,
    string Username,
    byte[] CredentialFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt);

/// <summary>Sign-in state, sessions and the sign-in log. Every method is atomic, across instances too.</summary>
public interface IReviewerAccessStore
{
    Task<ReviewerGuard> GetGuardAsync(string username, CancellationToken cancellationToken);

    /// <summary>
    /// Counts a failed sign-in and logs it. When it is failure number <paramref name="maxFailedAttempts"/> in a row,
    /// sign-in pauses until <paramref name="lockedUntil"/>, the count starts again, and the pause is logged; returns
    /// whether this failure started a pause.
    /// </summary>
    Task<bool> RecordFailureAsync(string username, DateTimeOffset now, int maxFailedAttempts, DateTimeOffset lockedUntil, CancellationToken cancellationToken);

    /// <summary>Logs an attempt refused because sign-in was paused. It does not lengthen the pause.</summary>
    Task RecordBlockedAsync(string username, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Marks authenticator step <paramref name="totpStep"/> used, clears the failure count, opens the session and logs
    /// the sign-in, all at once. Returns false, changing nothing, if that step or a later one was already used, or if
    /// sign-in is paused at the session's creation time.
    /// </summary>
    Task<bool> OpenSessionAsync(ReviewerSession session, long totpStep, CancellationToken cancellationToken);

    Task<ReviewerSession?> FindSessionAsync(byte[] tokenSha256, CancellationToken cancellationToken);

    /// <summary>Records that an open session was used at <paramref name="now"/>.</summary>
    Task TouchSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Ends an open session at the reviewer's request and logs it; false if it had already ended.</summary>
    Task<bool> EndSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The reviewer's sign-in log, newest first.</summary>
    Task<IReadOnlyList<ReviewerAccessEvent>> ListEventsAsync(string username, int limit, CancellationToken cancellationToken);
}

public sealed record ReviewerIdentity(string Username, string DisplayName, Guid SessionId, DateTimeOffset ExpiresAt);

/// <summary>A successful sign-in: the session token, and what the reviewer should know about earlier sign-ins.</summary>
public sealed record ReviewerSignIn(string Token, ReviewerIdentity Reviewer, DateTimeOffset? PreviousSignInAt, int FailedAttemptsSincePrevious);

/// <summary>
/// Signs reviewers in with a password and a code from their authenticator app, and recognises their sessions.
/// <list type="bullet">
/// <item>Every failure looks the same to the caller (unknown username, wrong password or code, a reused code, a paused
/// sign-in), takes about as long, and is logged. After <see cref="ReviewerAccessOptions.MaxFailedAttempts"/> failures in
/// a row, sign-in pauses for <see cref="ReviewerAccessOptions.LockoutMinutes"/>.</item>
/// <item>A session token is 256 random bits; only its SHA-256 is stored. A session ends when the reviewer signs out,
/// after <see cref="ReviewerAccessOptions.SessionIdleMinutes"/> without use, after
/// <see cref="ReviewerAccessOptions.SessionMaxHours"/>, or when the reviewer's account is removed or its password or
/// authenticator key changes.</item>
/// </list>
/// </summary>
public sealed partial class ReviewerAccessService(
    IReviewerAccessStore store,
    IOptions<ReviewerAccessOptions> options,
    TimeProvider time,
    ILogger<ReviewerAccessService> logger)
{
    public const string TokenPrefix = "rvs_";
    private const int TokenBytes = 32;
    private const int HistoryScanned = 100;
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    // Checked against when the username is unknown, so that takes as long as a real check.
    private static readonly Lazy<string> DecoyHash = new(() => PasswordHashing.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    public static bool IsSessionToken(string? token) => token is not null && token.StartsWith(TokenPrefix, StringComparison.Ordinal);

    /// <summary>Null for any failure, deliberately without saying which part was wrong.</summary>
    public async Task<ReviewerSignIn?> SignInAsync(string? username, string? password, string? code, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var account = string.IsNullOrWhiteSpace(username) ? null : settings.Find(username.Trim().ToLowerInvariant());
        if (account is null)
        {
            PasswordHashing.Verify(password ?? string.Empty, DecoyHash.Value);

            // What was typed is not logged: it may be a password entered in the wrong field.
            LogUnknownUsername(logger);
            return null;
        }

        var guard = await store.GetGuardAsync(account.Username, cancellationToken);
        var passwordMatches = PasswordHashing.Verify(password ?? string.Empty, account.PasswordHash);
        var step = Totp.Match(Base32.TryDecode(account.TotpSecret), code, now, guard.LastTotpStep);
        if (guard.IsLockedAt(now))
        {
            await store.RecordBlockedAsync(account.Username, now, cancellationToken);
            LogBlocked(logger, account.Username);
            return null;
        }

        if (!passwordMatches || step is null)
        {
            await FailAsync(account.Username, now, cancellationToken);
            return null;
        }

        var history = await store.ListEventsAsync(account.Username, HistoryScanned, cancellationToken);
        var token = TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var session = new ReviewerSession(
            Guid.NewGuid(), HashToken(token), account.Username, Fingerprint(account), now, now, now.AddHours(settings.SessionMaxHours), EndedAt: null);
        if (!await store.OpenSessionAsync(session, step.Value, cancellationToken))
        {
            // The same code was used at the same moment elsewhere, or sign-in was paused meanwhile.
            await FailAsync(account.Username, now, cancellationToken);
            return null;
        }

        LogSignedIn(logger, account.Username, session.SessionId);
        var previous = history.FirstOrDefault(entry => entry.Kind == ReviewerAccessEventKind.SignedIn);
        var failedSince = history
            .TakeWhile(entry => entry.Kind != ReviewerAccessEventKind.SignedIn)
            .Count(entry => entry.Kind is ReviewerAccessEventKind.SignInFailed or ReviewerAccessEventKind.SignInBlocked);
        return new ReviewerSignIn(token, Identity(account, session), previous?.OccurredAt, failedSince);
    }

    /// <summary>The reviewer a session token belongs to, or null if the session has ended for any reason.</summary>
    public async Task<ReviewerIdentity?> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        if (!IsSessionToken(token) || token!.Length > 100)
        {
            return null;
        }

        var session = await store.FindSessionAsync(HashToken(token), cancellationToken);
        var now = time.GetUtcNow();
        var settings = options.Value;
        if (session is null || session.EndedAt is not null || now >= session.ExpiresAt ||
            now - session.LastSeenAt > TimeSpan.FromMinutes(settings.SessionIdleMinutes))
        {
            return null;
        }

        var account = settings.Find(session.Username);
        if (account is null || !CryptographicOperations.FixedTimeEquals(Fingerprint(account), session.CredentialFingerprint))
        {
            return null;
        }

        if (now - session.LastSeenAt >= TouchInterval)
        {
            await store.TouchSessionAsync(session.SessionId, now, cancellationToken);
        }

        return Identity(account, session);
    }

    public async Task SignOutAsync(ReviewerIdentity reviewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        if (await store.EndSessionAsync(reviewer.SessionId, time.GetUtcNow(), cancellationToken))
        {
            LogSignedOut(logger, reviewer.Username, reviewer.SessionId);
        }
    }

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Changes whenever the account's password or authenticator key changes, which ends its sessions.</summary>
    private static byte[] Fingerprint(ReviewerAccount account) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(account.PasswordHash + "\n" + account.TotpSecret));

    private static ReviewerIdentity Identity(ReviewerAccount account, ReviewerSession session) =>
        new(account.Username, ReviewerNames.DisplayNameOf(account), session.SessionId, session.ExpiresAt);

    private async Task FailAsync(string username, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (await store.RecordFailureAsync(username, now, settings.MaxFailedAttempts, now.AddMinutes(settings.LockoutMinutes), cancellationToken))
        {
            LogLockedOut(logger, username, settings.MaxFailedAttempts, settings.LockoutMinutes);
        }
        else
        {
            LogFailed(logger, username);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reviewer {Username} signed in (session {SessionId}).")]
    private static partial void LogSignedIn(ILogger logger, string username, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reviewer {Username} signed out (session {SessionId}).")]
    private static partial void LogSignedOut(ILogger logger, string username, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer sign-in failed for {Username}.")]
    private static partial void LogFailed(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer sign-in for {Username} paused for {Minutes} minutes after {Attempts} failures in a row.")]
    private static partial void LogLockedOut(ILogger logger, string username, int attempts, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer sign-in for {Username} refused: sign-in is paused after repeated failures.")]
    private static partial void LogBlocked(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer sign-in failed: no reviewer has that username.")]
    private static partial void LogUnknownUsername(ILogger logger);
}
