using Noelia.Core.Identity;

namespace Noelia.Abstractions.Security.Sessions;

/// <summary>What a sign-in produced.</summary>
/// <param name="Session">Names the sign-in for its whole life.</param>
/// <param name="RefreshToken">The one issued value; stores retain only its hash.</param>
/// <param name="ExpiresAt">When this token stops working.</param>
public sealed record SignInResult(SessionId Session, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>What presenting a refresh token produced.</summary>
public sealed record RefreshResult(
    ConsumeOutcome Outcome,
    SessionId? Session,
    SubjectId? Subject,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt)
{
    /// <summary>Whether the caller may issue a new access token.</summary>
    public bool Succeeded =>
        Outcome is ConsumeOutcome.Rotated or ConsumeOutcome.RotatedWithinGrace;
}

/// <summary>Signing in, refreshing and signing out.</summary>
public interface ITokenSessionService
{
    /// <summary>Starts a sign-in and issues its first refresh token.</summary>
    Task<SignInResult> SignInAsync(
        SubjectId subject,
        string? clientFingerprint = null,
        CancellationToken cancellationToken = default);

    /// <summary>Exchanges a refresh token for its successor.</summary>
    Task<RefreshResult> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>Ends one sign-in.</summary>
    /// <remarks>
    /// Ends the session for good: a refresh token of it that was rotated a moment
    /// ago gets nothing back either, not even inside the grace window.
    /// <para>
    /// Take the session from the same signed-in identity as the person. With two
    /// person identities on one request (two person schemes), the current
    /// principal is the first of them, and this ends what belongs to the session
    /// you pass; the other person ends theirs with a request of their own.
    /// </para>
    /// <para>
    /// Where the application has a database transaction of its own open, this
    /// takes effect when it commits and, against a client that keeps rotating
    /// meanwhile, may fail loudly instead. Call it outside such a transaction,
    /// and write the audit row afterwards; see <see cref="IRefreshTokenStore"/>.
    /// </para>
    /// </remarks>
    Task SignOutAsync(SessionId session, CancellationToken cancellationToken = default);

    /// <summary>Ends every sign-in of one person.</summary>
    /// <remarks>
    /// Ends every session that person has, for good, as <see cref="SignOutAsync"/>
    /// does for one. The person is the one behind the current principal, never a
    /// claim read from some other identity on the request: a service token names
    /// no person and owns no session. With two person identities on one request
    /// (two person schemes), the current principal is the first of them, so only
    /// that person's sessions end, and the access tokens the application revokes
    /// for them; the other person signs out with a request of their own.
    /// <para>
    /// Inside a database transaction of the application's own the same limit
    /// applies as for <see cref="SignOutAsync"/>: call it outside, and write the
    /// audit row afterwards.
    /// </para>
    /// </remarks>
    Task SignOutEverywhereAsync(
        SubjectId subject,
        CancellationToken cancellationToken = default);

    /// <summary>The sign-ins a person currently holds.</summary>
    Task<IReadOnlyList<SessionSummary>> ActiveSessionsAsync(
        SubjectId subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a token-free view of sessions observed by this service instance.
    /// Implementations that cannot enumerate safely report unavailable.
    /// </summary>
    Task<TokenSessionInspection> InspectAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(TokenSessionInspection.Unavailable);
}

/// <summary>One active session without a token or raw device fingerprint.</summary>
public sealed record TokenSessionEntry(
    string Subject,
    string Session,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset ExpiresAt,
    string ClientFingerprintShape);

/// <summary>The sessions this instance has observed, not a cluster-wide inventory.</summary>
public sealed record TokenSessionInspection(
    bool IsAvailable,
    bool IsInstanceScoped,
    IReadOnlyList<TokenSessionEntry> Sessions)
{
    /// <summary>An implementation with no safe operator read model.</summary>
    public static TokenSessionInspection Unavailable { get; } =
        new(false, true, Array.Empty<TokenSessionEntry>());
}
