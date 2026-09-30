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
        string? expectedHead = null;
        var shared = sink as IChainedSovereignAuditSink;
        AuditReadSnapshot? snapshot = null;

        try
        {
            if (since is null && sink is IStableAuditSnapshotReader snapshotReader)
                snapshot = await snapshotReader.TryCaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);

            // The shared head is an observed boundary, not an independent
            // checkpoint. A move during the walk invalidates a positive result.
            if (shared is not null && snapshot is null)
                expectedHead = await shared.HeadAsync(cancellationToken).ConfigureAwait(false);

            var entries = snapshot is null
                ? sink.ReadAsync(since, cancellationToken)
                : SnapshotEntries(snapshot.Entries, cancellationToken);
            await foreach (var entry in entries
                .ConfigureAwait(false))
            {
                oldest ??= entry.Timestamp;
                newest = entry.Timestamp;
                count++;

                if (!entry.VerifyHash())
                {
                    return Broken(entry, count, oldest, since, snapshot, AuditChainBreakKind.ContentsChanged,
                        "The entry no longer produces the hash stored with it.");
                }

                // A whole-store walk must begin at the declared genesis. A
                // caller-requested suffix deliberately has no such boundary.
                // This detects a missing prefix, not a recomputed replacement.
                if (previous is null && since is null && entry.PreviousHash is not null)
                {
                    return Broken(entry, count, oldest, since, snapshot, AuditChainBreakKind.LinkBroken,
                        "The first available entry has a predecessor; the required genesis boundary is missing.");
                }

                if (previous is not null && entry.PreviousHash != previous.Hash)
                {
                    return Broken(entry, count, oldest, since, snapshot, AuditChainBreakKind.LinkBroken,
                        $"Expected to follow {Short(previous.Hash)}, but it follows "
                        + $"{Short(entry.PreviousHash)}.");
                }

                previous = entry;
            }

            if (snapshot is not null && snapshot.StoredHead != previous?.Hash)
            {
                return BoundaryFailed(count, oldest, newest, previous?.Hash, since, snapshot,
                    "The captured audit head does not match the last captured entry.");
            }
            if (shared is not null && snapshot is null)
            {
                var finalHead = await shared.HeadAsync(cancellationToken).ConfigureAwait(false);
                if (expectedHead != finalHead
                    || ((since is null || count > 0) && expectedHead != previous?.Hash))
                {
                    return BoundaryFailed(count, oldest, newest, previous?.Hash, since, null,
                        "The shared audit head changed during read-back or the reader did not reach it. "
                        + "Retry after writers settle; this head is not an independent checkpoint.");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Storage exceptions can carry payloads, endpoints and credentials.
            // A failed enumeration is not a demonstrated cryptographic break.
            return new AuditChainVerification
            {
                IsSupported = true, IsIntact = false, EntriesVerified = count,
                Oldest = oldest, Newest = newest, Head = previous?.Hash,
                IsPartial = since is not null,
                Evidence = Evidence(AuditChainConsistency.ReadFailed, since, snapshot),
                Note = "The audit reader did not finish. Any entries examined are only a prefix; "
                    + "no completeness or consistency conclusion is available for the requested range."
            };
        }

        var checkpointState = "NotConfigured";
        TrustedAuditCheckpoint? checkpoint = null;
        if (services.GetService<ITrustedAuditCheckpointSource>() is { } source)
        {
            if (snapshot is null)
                checkpointState = "SnapshotUnavailable";
            else if (!snapshot.SequenceVerified)
                checkpointState = "SequenceUnverified";
            else
            {
                try
                {
                    checkpoint = await source.ReadVerifiedAsync(snapshot.ChainId, cancellationToken)
                        .ConfigureAwait(false);
                    checkpointState = checkpoint is null ? "Unavailable"
                        : checkpoint.ChainId == snapshot.ChainId
                            && checkpoint.EntryCount == count
                            && checkpoint.GenesisHash == (snapshot.Entries.FirstOrDefault()?.Hash ?? string.Empty)
                            && checkpoint.HeadHash == (previous?.Hash ?? string.Empty)
                                ? "Matched" : "Mismatch";
                    if (checkpointState == "Matched" && count == 0)
                        checkpointState = "Empty";
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A forged or unreadable external document must not turn
                    // consistent store data into a trusted complete history.
                    checkpointState = "Rejected";
                }
            }
        }

        var complete = count > 0 && checkpointState == "Matched";
        return new AuditChainVerification
        {
            IsSupported = true,
            IsIntact = true,
            EntriesVerified = count,
            Oldest = oldest,
            Newest = newest,
            Head = previous?.Hash,
            IsPartial = since is not null,
            Evidence = Evidence(count == 0 ? AuditChainConsistency.Unknown : AuditChainConsistency.Consistent,
                since, snapshot, checkpointState, complete ? checkpoint : null),
            Note = snapshot is null
                ? "Hash/link read-back only. Completeness is not established: no stable snapshot or independent checkpoint was verified."
                : complete
                    ? "The atomic full-chain snapshot matches an independently signed checkpoint. "
                        + "This proves the captured chain relative to that signer, not that every real-world event was recorded."
                    : "Atomic hash/link snapshot only. Completeness is not established without a matching independent checkpoint. "
                        + "Recomputed or truncated data may still be consistent."
        };
    }

    private static AuditChainEvidence Evidence(AuditChainConsistency consistency, DateTimeOffset? since,
        AuditReadSnapshot? snapshot = null, string checkpointState = "NotConfigured",
        TrustedAuditCheckpoint? checkpoint = null) => new()
    {
        SchemaVersion = snapshot is null ? 1 : 2, Consistency = consistency,
        Completeness = checkpoint is not null ? AuditChainCompleteness.Complete
            : since is null ? AuditChainCompleteness.Unknown : AuditChainCompleteness.Partial,
        Scope = since is null ? AuditChainReadScope.AvailableStore : AuditChainReadScope.TimeSuffix,
        HasStableSnapshot = snapshot is not null,
        HasIndependentCheckpoint = checkpoint is not null,
        SequenceVerified = snapshot?.SequenceVerified is true,
        SnapshotEntryCount = snapshot?.Entries.Count,
        SnapshotHead = snapshot?.StoredHead,
        CheckpointState = checkpointState,
        CheckpointId = checkpoint?.CheckpointId,
        CheckpointIssuedAt = checkpoint?.IssuedAt
    };

    private static AuditChainVerification BoundaryFailed(long count, DateTimeOffset? oldest,
        DateTimeOffset? newest, string? head, DateTimeOffset? since, AuditReadSnapshot? snapshot, string note) =>
        new()
        {
            IsSupported = true, IsIntact = false, EntriesVerified = count,
            Oldest = oldest, Newest = newest, Head = head, IsPartial = since is not null,
            Evidence = Evidence(AuditChainConsistency.ReadFailed, since, snapshot), Note = note
        };

    private static async IAsyncEnumerable<StoredAuditEntry> SnapshotEntries(
        IReadOnlyList<StoredAuditEntry> entries,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
        await Task.CompletedTask;
    }

    private static AuditChainVerification Broken(
        StoredAuditEntry entry,
        long count,
        DateTimeOffset? oldest,
        DateTimeOffset? since,
        AuditReadSnapshot? snapshot,
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
            Evidence = Evidence(AuditChainConsistency.Broken, since, snapshot),
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
