namespace Noelia.Abstractions.Security;

/// <summary>
/// A presented access token, as the middleware reads it from the claims.
/// </summary>
/// <remarks>
/// A record struct rather than loose parameters: new criteria (a locked tenant,
/// a device class) are added as fields without breaking every implementation of
/// <see cref="ITokenRevocationEvaluator"/>.
/// </remarks>
public readonly record struct TokenIdentity
{
    /// <summary>Identifier of this one token, from the <c>jti</c> claim.</summary>
    public required string TokenId { get; init; }

    /// <summary>Who the token was issued to, from the <c>sub</c> claim.</summary>
    public required string SubjectId { get; init; }

    /// <summary>
    /// When the token was issued, from the <c>iat</c> claim. Compared against the
    /// subject's cutoff, so it must be the value the issuer wrote, not the time
    /// the token was received.
    /// </summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// Session or device the token belongs to, from the <c>session_id</c> claim Noelia's issuer writes, or else the standard <c>sid</c>.
    /// Null when the issuer sets no such claim; session revocation is then
    /// unavailable and only token and cutoff revocation apply.
    /// </summary>
    public string? SessionId { get; init; }
}

/// <summary>Why a token was refused, or that it was not.</summary>
public enum RevocationReason
{
    /// <summary>The token stands.</summary>
    None = 0,

    /// <summary>This one token was revoked by its id.</summary>
    TokenRevoked,

    /// <summary>The session the token belongs to was revoked.</summary>
    SessionRevoked,

    /// <summary>The token was issued before the subject's cutoff.</summary>
    SubjectCutoff,

    /// <summary>The store could not be reached and the configured policy decided.</summary>
    StoreUnavailable
}

/// <summary>Outcome of a revocation check.</summary>
/// <param name="IsRevoked">Whether the token must be refused.</param>
/// <param name="Reason">What decided it.</param>
/// <param name="IsStale">
/// True when the answer came from a cached state rather than the store. Belongs
/// in the log and in a metric: a system running on stale revocation data is
/// working, but not as well as it looks.
/// </param>
public readonly record struct RevocationVerdict(
    bool IsRevoked,
    RevocationReason Reason,
    bool IsStale)
{
    /// <summary>The token stands, decided against a reachable store.</summary>
    public static readonly RevocationVerdict Valid = new(false, RevocationReason.None, false);
}

/// <summary>
/// Decides whether a presented token may still be honoured. The read side of
/// token revocation; register an implementation to enable the check.
/// </summary>
/// <remarks>
/// Called once per request, so implementations must answer all criteria in a
/// single round trip to their store.
/// </remarks>
public interface ITokenRevocationEvaluator
{
    /// <summary>Decides <paramref name="token"/>.</summary>
    ValueTask<RevocationVerdict> EvaluateAsync(
        TokenIdentity token,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Records revocations. The write side, separate from
/// <see cref="ITokenRevocationEvaluator"/> because the service issuing tokens
/// only writes and the middleware verifying them only reads.
/// </summary>
public interface ITokenRevocationWriter
{
    /// <summary>Revokes exactly one token.</summary>
    /// <param name="tokenId">The token's <c>jti</c>.</param>
    /// <param name="expiresAt">
    /// When the token would have expired anyway. The entry may be dropped after
    /// this point, never before.
    /// </param>
    /// <param name="reason">Recorded for the operator; never returned to callers.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RevokeTokenAsync(
        string tokenId,
        DateTimeOffset expiresAt,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes one session — "sign this device out".</summary>
    /// <param name="subjectId">Whose session it is; sessions are unique only within a subject.</param>
    /// <param name="sessionId">The session's <c>sid</c>.</param>
    /// <param name="expiresAt">When the entry may be dropped.</param>
    /// <param name="reason">Recorded for the operator.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RevokeSessionAsync(
        string subjectId,
        string sessionId,
        DateTimeOffset expiresAt,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses every token of <paramref name="subjectId"/> issued before
    /// <paramref name="cutoff"/>. This is what "sign out everywhere",
    /// "password changed" and "account suspended" call.
    /// </summary>
    /// <remarks>
    /// Idempotent and monotonic: a cutoff already further ahead is never moved
    /// back, so two concurrent revocations cannot undo one another.
    /// <para>
    /// A cutoff alone closes only the access-token window. Whatever issues
    /// refresh tokens has to revoke those in the same operation, or the holder
    /// simply refreshes into a new access token issued after the cutoff.
    /// </para>
    /// </remarks>
    /// <param name="subjectId">Whose tokens are refused.</param>
    /// <param name="cutoff">Tokens issued before this moment are refused.</param>
    /// <param name="reason">Recorded for the operator.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RevokeSubjectBeforeAsync(
        string subjectId,
        DateTimeOffset cutoff,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The key layout implementations must follow, so that services written in
/// different languages can share one revocation store.
/// </summary>
/// <remarks>
/// This is a wire contract, not an implementation detail: during a migration a
/// Python service may write the cutoff that a .NET service reads. Encoding is
/// therefore fixed — Unix <b>seconds</b>, decimal ASCII, no fractional part —
/// because <c>1755264720</c> and <c>1755264720.0</c> are not the same string.
/// Times are compared in whole seconds, matching the <c>iat</c> claim
/// (RFC 7519 NumericDate).
/// </remarks>
public static class TokenRevocationKeys
{
    /// <summary>Prefix every key carries. Fixed, so both sides agree.</summary>
    // Stable Noelia 4.x storage prefix. Changing it would hide existing
    // revocations and could make a revoked token valid again after upgrade.
    public const string Prefix = "noelia:revocation:";

    /// <summary>Key marking one revoked token. Presence alone means revoked.</summary>
    public static string Token(string tokenId) => $"{Prefix}token:{tokenId}";

    /// <summary>Key marking one revoked session. Presence alone means revoked.</summary>
    public static string Session(string subjectId, string sessionId) =>
        $"{Prefix}session:{subjectId}:{sessionId}";

    /// <summary>
    /// Key holding a subject's cutoff as Unix seconds in decimal ASCII. A token
    /// is refused when its <c>iat</c> is strictly less than this value.
    /// </summary>
    public static string SubjectCutoff(string subjectId) => $"{Prefix}cutoff:{subjectId}";

    /// <summary>
    /// Rounds <paramref name="cutoff"/> up to a whole second.
    /// </summary>
    /// <remarks>
    /// <c>iat</c> has second granularity, so a cutoff inside a second cannot be
    /// decided for tokens issued during it. Rounding up discards one token too
    /// many rather than one too few — at "password changed", the token issued in
    /// that very second is the one that matters.
    /// </remarks>
    public static long ToCutoffSeconds(DateTimeOffset cutoff) =>
        (cutoff.ToUnixTimeMilliseconds() + 999) / 1000;
}
