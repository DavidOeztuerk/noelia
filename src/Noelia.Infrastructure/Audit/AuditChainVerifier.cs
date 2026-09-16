using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Audit;

namespace Noelia.Infrastructure.Audit;

/// <summary>
/// Recomputes a stored chain, entry by entry, and says where it first stops
/// adding up.
/// </summary>
/// <remarks>
/// <para>Two things are checked per entry and they catch different edits.
/// Recomputing the entry's own hash catches a record that was rewritten in
/// place. Comparing its <c>PreviousHash</c> to the hash of the entry before it
/// catches a record that was removed, inserted or moved — where every entry is
/// individually intact and only the sequence is a lie.</para>
///
/// <para>The walk stops at the first break and keeps the count it reached.
/// Going on would produce a list of consequences rather than findings: once a
/// link is broken every later link is broken too, and an investigator handed
/// four hundred breaks has to find the first one themselves.</para>
/// </remarks>
public sealed class AuditChainVerifier(IServiceProvider services) : IAuditChainVerifier
{
    /// <inheritdoc />
    public async Task<AuditChainVerification> VerifyAsync(
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default)
    {
        if (services.GetService<ISovereignAuditSink>() is not IReadableSovereignAuditSink sink)
        {
            return AuditChainVerification.Unsupported(
                "The registered audit sink cannot be read back, so its chain cannot be "
                + "recomputed here. Register a sink that implements "
                + "IReadableSovereignAuditSink — AddRedisSovereignAudit() from Noelia.Redis "
                + "does — or verify the chain where it is stored.");
        }

        StoredAuditEntry? previous = null;
        var count = 0L;
        DateTimeOffset? oldest = null;
        DateTimeOffset? newest = null;

        await foreach (var entry in sink.ReadAsync(since, cancellationToken)
            .ConfigureAwait(false))
        {
            oldest ??= entry.Timestamp;
            newest = entry.Timestamp;
            count++;

            if (!entry.VerifyHash())
            {
                return Broken(entry, count, oldest, since, AuditChainBreakKind.ContentsChanged,
                    "The entry no longer produces the hash stored with it.");
            }

            // The first entry seen has nothing before it to hang off. That is
            // true both for the genesis entry and for the first entry of a
            // partial walk — which is exactly what makes a partial walk a
            // smaller claim, and why the answer says it was one.
            if (previous is not null && entry.PreviousHash != previous.Hash)
            {
                return Broken(entry, count, oldest, since, AuditChainBreakKind.LinkBroken,
                    $"Expected to follow {Short(previous.Hash)}, but it follows "
                    + $"{Short(entry.PreviousHash)}.");
            }

            previous = entry;
        }

        return new AuditChainVerification
        {
            IsSupported = true,
            IsIntact = true,
            EntriesVerified = count,
            Oldest = oldest,
            Newest = newest,
            Head = previous?.Hash,
            IsPartial = since is not null
        };
    }

    private static AuditChainVerification Broken(
        StoredAuditEntry entry,
        long count,
        DateTimeOffset? oldest,
        DateTimeOffset? since,
        AuditChainBreakKind kind,
        string detail) =>
        new()
        {
            IsSupported = true,
            IsIntact = false,
            EntriesVerified = count,
            Oldest = oldest,
            Newest = entry.Timestamp,
            IsPartial = since is not null,
            FirstBreak = new AuditChainBreak(entry.Id, entry.Timestamp, kind, detail)
        };

    /// <summary>
    /// Enough of a hash to identify it in a sentence, and no more.
    /// </summary>
    /// <remarks>
    /// The full hashes are in the store, where the investigation happens. This
    /// text ends up on an operator dashboard, and a dashboard is a screen
    /// somebody else can be standing behind.
    /// </remarks>
    private static string Short(string? hash) =>
        hash is null ? "nothing" : hash.Length <= 12 ? hash : hash[..12] + "…";
}
