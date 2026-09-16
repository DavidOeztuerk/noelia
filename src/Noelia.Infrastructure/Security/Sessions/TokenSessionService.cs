using System.Security.Cryptography;
using System.Collections.Concurrent;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Noelia.Infrastructure.Security.Sessions;

/// <inheritdoc />
/// <remarks>
/// Holds the algorithm; the store holds the state. Splitting them is what lets
/// the same rules run over a dictionary in a test and over a database in
/// production without either knowing about the other.
/// </remarks>
public sealed class TokenSessionService(
    IRefreshTokenStore store,
    IOptions<TokenSessionOptions> options,
    TimeProvider clock,
    ILogger<TokenSessionService> logger,
    SessionObservations observed) : ITokenSessionService
{
    /// <summary>
    /// 32 bytes of randomness. Not a JWT: nothing reads anything out of a
    /// refresh token, so it carries nothing.
    /// </summary>
    private const int TokenBytes = 32;

    private readonly TokenSessionOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<SignInResult> SignInAsync(
        SubjectId subject,
        string? clientFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var session = SessionId.New();
        var sessionEnds = now + _options.AbsoluteSessionLifetime;
        var token = NewToken();

        await store.CreateAsync(
            new RefreshTokenRecord(
                RefreshTokenId.New(),
                session,
                subject,
                Hash(token),
                now,
                Earliest(now + _options.RefreshTokenLifetime, sessionEnds),
                now,
                sessionEnds,
                null,
                null,
                clientFingerprint),
            cancellationToken);

        observed.Add(subject);

        logger.LogInformation("Session {Session} started", session);

        return new SignInResult(session, token, Earliest(now + _options.RefreshTokenLifetime, sessionEnds));
    }

    /// <inheritdoc />
    public async Task<RefreshResult> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var now = clock.GetUtcNow();
        var successorToken = NewToken();

        // The successor is built before the store is asked, because the store's
        // one atomic step both consumes and writes. Its session attributes are
        // provisional: the store copies the real ones from the predecessor, so
        // a caller cannot extend a sign-in by asking for a longer token.
        var successor = new RefreshTokenRecord(
            RefreshTokenId.New(),
            SessionId.New(),
            SubjectId.New(),
            Hash(successorToken),
            now,
            now + _options.RefreshTokenLifetime,
            now,
            now + _options.AbsoluteSessionLifetime,
            null,
            null,
            null);

        var result = await store.TryConsumeAsync(
            Hash(refreshToken),
            successor,
            now,
            _options.ReuseGracePeriod,
            _options.MaxConcurrentTokensPerSession,
            cancellationToken);

        switch (result.Outcome)
        {
            case ConsumeOutcome.ReuseDetected:
                // Which holder was the thief is unknowable, so neither keeps
                // the session. The legitimate one signs in again with a
                // password the other does not have.
                logger.LogWarning("Refresh token replayed after the grace window; session ended");
                break;

            case ConsumeOutcome.RotatedWithinGrace:
                // Ordinary once; a run of them on one session is the shape a
                // guessing attack makes.
                logger.LogInformation(
                    "Refresh raced within the grace window for session {Session}",
                    result.Issued!.Value.Session);
                break;
        }

        if (result.Issued is not { } issued)
        {
            return new RefreshResult(result.Outcome, null, null, null, null);
        }

        observed.Add(issued.Subject);
        return new RefreshResult(
            result.Outcome, issued.Session, issued.Subject, successorToken, issued.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task SignOutAsync(SessionId session, CancellationToken cancellationToken = default)
    {
        var closed = await store.CloseSessionAsync(session, clock.GetUtcNow(), cancellationToken);
        logger.LogInformation("Session {Session} ended, {Count} token(s) closed", session, closed);
    }

    /// <inheritdoc />
    public async Task SignOutEverywhereAsync(
        SubjectId subject,
        CancellationToken cancellationToken = default)
    {
        var closed = await store.CloseAllSessionsAsync(subject, clock.GetUtcNow(), cancellationToken);
        observed.Remove(subject);
        logger.LogInformation("All sessions ended, {Count} token(s) closed", closed);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionSummary>> ActiveSessionsAsync(
        SubjectId subject,
        CancellationToken cancellationToken = default)
    {
        observed.Add(subject);
        return store.ActiveSessionsAsync(subject, clock.GetUtcNow(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TokenSessionInspection> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var entries = new List<TokenSessionEntry>();

        foreach (var subject in observed.Subjects)
        {
            var sessions = await store.ActiveSessionsAsync(subject, now, cancellationToken)
                .ConfigureAwait(false);

            entries.AddRange(sessions.Select(session => new TokenSessionEntry(
                subject.ToString(),
                session.Session.ToString(),
                session.StartedAt,
                session.LastUsedAt,
                session.ExpiresAt,
                session.ClientFingerprint is null
                    ? "not set"
                    : "set")));
        }

        return new TokenSessionInspection(
            true,
            true,
            entries.OrderByDescending(entry => entry.LastUsedAt).ToArray());
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// SHA-256, not a password hash. The token is already 256 bits of
    /// randomness, so there is no dictionary to slow down — and this runs on
    /// every refresh.
    /// </summary>
    private static byte[] Hash(string token) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    private static DateTimeOffset Earliest(DateTimeOffset first, DateTimeOffset second) =>
        first < second ? first : second;
}
