namespace Noelia.Abstractions.Audit;

/// <summary>
/// Writes audit events to an external, sovereign sink (database, event store,
/// OpenSearch, …). The sink is provided by whoever operates the system.
/// </summary>
/// <remarks>
/// <para>A sink names a destination, and that decision belongs to the operator.
/// <c>Noelia.Redis</c> ships one; the in-memory sink in
/// <c>Noelia.Infrastructure</c> is the fallback for a single process.</para>
/// <para>
/// This lived in <c>Noelia.Infrastructure</c> until 5.2.0, which meant no
/// provider package could implement it: providers depend on the ports package,
/// not on the engine. A port nobody outside the engine can implement is not a
/// port.
/// </para>
/// </remarks>
public interface ISovereignAuditSink
{
    /// <summary>
    /// Persists a single audit event.
    /// </summary>
    /// <typeparam name="T">The resource type the event describes.</typeparam>
    /// <param name="auditEvent">The event, with its hash already computed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task WriteAsync<T>(AuditEvent<T> auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// A sink that also owns the chain head, so replicas share one sequence.
/// </summary>
/// <remarks>
/// <para>Without this, <c>AuditTrailService</c> advances the chain from a field
/// it holds itself. That is correct for one replica and silently wrong for two:
/// both start from their own head, and a verifier reading the store back finds
/// a broken chain on a system where nothing was tampered with.</para>
///
/// <para><strong>Why the write and the advance are one call.</strong> They
/// cannot be two. Advancing first and writing second leaves a head pointing at
/// an event that was never stored; writing first and advancing second lets a
/// second writer chain from the old head and produce two children of the same
/// predecessor. Only an implementation that does both at once can be correct,
/// so the interface asks for both at once and lets the store decide how —
/// a Lua script on a RESP server, a transaction on a database.</para>
///
/// <para>Transparency logs solve this by never letting two writers near one
/// tree: Trillian sequences each Merkle tree from a single signer process.
/// Compare-and-set is the same guarantee reached from the other side — the
/// writers race, exactly one wins, and the loser retries against the head that
/// won.</para>
/// </remarks>
public interface IChainedSovereignAuditSink : ISovereignAuditSink
{
    /// <summary>
    /// The current head of the shared chain, or <c>null</c> when it is empty.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<string?> HeadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the event and advances the head, or reports that someone else did.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> — rather than throwing — when the stored head is no
    /// longer <paramref name="expectedHead"/>. A lost race is an ordinary
    /// outcome under concurrency, not a failure, and the caller answers it by
    /// rebuilding the event on the new head.
    /// </remarks>
    /// <typeparam name="T">The resource type the event describes.</typeparam>
    /// <param name="auditEvent">The event, with its hash already computed.</param>
    /// <param name="expectedHead">The head this event was chained onto.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<bool> TryWriteAsync<T>(
        AuditEvent<T> auditEvent,
        string? expectedHead,
        CancellationToken cancellationToken = default);
}
