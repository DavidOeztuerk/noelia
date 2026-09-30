namespace Noelia.Abstractions.Audit;

/// <summary>
/// Reads a stored audit chain back and recomputes it.
/// </summary>
/// <remarks>
/// Separate from the writer for read-back verification. This separation alone
/// provides no independent trust: an attacker able to rewrite the store can
/// recompute unkeyed hashes. Completeness requires additional evidence.
/// </remarks>
public interface IAuditChainVerifier
{
    /// <summary>
    /// Walks the chain and reports what it found.
    /// </summary>
    /// <param name="since">
    /// Start here instead of at the beginning. Verifying from a point means
    /// taking everything before it on trust, and the answer says so.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<AuditChainVerification> VerifyAsync(
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default);
}

/// <summary>What a verification pass found.</summary>
public sealed record AuditChainVerification
{
    /// <summary>
    /// Whether the registered sink can be read back at all.
    /// </summary>
    /// <remarks>
    /// False is not a failed verification — it is the absence of one, and a
    /// report that showed the two the same way would let "we never checked"
    /// read as "we checked and it was fine".
    /// </remarks>
    public bool IsSupported { get; init; }

    /// <summary>Legacy hash/link result for the entries read, not a completeness verdict.</summary>
    /// <remarks>An empty successful walk also returns true. Prefer the explicit evidence states.</remarks>
    public bool IsIntact { get; init; }

    /// <summary>How many entries were examined, including the first failing entry if any.</summary>
    public long EntriesVerified { get; init; }

    /// <summary>Versioned limits of this verification; null for legacy or unspecified evidence.</summary>
    public AuditChainEvidence? Evidence { get; init; }

    /// <summary>The timestamp of the oldest entry seen, if any.</summary>
    public DateTimeOffset? Oldest { get; init; }

    /// <summary>The timestamp of the newest entry seen, if any.</summary>
    public DateTimeOffset? Newest { get; init; }

    /// <summary>The hash the walk ended on, if any.</summary>
    public string? Head { get; init; }

    /// <summary>
    /// Whether the walk began partway in, and everything before it was taken on
    /// trust.
    /// </summary>
    public bool IsPartial { get; init; }

    /// <summary>
    /// Where it first went wrong, or <c>null</c> when nothing did.
    /// </summary>
    /// <remarks>
    /// Named rather than merely counted. A verdict of "invalid" without a
    /// location sends an investigator back to doing by hand exactly the work
    /// they asked a machine to do.
    /// </remarks>
    public AuditChainBreak? FirstBreak { get; init; }

    /// <summary>Why, when there is nothing to report.</summary>
    public string? Note { get; init; }

    /// <summary>The answer when no readable sink is registered.</summary>
    /// <param name="note">What to tell the operator.</param>
    public static AuditChainVerification Unsupported(string note) =>
        new() { IsSupported = false, Note = note, Evidence = new() { SchemaVersion = 1 } };
}

/// <summary>What a chain walk establishes, independently of legacy pass/fail flags.</summary>
public sealed record AuditChainEvidence
{
    /// <summary>Evidence format; zero means unspecified, not the producer's current version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Hash/link consistency of the available entries, not authenticity.</summary>
    public AuditChainConsistency Consistency { get; init; }

    /// <summary>Completeness of the audit history. Version 1 cannot establish completeness.</summary>
    public AuditChainCompleteness Completeness { get; init; }

    /// <summary>The requested read boundary, not proof of what exists outside it.</summary>
    public AuditChainReadScope Scope { get; init; }

    /// <summary>Whether a stable snapshot boundary was established for this walk.</summary>
    public bool HasStableSnapshot { get; init; }

    /// <summary>Whether a checkpoint independent of the mutable store was verified.</summary>
    public bool HasIndependentCheckpoint { get; init; }

    /// <summary>Whether index positions were contiguous from the defined genesis sequence.</summary>
    public bool SequenceVerified { get; init; }

    /// <summary>Number of entries atomically captured, when a snapshot was available.</summary>
    public long? SnapshotEntryCount { get; init; }

    /// <summary>Head atomically captured with the snapshot; still from the mutable store.</summary>
    public string? SnapshotHead { get; init; }

    /// <summary>Why an independent checkpoint did or did not establish completeness.</summary>
    public string CheckpointState { get; init; } = "NotConfigured";

    /// <summary>Digest identifier of the matched signed checkpoint, if any.</summary>
    public string? CheckpointId { get; init; }

    /// <summary>UTC issue time of the matched signed checkpoint, if any.</summary>
    public DateTimeOffset? CheckpointIssuedAt { get; init; }
}

/// <summary>Consistency, an unavailable conclusion and a failed read are distinct outcomes.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AuditChainConsistency>))]
public enum AuditChainConsistency
{
    /// <summary>No consistency conclusion, including an empty or unsupported walk.</summary>
    Unknown,
    /// <summary>Observed hashes and links held; rewriting and recomputing can still produce this result.</summary>
    Consistent,
    /// <summary>An observed hash, link or required genesis predecessor failed.</summary>
    Broken,
    /// <summary>The reader did not finish; no conclusion about the unread remainder.</summary>
    ReadFailed
}

/// <summary>No complete-history claim is available without a defined independent anchor.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AuditChainCompleteness>))]
public enum AuditChainCompleteness
{
    /// <summary>The available data cannot establish completeness.</summary>
    Unknown,
    /// <summary>The caller explicitly requested only a time suffix.</summary>
    Partial,
    /// <summary>A stable full snapshot matches an independent authenticated checkpoint.</summary>
    Complete
}

/// <summary>Which part of the available store the caller asked to read.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AuditChainReadScope>))]
public enum AuditChainReadScope
{
    /// <summary>No read scope was established.</summary>
    Unknown,
    /// <summary>All entries exposed by the reader; not a stable snapshot or complete-history proof.</summary>
    AvailableStore,
    /// <summary>A suffix starting at the supplied time boundary.</summary>
    TimeSuffix
}

/// <summary>The first place the chain stopped adding up.</summary>
/// <param name="EntryId">The entry the walk was on.</param>
/// <param name="Timestamp">When that entry claims to have been recorded.</param>
/// <param name="Kind">Which of the two things failed.</param>
/// <param name="Detail">What was expected and what was found — hashes only.</param>
public sealed record AuditChainBreak(
    string EntryId,
    DateTimeOffset Timestamp,
    AuditChainBreakKind Kind,
    string Detail);

/// <summary>The two ways a chain can fail to add up.</summary>
/// <remarks>
/// <para>They are different findings. A rewritten entry is one record that no
/// longer matches its own hash; a removed or reordered entry leaves the records
/// intact and the links dangling. Telling an investigator which one happened is
/// most of the investigation.</para>
///
/// <para>Written out by name rather than as its ordinal. This travels to
/// readers that are not this assembly, and a number would let somebody reorder
/// the members one day and silently turn every stored finding into the other
/// kind.</para>
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AuditChainBreakKind>))]
public enum AuditChainBreakKind
{
    /// <summary>The entry's contents no longer produce the hash stored with it.</summary>
    ContentsChanged,

    /// <summary>The entry does not hang off the one before it.</summary>
    LinkBroken
}
