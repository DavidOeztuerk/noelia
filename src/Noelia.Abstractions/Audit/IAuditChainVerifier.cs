namespace Noelia.Abstractions.Audit;

/// <summary>
/// Reads a stored audit chain back and recomputes it.
/// </summary>
/// <remarks>
/// Separate from the writer on purpose. A component that both extends a chain
/// and judges it is the same party doing both, which is the arrangement a hash
/// chain exists to make unnecessary.
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

    /// <summary>Whether every entry and every link held.</summary>
    public bool IsIntact { get; init; }

    /// <summary>How many entries were recomputed.</summary>
    public long EntriesVerified { get; init; }

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
        new() { IsSupported = false, Note = note };
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
