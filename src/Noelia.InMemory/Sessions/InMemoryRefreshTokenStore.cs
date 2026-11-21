using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;

namespace Noelia.InMemory.Sessions;

/// <summary>
/// Keeps refresh tokens in this process.
/// </summary>
/// <remarks>
/// For tests and for a first run: nothing survives a restart, so everyone is
/// signed out by a deployment, and a second instance knows nothing of the
/// first. Put them in the database the application already runs before that
/// matters.
/// <para>
/// One lock around the whole transition. That is what the contract asks for and
/// it is trivially correct here — which is also why it proves nothing about a
/// database, and why the conformance suite is written to run against one.
/// </para>
/// </remarks>
public sealed class InMemoryRefreshTokenStore : IRefreshTokenStore
{
    // How far a rotation chain is followed; the database store uses the same.
    private const int MaxChainLinks = 32;

    private readonly Lock _gate = new();
    private readonly Dictionary<RefreshTokenId, RefreshTokenRecord> _tokens = [];
    private readonly Dictionary<string, RefreshTokenId> _byHash = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task CreateAsync(RefreshTokenRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Refused rather than overwritten, matching the unique index a
            // relational store puts on the hash. Silently replacing would hand
            // one token's identity to another.
            if (!_byHash.TryAdd(Key(record.TokenHash), record.Id))
            {
                throw new InvalidOperationException("A token with this hash already exists.");
            }

            _tokens[record.Id] = record;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ConsumeResult> TryConsumeAsync(
        byte[] tokenHash,
        RefreshTokenRecord successor,
        DateTimeOffset now,
        TimeSpan grace,
        int maxConcurrentPerSession,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_byHash.TryGetValue(Key(tokenHash), out var id))
            {
                return Task.FromResult(new ConsumeResult(ConsumeOutcome.NotFound, null));
            }

            var current = _tokens[id];

            // Rotated already. Whether that is a race or a theft is decided by
            // how long ago — and inside the window the two are genuinely
            // indistinguishable, which is why the window is seconds.
            if (current.ReplacedBy is not null)
            {
                if (now > current.RevokedAt!.Value + grace)
                {
                    CloseSession(current.Session, now);
                    return Task.FromResult(new ConsumeResult(ConsumeOutcome.ReuseDetected, null));
                }

                // A sibling exists only while the newest token of the chain the
                // presented one belongs to is open. If that was closed — by a
                // sign-out, by the cap, by a replay — the session is no longer
                // one to give a token to, and this is not a theft either.
                if (!ChainEndIsOpen(current))
                {
                    return Task.FromResult(new ConsumeResult(ConsumeOutcome.Revoked, null));
                }

                // A sibling past the ceiling would be born dead.
                if (now >= current.SessionExpiresAt)
                {
                    return Task.FromResult(new ConsumeResult(ConsumeOutcome.SessionExpired, null));
                }

                var sibling = Rotate(current, successor, now, isGrace: true);
                Cap(current.Session, maxConcurrentPerSession, now);
                return Task.FromResult(
                    new ConsumeResult(ConsumeOutcome.RotatedWithinGrace, sibling));
            }

            if (current.RevokedAt is not null)
            {
                return Task.FromResult(new ConsumeResult(ConsumeOutcome.Revoked, null));
            }

            // The ceiling is checked first: once the sign-in is over, both are
            // true — successors are clamped to it — and "this token is stale"
            // describes the smaller fact.
            if (now >= current.SessionExpiresAt)
            {
                return Task.FromResult(new ConsumeResult(ConsumeOutcome.SessionExpired, null));
            }

            if (now >= current.ExpiresAt)
            {
                return Task.FromResult(new ConsumeResult(ConsumeOutcome.Expired, null));
            }

            var issued = Rotate(current, successor, now, isGrace: false);
            Cap(current.Session, maxConcurrentPerSession, now);

            return Task.FromResult(new ConsumeResult(ConsumeOutcome.Rotated, issued));
        }
    }

    /// <inheritdoc />
    public Task<int> CloseSessionAsync(
        SessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(CloseSession(session, at));
        }
    }

    /// <inheritdoc />
    public Task<int> CloseAllSessionsAsync(
        SubjectId subject,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var sessions = _tokens.Values
                .Where(t => t.Subject == subject && IsOpen(t))
                .Select(t => t.Session)
                .Distinct()
                .ToArray();

            return Task.FromResult(sessions.Sum(session => CloseSession(session, at)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionSummary>> ActiveSessionsAsync(
        SubjectId subject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var summaries = _tokens.Values
                .Where(t => t.Subject == subject && IsOpen(t) && now < t.ExpiresAt && now < t.SessionExpiresAt)
                .GroupBy(t => t.Session)
                .Select(group => new SessionSummary(
                    group.Key,
                    group.Min(t => t.SessionStartedAt),
                    group.Max(t => t.IssuedAt),
                    group.Min(t => t.SessionExpiresAt),
                    group.OrderByDescending(t => t.IssuedAt).First().ClientFingerprint))
                .OrderByDescending(summary => summary.LastUsedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<SessionSummary>>(summaries);
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        lock (_gate)
        {
            var finished = _tokens.Values
                .Where(t => IsFinished(t, olderThan))
                .OrderBy(t => t.IssuedAt)
                .Take(batchSize)
                .ToArray();

            foreach (var token in finished)
            {
                _tokens.Remove(token.Id);
                _byHash.Remove(Key(token.TokenHash));
            }

            return Task.FromResult(finished.Length);
        }
    }

    /// <summary>How many tokens of a session are still usable. For tests.</summary>
    public int OpenTokenCount(SessionId session)
    {
        lock (_gate)
        {
            return _tokens.Values.Count(t => t.Session == session && IsOpen(t));
        }
    }

    /// <summary>
    /// Writes the successor and marks the predecessor as replaced.
    /// </summary>
    /// <remarks>
    /// The session's own attributes are copied rather than taken from the
    /// successor the caller built, so a caller cannot extend a sign-in past its
    /// ceiling by asking for a longer token.
    /// </remarks>
    private RefreshTokenRecord Rotate(
        RefreshTokenRecord current,
        RefreshTokenRecord successor,
        DateTimeOffset now,
        bool isGrace)
    {
        var issued = successor with
        {
            Session = current.Session,
            Subject = current.Subject,
            SessionStartedAt = current.SessionStartedAt,
            SessionExpiresAt = current.SessionExpiresAt,
            ExpiresAt = successor.ExpiresAt > current.SessionExpiresAt
                ? current.SessionExpiresAt
                : successor.ExpiresAt,
            IssuedAt = now,
            RevokedAt = null,
            ReplacedBy = null
        };

        if (!_byHash.TryAdd(Key(issued.TokenHash), issued.Id))
        {
            throw new InvalidOperationException("A token with this hash already exists.");
        }

        _tokens[issued.Id] = issued;

        // Inside the grace window the predecessor was already replaced; leaving
        // its first successor in place keeps the chain readable.
        if (!isGrace)
        {
            _tokens[current.Id] = current with { RevokedAt = now, ReplacedBy = issued.Id };
        }

        return issued;
    }

    /// <summary>
    /// Whether the end of the chain that <paramref name="rotated"/> belongs to is
    /// still open.
    /// </summary>
    /// <remarks>
    /// The chain follows the replacements from the presented token to the first
    /// row that has none; siblings are not part of it. A hole in it (a purge) or
    /// a chain longer than the store follows cannot be shown to end in an open
    /// token, so it counts as closed. The database store applies the same rule,
    /// there under a lock; here the lock around the whole transition does that
    /// work.
    /// </remarks>
    private bool ChainEndIsOpen(RefreshTokenRecord rotated)
    {
        var end = rotated;

        for (var hops = 1; end.ReplacedBy is { } next; hops++)
        {
            if (hops > MaxChainLinks || !_tokens.TryGetValue(next, out end!))
            {
                return false;
            }
        }

        return end.RevokedAt is null;
    }

    /// <summary>Revokes every still-open token of a session.</summary>
    private int CloseSession(SessionId session, DateTimeOffset at)
    {
        var open = _tokens.Values.Where(t => t.Session == session && IsOpen(t)).ToArray();

        foreach (var token in open)
        {
            _tokens[token.Id] = token with { RevokedAt = at };
        }

        return open.Length;
    }

    /// <summary>
    /// Keeps a session's open tokens within its allowance, oldest first.
    /// </summary>
    private void Cap(SessionId session, int allowance, DateTimeOffset at)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(allowance, 1);

        var open = _tokens.Values
            .Where(t => t.Session == session && IsOpen(t))
            .OrderBy(t => t.IssuedAt)
            .ToArray();

        foreach (var token in open.Take(Math.Max(0, open.Length - allowance)))
        {
            _tokens[token.Id] = token with { RevokedAt = at };
        }
    }

    private static bool IsOpen(RefreshTokenRecord token) =>
        token.RevokedAt is null && token.ReplacedBy is null;

    private static bool IsFinished(RefreshTokenRecord token, DateTimeOffset olderThan) =>
        (token.RevokedAt is { } revoked && revoked < olderThan)
        || (token.ReplacedBy is not null && token.IssuedAt < olderThan)
        || token.ExpiresAt < olderThan;

    private static string Key(byte[] hash) => Convert.ToBase64String(hash);
}
