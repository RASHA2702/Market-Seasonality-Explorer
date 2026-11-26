using FiatCryptoGateway.Application.Reviewers;

namespace FiatCryptoGateway.Sandbox;

/// <summary>Reviewer sign-in state in memory: lost on restart, one instance only. Each method holds one lock.</summary>
public sealed class InMemoryReviewerAccessStore : IReviewerAccessStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ReviewerGuard> _guards = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ReviewerSession> _sessions = [];
    private readonly Dictionary<string, Guid> _sessionsByToken = new(StringComparer.Ordinal);
    private readonly List<ReviewerAccessEvent> _events = [];

    public Task<ReviewerGuard> GetGuardAsync(string username, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_guards.GetValueOrDefault(username, ReviewerGuard.Fresh));
        }
    }

    public Task<bool> RecordFailureAsync(string username, DateTimeOffset now, int maxFailedAttempts, DateTimeOffset lockedUntil, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var guard = _guards.GetValueOrDefault(username, ReviewerGuard.Fresh);
            var failures = guard.FailedAttempts + 1;
            var locks = failures >= maxFailedAttempts;
            _guards[username] = locks ? guard with { FailedAttempts = 0, LockedUntil = lockedUntil } : guard with { FailedAttempts = failures };
            _events.Add(new ReviewerAccessEvent(username, ReviewerAccessEventKind.SignInFailed, now, null));
            if (locks)
            {
                _events.Add(new ReviewerAccessEvent(username, ReviewerAccessEventKind.LockedOut, now, null));
            }

            return Task.FromResult(locks);
        }
    }

    public Task RecordBlockedAsync(string username, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _events.Add(new ReviewerAccessEvent(username, ReviewerAccessEventKind.SignInBlocked, now, null));
            return Task.CompletedTask;
        }
    }

    public Task<bool> OpenSessionAsync(ReviewerSession session, long totpStep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            var guard = _guards.GetValueOrDefault(session.Username, ReviewerGuard.Fresh);
            if (guard.LastTotpStep >= totpStep || guard.IsLockedAt(session.CreatedAt))
            {
                return Task.FromResult(false);
            }

            _guards[session.Username] = new ReviewerGuard(0, null, totpStep);
            _sessions.Add(session.SessionId, session);
            _sessionsByToken.Add(Convert.ToHexString(session.TokenSha256), session.SessionId);
            _events.Add(new ReviewerAccessEvent(session.Username, ReviewerAccessEventKind.SignedIn, session.CreatedAt, session.SessionId));
            return Task.FromResult(true);
        }
    }

    public Task<ReviewerSession?> FindSessionAsync(byte[] tokenSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenSha256);
        lock (_gate)
        {
            return Task.FromResult(
                _sessionsByToken.TryGetValue(Convert.ToHexString(tokenSha256), out var sessionId) ? _sessions[sessionId] : null);
        }
    }

    public Task TouchSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var session) && session.EndedAt is null && session.LastSeenAt < now)
            {
                _sessions[sessionId] = session with { LastSeenAt = now };
            }

            return Task.CompletedTask;
        }
    }

    public Task<bool> EndSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.EndedAt is not null)
            {
                return Task.FromResult(false);
            }

            _sessions[sessionId] = session with { EndedAt = now };
            _events.Add(new ReviewerAccessEvent(session.Username, ReviewerAccessEventKind.SignedOut, now, sessionId));
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ReviewerAccessEvent>> ListEventsAsync(string username, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<ReviewerAccessEvent> events = [.. Enumerable.Reverse(_events).Where(entry => entry.Username == username).Take(limit)];
            return Task.FromResult(events);
        }
    }
}
