namespace Noelia.Abstractions.Audit;

/// <summary>Externally authenticated expectation for one complete audit snapshot.</summary>
/// <param name="ChainId">The exact chain namespace this checkpoint names.</param>
/// <param name="GenesisHash">Expected hash of the first entry.</param>
/// <param name="HeadHash">Expected hash of the last entry.</param>
/// <param name="EntryCount">Expected number of entries.</param>
/// <param name="IssuedAt">When the independent signer accepted this state.</param>
/// <param name="CheckpointId">A safe identifier for the signed document.</param>
public sealed record TrustedAuditCheckpoint(
    string ChainId,
    string GenesisHash,
    string HeadHash,
    long EntryCount,
    DateTimeOffset IssuedAt,
    string CheckpointId);

/// <summary>Supplies checkpoints authenticated outside the mutable audit store.</summary>
/// <remarks>
/// Implementations must verify a document against a separately pinned trust
/// key before returning it. Reading a head/count from the same audit store is
/// not an implementation of this contract. The verifier also requires a stable
/// snapshot and checks genesis, sequence, count and head independently.
/// </remarks>
public interface ITrustedAuditCheckpointSource
{
    /// <summary>Returns a verified checkpoint for this chain, or null if none exists.</summary>
    Task<TrustedAuditCheckpoint?> ReadVerifiedAsync(
        string chainId,
        CancellationToken cancellationToken = default);
}
