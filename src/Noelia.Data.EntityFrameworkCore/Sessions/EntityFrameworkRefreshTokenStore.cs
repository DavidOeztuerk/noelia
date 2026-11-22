using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Noelia.Data.EntityFrameworkCore.Sessions;

/// <summary>
/// Keeps refresh tokens in the database the application already runs.
/// </summary>
/// <remarks>
/// Which is the point: ending a session should cost a row, not a deployment.
/// Nothing here needs a server of its own.
/// <para>
/// Ending a session writes one extra row per session into the same table, a
/// closed and already expired token that nothing can present. Refreshing reads
/// it by primary key and writes nothing once it is there, which is what lets a
/// sign-out finish against a client that keeps rotating. It needs no column and
/// no migration, and it is removed by <see cref="PurgeAsync"/> like any closed
/// row. Queries of your own on <c>noelia_refresh_tokens</c> see it as a closed
/// token.
/// </para>
/// <para>
/// Everything here goes through the <see cref="DbContext"/> it is given, and
/// ending a session saves through it: changes the application has pending on
/// that context are written with it, as they already are by signing in and by
/// rotating.
/// </para>
/// </remarks>
public sealed class EntityFrameworkRefreshTokenStore<TContext>(TContext context) : IRefreshTokenStore
    where TContext : DbContext
{
    // How far the store follows a chain of rotations, and how many times a
    // sign-out looks again. Both are nets, not behaviour: a chain this long is
    // not a session anybody holds, and a sign-out that has not finished after
    // this many passes is not going to by being asked again in the same breath.
    private const int MaxChainLinks = 32;
    private const int MaxClosePasses = 32;

    // Waits before the first, second and third repeat of a statement that
    // failed in a way the database calls transient — a deadlock, say.
    private static readonly int[] RetryDelaysMilliseconds = [20, 100, 500];

    // Part of the stored data format: the identifier of the marker row is
    // derived with it. Changing it would orphan every marker already written.
    private static readonly byte[] MarkerDomain =
        Encoding.UTF8.GetBytes("noelia.refresh-token.session-ended.v1");

    private DbSet<NoeliaRefreshToken> Tokens => context.Set<NoeliaRefreshToken>();

    /// <summary>
    /// Whether the caller has a transaction of its own open: one from
    /// <c>BeginTransaction</c>, or an ambient <see cref="System.Transactions.TransactionScope"/>.
    /// </summary>
    /// <remarks>
    /// A connection admits no second transaction, so the store joins what is
    /// there. And a statement is only repeated after a failure where it is not
    /// part of someone else's unit of work: the transaction may be lost already,
    /// and repeating inside it would hide that.
    /// </remarks>
    private bool CallerOwnsATransaction =>
        context.Database.CurrentTransaction is not null
        || System.Transactions.Transaction.Current is not null;

    /// <inheritdoc />
    public async Task CreateAsync(
        RefreshTokenRecord record,
        CancellationToken cancellationToken = default)
    {
        Tokens.Add(ToRow(record));
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The transition is a conditional update whose row count decides the
    /// outcome: exactly one caller can move a row out of the unconsumed state,
    /// and everyone else reads zero and takes the loser path. Reading the row
    /// first is only to build the successor — the guard is re-evaluated by the
    /// database, so a concurrent winner still costs this caller its update.
    /// <para>
    /// Runs inside a transaction the caller already opened, and opens one only
    /// when there is none.
    /// </para>
    /// </remarks>
    public async Task<ConsumeResult> TryConsumeAsync(
        byte[] tokenHash,
        RefreshTokenRecord successor,
        DateTimeOffset now,
        TimeSpan grace,
        int maxConcurrentPerSession,
        CancellationToken cancellationToken = default)
    {
        var current = await Tokens.AsNoTracking()
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

        if (current is null)
        {
            return new ConsumeResult(ConsumeOutcome.NotFound, null);
        }

        if (current.ReplacedBy is not null)
        {
            return await AlreadyRotatedAsync(current, successor, now, grace, maxConcurrentPerSession, cancellationToken);
        }

        if (current.RevokedAt is not null)
        {
            return new ConsumeResult(ConsumeOutcome.Revoked, null);
        }

        // The ceiling first: once the sign-in is over both are true, because
        // successors are clamped to it.
        if (now.UtcDateTime >= current.SessionExpiresAt)
        {
            return new ConsumeResult(ConsumeOutcome.SessionExpired, null);
        }

        if (now.UtcDateTime >= current.ExpiresAt)
        {
            return new ConsumeResult(ConsumeOutcome.Expired, null);
        }

        var issued = Inherit(successor, current, now);

        switch (await ClaimAsync(current, issued, now, maxConcurrentPerSession, cancellationToken))
        {
            case Claim.Won:
                return new ConsumeResult(ConsumeOutcome.Rotated, issued);
            case Claim.SessionEnded:
                return new ConsumeResult(ConsumeOutcome.Revoked, null);
        }

        // Someone else moved the row between the read and the update. Answering
        // is itself a write — a sibling, or the end of the session — so it
        // happens out here, where no transaction is about to be left behind.
        var reread = await Tokens.AsNoTracking()
            .FirstOrDefaultAsync(token => token.Id == current.Id, cancellationToken);

        // A row that was closed, not rotated, is a closed row: the sign-out or
        // the cap that took it must not be undone by reading it as "rotated a
        // moment ago", which would hand out a sibling in a session that has
        // ended. A row that is gone was purged, and says nothing more.
        if (reread?.ReplacedBy is null)
        {
            return new ConsumeResult(ConsumeOutcome.Revoked, null);
        }

        return await AlreadyRotatedAsync(reread, successor, now, grace, maxConcurrentPerSession, cancellationToken);
    }

    private enum Claim
    {
        /// <summary>The row was moved and its successor written.</summary>
        Won,

        /// <summary>Someone else moved the row first.</summary>
        Lost,

        /// <summary>The session carries the marker of a sign-out in progress.</summary>
        SessionEnded
    }

    /// <summary>
    /// Moves one row out of the unconsumed state and writes its successor, both
    /// or neither.
    /// </summary>
    /// <remarks>
    /// A transaction is opened only when none is open. A caller may run the
    /// store inside a unit of work of its own — writing an audit row in the same
    /// transaction as the change it records is the usual reason — and a
    /// connection admits no second transaction. Where one is open this joins it,
    /// so the rotation commits and rolls back with the caller's work.
    /// <para>
    /// The marker of a sign-out is read first, before the update. A rotation
    /// that finds it writes nothing: the sign-out is not waiting for this
    /// rotation, it is waiting for the rotations that began before the marker,
    /// and this one did not.
    /// </para>
    /// </remarks>
    private async Task<Claim> ClaimAsync(
        NoeliaRefreshToken current,
        RefreshTokenRecord issued,
        DateTimeOffset now,
        int maxConcurrentPerSession,
        CancellationToken cancellationToken)
    {
        await using var transaction = CallerOwnsATransaction
            ? null
            : await context.Database.BeginTransactionAsync(cancellationToken);

        var outcome = Claim.SessionEnded;

        if (!await HasMarkerAsync(current.SessionId, cancellationToken))
        {
            var claimed = await Tokens
                .Where(token => token.Id == current.Id
                                && token.RevokedAt == null
                                && token.ReplacedBy == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(token => token.RevokedAt, now.UtcDateTime)
                        .SetProperty(token => token.ReplacedBy, issued.Id.Value),
                    cancellationToken);

            outcome = claimed > 0 ? Claim.Won : Claim.Lost;

            if (claimed > 0)
            {
                Tokens.Add(ToRow(issued));
                await context.SaveChangesAsync(cancellationToken);
                await CapAsync(issued.Session, maxConcurrentPerSession, now, cancellationToken);
            }
        }

        // Also when nothing was claimed: the conditional update matched no row,
        // so there is nothing to undo, and rolling back would reach past this
        // method into work that is not its own.
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// Decides whether a second presentation is a race or a theft, and inside
    /// the window whether the session is still there to give a sibling to.
    /// </summary>
    /// <remarks>
    /// In this order, and the order is the point. A replay after the window is a
    /// theft whatever became of the session. Otherwise a sibling is written only
    /// while the newest token of the chain the presented one belongs to is
    /// open, and that is decided under a lock on it, so that a sign-out running
    /// at the same moment waits for this call and closes the sibling too, or has
    /// closed that token first and is seen here.
    /// </remarks>
    private async Task<ConsumeResult> AlreadyRotatedAsync(
        NoeliaRefreshToken current,
        RefreshTokenRecord successor,
        DateTimeOffset now,
        TimeSpan grace,
        int maxConcurrentPerSession,
        CancellationToken cancellationToken)
    {
        if (current.RevokedAt is null || now.UtcDateTime > current.RevokedAt.Value + grace)
        {
            await CloseSessionAsync(new SessionId(current.SessionId), now, cancellationToken);
            return new ConsumeResult(ConsumeOutcome.ReuseDetected, null);
        }

        // Before the transaction and the lock, so that requests arriving after a
        // sign-out do not queue up behind the end of the chain. The decisive
        // read is the one after the lock.
        if (await HasMarkerAsync(current.SessionId, cancellationToken))
        {
            return new ConsumeResult(ConsumeOutcome.Revoked, null);
        }

        // The caller's own transaction is joined, not replaced: the sibling and
        // the lock then commit with the caller's work, and a sign-out waits for
        // that commit.
        await using var transaction = CallerOwnsATransaction
            ? null
            : await context.Database.BeginTransactionAsync(cancellationToken);

        var result = await SiblingUnderLockAsync(
            current, successor, now, maxConcurrentPerSession, cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    private async Task<ConsumeResult> SiblingUnderLockAsync(
        NoeliaRefreshToken current,
        RefreshTokenRecord successor,
        DateTimeOffset now,
        int maxConcurrentPerSession,
        CancellationToken cancellationToken)
    {
        var revoked = new ConsumeResult(ConsumeOutcome.Revoked, null);

        // The end of the chain the presented token belongs to: follow the
        // replacements to the first row that has none. Siblings are not part of
        // the chain. Hops are counted over the walk and the lock together, and
        // a chain that is too long or has a hole in it (a purge) cannot be shown
        // to end in an open token, so it is read as closed.
        var hops = 1;
        var end = current.ReplacedBy!.Value;

        while (true)
        {
            var link = await ReplacementOfAsync(end, cancellationToken);
            if (link is null)
            {
                return revoked;
            }

            if (link.ReplacedBy is not { } next)
            {
                break;
            }

            if (++hops > MaxChainLinks)
            {
                return revoked;
            }

            end = next;
        }

        // Lock the end. A statement that changes nothing and matches only an
        // open row: it holds the row until the transaction ends, so a sign-out
        // that wants to close it waits here, and a sign-out that got there first
        // leaves nothing to match.
        while (await LockAsync(end, cancellationToken) == 0)
        {
            // Not locked: closed, or rotated while this call was on its way. A
            // rotation means the chain went on, and the lock follows it; a
            // closure ends the matter, and so does a row that is gone.
            var link = await ReplacementOfAsync(end, cancellationToken);
            if (link?.ReplacedBy is not { } next || ++hops > MaxChainLinks)
            {
                return revoked;
            }

            end = next;
        }

        // The decisive read: after the lock. A sign-out that wrote its marker
        // before this point is seen here; one that writes it later has to get
        // past the lock on the end of the chain first.
        if (await HasMarkerAsync(current.SessionId, cancellationToken))
        {
            return revoked;
        }

        // A sibling past the ceiling would be born dead and reported as a
        // success.
        if (now.UtcDateTime >= current.SessionExpiresAt)
        {
            return new ConsumeResult(ConsumeOutcome.SessionExpired, null);
        }

        // Inside the window: a fresh sibling for the same sign-in. The recorded
        // successor is not handed back — only its hash was kept, and keeping the
        // token would give up the reason for hashing it.
        var sibling = Inherit(successor, current, now);

        Tokens.Add(ToRow(sibling));
        await context.SaveChangesAsync(cancellationToken);
        await CapAsync(sibling.Session, maxConcurrentPerSession, now, cancellationToken);

        return new ConsumeResult(ConsumeOutcome.RotatedWithinGrace, sibling);
    }

    private async Task<Replacement?> ReplacementOfAsync(Guid id, CancellationToken cancellationToken) =>
        await Tokens.AsNoTracking()
            .Where(token => token.Id == id)
            .Select(token => new Replacement(token.ReplacedBy))
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record Replacement(Guid? ReplacedBy);

    private async Task<int> LockAsync(Guid id, CancellationToken cancellationToken) =>
        await Tokens
            .Where(token => token.Id == id
                            && token.RevokedAt == null
                            && token.ReplacedBy == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, token => token.RevokedAt),
                cancellationToken);

    /// <inheritdoc />
    public async Task<int> CloseSessionAsync(
        SessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var id = session.Value;

        // A session with no row has nothing to close and nothing to mark.
        var known = await RetryAsync(
            async () => await Tokens.AsNoTracking().AnyAsync(token => token.SessionId == id, cancellationToken),
            cancellationToken);

        return known
            ? await CloseAsync([id], $"session {id}", at, cancellationToken)
            : 0;
    }

    /// <inheritdoc />
    public async Task<int> CloseAllSessionsAsync(
        SubjectId subject,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var person = subject.Value;
        var atUtc = at.UtcDateTime;

        // Chosen by a condition no update changes — the ceiling — and not by
        // "has an open token", which a rotation in flight can make untrue
        // between this read and the write. A session past its ceiling can get no
        // usable token any more, so it is only picked up through an open row,
        // which is closed, as it always was.
        var sessions = await RetryAsync(
            async () => await Tokens.AsNoTracking()
                .Where(token => token.SubjectId == person
                                && (token.SessionExpiresAt > atUtc
                                    || (token.RevokedAt == null && token.ReplacedBy == null)))
                .Select(token => token.SessionId)
                .Distinct()
                .ToListAsync(cancellationToken),
            cancellationToken);

        return sessions.Count == 0
            ? 0
            : await CloseAsync(sessions, $"the sessions of person {person}", at, cancellationToken);
    }

    /// <summary>
    /// Writes a marker for each session, then closes open tokens until a fresh
    /// count finds none.
    /// </summary>
    /// <remarks>
    /// The marker first: from its commit on, no rotation and no grace request
    /// writes anything, so what the passes have to chase is limited to what was
    /// already under way. The count is its own statement, because a pass that
    /// waited on a concurrent rotation does not see that rotation's successor,
    /// and "this pass closed nothing" would then be read as "nothing is open".
    /// Update and count touch only the sessions chosen at the start: a sign-in
    /// that begins meanwhile is not part of this sign-out.
    /// </remarks>
    private async Task<int> CloseAsync(
        List<Guid> sessions,
        string what,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var atUtc = at.UtcDateTime;
        sessions.Sort();

        // One at a time and in a fixed order, so that two sign-outs of the same
        // person cannot wait on each other's markers in a circle.
        foreach (var session in sessions)
        {
            await RetryAsync(
                async () => await WriteMarkerAsync(session, atUtc, cancellationToken),
                cancellationToken);
        }

        var closed = 0;
        for (var pass = 0; pass < MaxClosePasses; pass++)
        {
            closed += await RetryAsync(
                async () => await Tokens
                    .Where(token => sessions.Contains(token.SessionId)
                                    && token.RevokedAt == null
                                    && token.ReplacedBy == null)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(token => token.RevokedAt, atUtc),
                        cancellationToken),
                cancellationToken);

            var stillOpen = await RetryAsync(
                async () => await Tokens.AsNoTracking()
                    .CountAsync(
                        token => sessions.Contains(token.SessionId)
                                 && token.RevokedAt == null
                                 && token.ReplacedBy == null,
                        cancellationToken),
                cancellationToken);

            if (stillOpen == 0)
            {
                return closed;
            }
        }

        // Loud, not closed: returning quietly would claim an end that is not
        // there. Closing again is safe — it is idempotent — and the message
        // names what may have stayed open, by identifier and never by token.
        throw new InvalidOperationException(
            $"Closing {what} did not finish: tokens were still being opened after {MaxClosePasses} passes. "
            + "Some of them may still be open; closing again is safe.");
    }

    /// <summary>
    /// Writes the marker row of a session unless it is there. Returns 1 if this
    /// call wrote it.
    /// </summary>
    private async Task<int> WriteMarkerAsync(
        Guid session,
        DateTime at,
        CancellationToken cancellationToken)
    {
        var id = MarkerId(session);

        if (await Tokens.AsNoTracking().AnyAsync(token => token.Id == id, cancellationToken))
        {
            return 0;
        }

        var template = await Tokens.AsNoTracking()
            .Where(token => token.SessionId == session)
            .OrderBy(token => token.IssuedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (template is null)
        {
            return 0;
        }

        // A row like the others, closed and expired from the start, so that it is
        // never open, never listed and purged like any closed one. The hash is
        // random: it is not the hash of any token, and nobody can present it.
        var marker = new NoeliaRefreshToken
        {
            Id = id,
            SessionId = session,
            SubjectId = template.SubjectId,
            TokenHash = RandomNumberGenerator.GetBytes(32),
            IssuedAt = at,
            ExpiresAt = at,
            RevokedAt = at,
            SessionStartedAt = template.SessionStartedAt,
            SessionExpiresAt = template.SessionExpiresAt
        };

        Tokens.Add(marker);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return 1;
        }
        catch (Exception exception)
        {
            // Out of the change tracker first, so the failed insert is not
            // attempted again by the next save on this context, whoever's it is.
            context.Entry(marker).State = EntityState.Detached;

            // A second sign-out may have written it between the read and the
            // insert. That is the one failure that is not a failure: the marker
            // is there, which is all that was wanted. Anything else is thrown.
            if (exception is DbUpdateException
                && await Tokens.AsNoTracking().AnyAsync(token => token.Id == id, cancellationToken))
            {
                return 0;
            }

            throw;
        }
    }

    private async Task<bool> HasMarkerAsync(Guid session, CancellationToken cancellationToken)
    {
        var id = MarkerId(session);
        return await Tokens.AsNoTracking().AnyAsync(token => token.Id == id, cancellationToken);
    }

    /// <summary>
    /// The identifier of a session's marker row: derived, so that there is at
    /// most one, and found by primary key.
    /// </summary>
    /// <remarks>
    /// The first 16 bytes of SHA-256 over the fixed domain and the session's
    /// bytes in the order .NET gives them. Part of the stored data format.
    /// </remarks>
    private static Guid MarkerId(Guid session)
    {
        Span<byte> input = stackalloc byte[MarkerDomain.Length + 16];
        MarkerDomain.CopyTo(input);
        session.TryWriteBytes(input[MarkerDomain.Length..]);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    /// <summary>
    /// Runs one statement and, if the database reports a transient failure,
    /// repeats it up to three times after short pauses.
    /// </summary>
    /// <remarks>
    /// Never inside a transaction of the caller: it may be lost already, and a
    /// repeat inside it would hide that and carry on in a state the caller does
    /// not know about. There the failure is the caller's to see.
    /// </remarks>
    private async Task<T> RetryAsync<T>(Func<Task<T>> statement, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await statement();
            }
            catch (Exception exception) when (
                attempt < RetryDelaysMilliseconds.Length
                && !CallerOwnsATransaction
                && IsTransient(exception))
            {
                await Task.Delay(RetryDelaysMilliseconds[attempt], cancellationToken);
            }
        }
    }

    // A deadlock reaches a statement as itself, or wrapped by SaveChanges.
    private static bool IsTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionSummary>> ActiveSessionsAsync(
        SubjectId subject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var open = await Tokens.AsNoTracking()
            .Where(token => token.SubjectId == subject.Value
                            && token.RevokedAt == null
                            && token.ReplacedBy == null
                            && token.ExpiresAt > now.UtcDateTime
                            && token.SessionExpiresAt > now.UtcDateTime)
            .ToListAsync(cancellationToken);

        return open
            .GroupBy(token => token.SessionId)
            .Select(group => new SessionSummary(
                new SessionId(group.Key),
                Utc(group.Min(token => token.SessionStartedAt)),
                Utc(group.Max(token => token.IssuedAt)),
                Utc(group.Min(token => token.SessionExpiresAt)),
                group.OrderByDescending(token => token.IssuedAt).First().ClientFingerprint))
            .OrderByDescending(summary => summary.LastUsedAt)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        // In batches, because a single unbounded delete competes with
        // TryConsumeAsync for the same pages — on a single-writer database that
        // blocks the sign-in path.
        var finished = await Tokens
            .Where(token => (token.RevokedAt != null && token.RevokedAt < olderThan.UtcDateTime)
                            || (token.ReplacedBy != null && token.IssuedAt < olderThan.UtcDateTime)
                            || token.ExpiresAt < olderThan.UtcDateTime)
            .OrderBy(token => token.IssuedAt)
            .Take(batchSize)
            .Select(token => token.Id)
            .ToListAsync(cancellationToken);

        if (finished.Count == 0)
        {
            return 0;
        }

        return await Tokens
            .Where(token => finished.Contains(token.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Keeps a session's open tokens within its allowance, oldest first.</summary>
    private async Task CapAsync(
        SessionId session,
        int allowance,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(allowance, 1);

        var open = await Tokens.AsNoTracking()
            .Where(token => token.SessionId == session.Value
                            && token.RevokedAt == null
                            && token.ReplacedBy == null)
            .OrderBy(token => token.IssuedAt)
            .Select(token => token.Id)
            .ToListAsync(cancellationToken);

        var surplus = open.Take(Math.Max(0, open.Count - allowance)).ToList();
        if (surplus.Count == 0)
        {
            return;
        }

        // Only rows still open: a surplus row rotated since the read keeps its
        // RevokedAt, which its grace window counts from.
        await Tokens
            .Where(token => surplus.Contains(token.Id)
                            && token.RevokedAt == null
                            && token.ReplacedBy == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, at.UtcDateTime),
                cancellationToken);
    }

    /// <summary>
    /// Gives the successor the sign-in's own attributes, so a caller cannot
    /// extend a session past its ceiling by asking for a longer token.
    /// </summary>
    private static RefreshTokenRecord Inherit(
        RefreshTokenRecord successor,
        NoeliaRefreshToken current,
        DateTimeOffset now) =>
        successor with
        {
            Session = new SessionId(current.SessionId),
            Subject = new SubjectId(current.SubjectId),
            IssuedAt = now,
            SessionStartedAt = Utc(current.SessionStartedAt),
            SessionExpiresAt = Utc(current.SessionExpiresAt),
            ExpiresAt = successor.ExpiresAt > Utc(current.SessionExpiresAt)
                ? Utc(current.SessionExpiresAt)
                : successor.ExpiresAt,
            RevokedAt = null,
            ReplacedBy = null,
            ClientFingerprint = current.ClientFingerprint
        };

    /// <summary>Reads a stored UTC value back as an offset-carrying one.</summary>
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static NoeliaRefreshToken ToRow(RefreshTokenRecord record) => new()
    {
        Id = record.Id.Value,
        SessionId = record.Session.Value,
        SubjectId = record.Subject.Value,
        TokenHash = record.TokenHash,
        IssuedAt = record.IssuedAt.UtcDateTime,
        ExpiresAt = record.ExpiresAt.UtcDateTime,
        SessionStartedAt = record.SessionStartedAt.UtcDateTime,
        SessionExpiresAt = record.SessionExpiresAt.UtcDateTime,
        RevokedAt = record.RevokedAt?.UtcDateTime,
        ReplacedBy = record.ReplacedBy?.Value,
        ClientFingerprint = record.ClientFingerprint
    };
}

public static class EntityFrameworkSessionRegistration
{
    /// <summary>
    /// Keeps refresh tokens in <typeparamref name="TContext"/>.
    /// </summary>
    /// <remarks>
    /// Call <c>ConfigureNoeliaRefreshTokens</c> from the context's
    /// <c>OnModelCreating</c> and add a migration; the table is the
    /// application's, in the application's database.
    /// </remarks>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection
        AddEntityFrameworkRefreshTokens<TContext>(
            this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        where TContext : DbContext =>
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddScoped<IRefreshTokenStore, EntityFrameworkRefreshTokenStore<TContext>>(services);
}
