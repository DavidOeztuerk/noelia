using Noelia.Core.Identity;

namespace Noelia.Abstractions.Security.Sessions;

/// <summary>
/// Where refresh tokens live. Implement it over the database the application
/// already runs — this needs no server of its own.
/// </summary>
/// <remarks>
/// Rotation is one method rather than find-then-update on purpose. Split in
/// two, a second request can consume the same row between the two calls, and an
/// interface that permits a race gets implementations that have one.
/// <para>
/// <b>What an implementation owes, beyond the signatures.</b> A custom store
/// that does not keep these leaves a signed-out session open for a few seconds,
/// and nothing in the compiler or the types says so; the conformance suite
/// (<c>RefreshTokenStoreConformance</c> in the repository's test project) is the
/// executable form of this list.
/// </para>
/// <list type="bullet">
/// <item><description>
/// A sibling inside the grace window exists only while the newest token of the
/// chain the presented token belongs to is open. The chain runs from the
/// presented token along its replacements to the first row that has none;
/// siblings are not part of it. If that end is closed — by a sign-out, by the
/// cap, by a replay — the answer is <see cref="ConsumeOutcome.Revoked"/>, and
/// nothing is written. Once closed it stays closed, so every later presentation
/// gets the same answer. A chain with a missing row or a chain longer than 32
/// links cannot be shown to end in an open token and counts as closed.
/// </description></item>
/// <item><description>
/// That decision is made under a lock on that end (or an equivalent), and a
/// concurrent <see cref="CloseSessionAsync"/> or <see cref="CloseAllSessionsAsync"/>
/// waits for it: either the close runs first and the grace request finds the end
/// closed, or the grace request writes first and the close closes the sibling
/// too. A sibling must never be committed after a close has returned.
/// </description></item>
/// <item><description>
/// A closing method returns only when a fresh read finds nothing open in the
/// sessions it was asked to close, or it throws. It must also end, in a bounded
/// number of steps, against a client that keeps rotating meanwhile; how is the
/// store's own business (the Entity Framework store writes a marker row per
/// session, the in-process store holds one lock). Returning quietly with
/// something still open would claim an end that is not there.
/// </description></item>
/// <item><description>
/// A closed token without a replacement is <see cref="ConsumeOutcome.Revoked"/> on
/// every path, including the one where it was closed between being read and
/// being claimed. Past the ceiling of the session, a grace request is
/// <see cref="ConsumeOutcome.SessionExpired"/> and gets no sibling.
/// </description></item>
/// <item><description>
/// A replay after the grace window is judged first and stays
/// <see cref="ConsumeOutcome.ReuseDetected"/>, also in a session that has been
/// ended meanwhile.
/// </description></item>
/// </list>
/// <para>
/// <b>Inside a transaction of the caller.</b> An implementation over a database
/// joins a transaction the caller has open and opens none of its own, and a
/// statement is not repeated after a transient failure there. Then the sign-out
/// takes effect with the caller's commit, and two things can go wrong that the
/// store cannot prevent on one connection: a client that rotates a generation per
/// pass can keep a sign-out running up to its limit and make it throw, and a
/// flood of requests with the replaced token can make the database abort one of
/// the transactions, which for the caller means its transaction is lost. Both
/// are loud, neither leaves a session silently open, and both are a way for an
/// attacker to make a sign-out fail, not to survive one. The advice is
/// therefore to call <see cref="CloseSessionAsync"/> and
/// <see cref="CloseAllSessionsAsync"/> outside a transaction of your own, and the
/// refresh as well where protection against token theft matters under load, and
/// to write the audit row after it. The Entity Framework store does not change
/// the isolation level; under PostgreSQL REPEATABLE READ and SERIALIZABLE, and
/// SQL Server SNAPSHOT, an overlap is expected to end in an error of the
/// database. Under MySQL/InnoDB, where REPEATABLE READ is the default, the count
/// that ends a sign-out reads the snapshot of the caller's transaction and can
/// come out as zero when it should not: sign out under READ COMMITTED, or outside
/// a transaction of your own.
/// </para>
/// <para>
/// <b>Purge.</b> <see cref="PurgeAsync"/> removes closed rows, and the Entity
/// Framework store's marker rows are closed rows. Keep the horizon well behind
/// the present (the lifetime of a refresh token and its grace window, not
/// minutes): a marker removed in the middle of a sign-out takes its protection
/// with it, and the sign-out then relies on its passes alone.
/// </para>
/// </remarks>
public interface IRefreshTokenStore
{
    /// <summary>Writes the first token of a new sign-in.</summary>
    Task CreateAsync(RefreshTokenRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes the token with <paramref name="tokenHash"/> and writes
    /// <paramref name="successor"/>, indivisibly.
    /// </summary>
    /// <remarks>
    /// The transition may succeed only if the row has not been consumed
    /// already. Concurrent callers must see
    /// <see cref="ConsumeOutcome.RotatedWithinGrace"/>,
    /// <see cref="ConsumeOutcome.ReuseDetected"/> or, if the end of the chain was
    /// closed in the meantime (a sign-out, the cap), <see cref="ConsumeOutcome.Revoked"/>
    /// — never a second <see cref="ConsumeOutcome.Rotated"/>. An implementation
    /// that lets two callers both rotate has handed the same session to two
    /// holders. <see cref="ConsumeOutcome.Revoked"/> means the end of the chain was
    /// closed, not rotated: a rotation that overtakes the lock goes on, and the
    /// caller gets a sibling for the chain's new end.
    /// <para>
    /// Within <paramref name="grace"/> of the original rotation the loser of a
    /// race is issued a fresh token for the same session rather than being
    /// treated as a thief. The successor is not re-delivered: only its hash was
    /// kept, and keeping the token itself would give up the reason for hashing.
    /// </para>
    /// </remarks>
    /// <param name="tokenHash">SHA-256 of the presented token.</param>
    /// <param name="successor">The record to write if the transition is allowed.</param>
    /// <param name="now">The moment to judge expiry and grace against.</param>
    /// <param name="grace">
    /// How long after a rotation a second presentation is still a race rather
    /// than a theft. Seconds, not minutes: inside this window a replay by an
    /// attacker is indistinguishable from a second tab, so it is a deliberate
    /// and bounded concession.
    /// </param>
    /// <param name="maxConcurrentPerSession">
    /// How many unconsumed tokens one session may hold. Each rotation inside
    /// the grace window adds a sibling, and a client retry loop would otherwise
    /// grow the session without limit. On overflow the oldest is revoked.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<ConsumeResult> TryConsumeAsync(
        byte[] tokenHash,
        RefreshTokenRecord successor,
        DateTimeOffset now,
        TimeSpan grace,
        int maxConcurrentPerSession,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends one sign-in. Idempotent.
    /// </summary>
    /// <remarks>
    /// Named "close" rather than "revoke" because it takes effect at the next
    /// refresh, not on the next request: an access token already issued stands
    /// until it expires. <c>ITokenRevocationWriter.RevokeSessionAsync</c> is the
    /// one that acts immediately, and needs a store on the reading side.
    /// </remarks>
    /// <returns>How many tokens were still open.</returns>
    Task<int> CloseSessionAsync(
        SessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>Ends every sign-in of one person. Idempotent.</summary>
    Task<int> CloseAllSessionsAsync(
        SubjectId subject,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The sign-ins a person currently holds, newest first.
    /// </summary>
    /// <remarks>
    /// So a person can see their own sessions and end one. Also what answers a
    /// request for access under GDPR Article 15 without keeping any record of
    /// behaviour.
    /// </remarks>
    Task<IReadOnlyList<SessionSummary>> ActiveSessionsAsync(
        SubjectId subject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes rows that expired or were closed before <paramref name="olderThan"/>.
    /// </summary>
    /// <remarks>
    /// In batches, and called by the application on its own schedule. Noelia
    /// runs no background loop: retention is policy, the table belongs to the
    /// application, and a bulk delete competes with
    /// <see cref="TryConsumeAsync"/> for the same pages — on a single-writer
    /// database that blocks the sign-in path.
    /// </remarks>
    /// <param name="olderThan">Cut-off; rows still in use are never touched.</param>
    /// <param name="batchSize">Rows per statement.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were removed.</returns>
    Task<int> PurgeAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>One sign-in, as a person sees it in their own list.</summary>
/// <param name="Session">Names it, so it can be ended.</param>
/// <param name="StartedAt">When the sign-in happened.</param>
/// <param name="LastUsedAt">When it was last refreshed.</param>
/// <param name="ExpiresAt">When it ends on its own.</param>
/// <param name="ClientFingerprint">Whatever the application chose to record, if anything.</param>
public readonly record struct SessionSummary(
    SessionId Session,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset ExpiresAt,
    string? ClientFingerprint);
