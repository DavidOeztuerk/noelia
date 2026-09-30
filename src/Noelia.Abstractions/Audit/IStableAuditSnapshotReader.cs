namespace Noelia.Abstractions.Audit;

/// <summary>A bounded, immutable point-in-time copy of one audit chain.</summary>
/// <remarks>
/// The entries, index order and stored head must be captured in one atomic
/// operation. The caller owns the memory needed for the copy. This does not
/// authenticate the mutable store or establish that earlier entries existed.
/// </remarks>
public sealed record AuditReadSnapshot(
    string ChainId,
    IReadOnlyList<StoredAuditEntry> Entries,
    string? StoredHead,
    bool SequenceVerified);

/// <summary>Optional stronger read contract for sinks that can freeze a bounded chain.</summary>
public interface IStableAuditSnapshotReader : IReadableSovereignAuditSink
{
    /// <summary>
    /// Captures the entire available chain atomically, or returns null when
    /// the configured size bound makes that impossible. Corrupt data throws.
    /// </summary>
    Task<AuditReadSnapshot?> TryCaptureSnapshotAsync(
        CancellationToken cancellationToken = default);
}
