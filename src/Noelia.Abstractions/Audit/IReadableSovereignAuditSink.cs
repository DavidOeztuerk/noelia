namespace Noelia.Abstractions.Audit;

/// <summary>
/// A sink whose chain can be read back and checked.
/// </summary>
/// <remarks>
/// <para>Until 6.0.0 <see cref="ISovereignAuditSink"/> had exactly one method,
/// <c>WriteAsync</c>. The trail was hash-chained, every entry hung off its
/// predecessor's hash, and an edit anywhere broke every hash after it — and
/// nothing in Noelia could check any of that. The promise was there; the means
/// of redeeming it was not. "The chain is intact" is a claim until somebody
/// recomputes it.</para>
///
/// <para><strong>Why this is its own interface.</strong> Adding a member to
/// <see cref="ISovereignAuditSink"/> would break every implementation, and a
/// sink that only writes is a legitimate sink: an append-only log or a foreign
/// system one writes into and not out of. Reading back is something a sink may
/// additionally be able to do, so it says so additionally. That is the same cut
/// 5.2.0 made for <see cref="IChainedSovereignAuditSink"/>.</para>
///
/// <para><strong>Order is the sink's promise.</strong> Entries come back in
/// chain order — the order they were appended — and not in timestamp order.
/// Two replicas can append inside the same millisecond, and their place in the
/// chain is decided by which one won the compare-and-set, not by their clocks.
/// A sink that returned them by timestamp would hand a verifier two entries the
/// wrong way round and make it report a break in a chain nobody touched.</para>
/// </remarks>
public interface IReadableSovereignAuditSink : ISovereignAuditSink
{
    /// <summary>
    /// Streams the stored chain, oldest first.
    /// </summary>
    /// <remarks>
    /// Streamed rather than returned as a list: a trail can hold millions of
    /// entries, and a signature that only offers the whole thing forces every
    /// verifier to hold all of it in memory at once.
    /// </remarks>
    /// <param name="since">
    /// Skip entries older than this, or <c>null</c> for the whole chain.
    /// Verifying from a point means trusting everything before it, so a
    /// verifier that uses this says so in its answer.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    IAsyncEnumerable<StoredAuditEntry> ReadAsync(
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One entry as it came back out of the store.
/// </summary>
/// <remarks>
/// The fields are exactly the ones the hash covers, and nothing else. A
/// verifier rebuilds the event from these and asks
/// <see cref="AuditEvent{T}.VerifyHash"/> to do the arithmetic, so the rule for
/// what a hash is computed over lives in one place. A verifier with its own
/// copy of that rule would keep passing after the rule changed.
/// </remarks>
public sealed record StoredAuditEntry
{
    /// <summary>The event's id.</summary>
    public required string Id { get; init; }

    /// <summary>When it was recorded.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Who acted.</summary>
    public required string ActorId { get; init; }

    /// <summary>In what capacity.</summary>
    public required string Capacity { get; init; }

    /// <summary>What they did.</summary>
    public required string Action { get; init; }

    /// <summary>To which resource.</summary>
    public required string Resource { get; init; }

    /// <summary>The correlation id, if one travelled with the request.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The recorded before-state, if any.</summary>
    public string? BeforeStateJson { get; init; }

    /// <summary>The recorded after-state, if any.</summary>
    public string? AfterStateJson { get; init; }

    /// <summary>The hash this entry was chained onto, or <c>null</c> for the first.</summary>
    public string? PreviousHash { get; init; }

    /// <summary>The hash stored with this entry.</summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Rebuilds the event this entry was, so its hash can be recomputed.
    /// </summary>
    /// <remarks>
    /// <c>object</c> as the type argument is deliberate and costs nothing: the
    /// hash is computed over the string fields only, so the resource type never
    /// enters it. A read-back that had to know the original type could not
    /// verify a trail written by a service it does not share code with — which
    /// is most of them.
    /// </remarks>
    public AuditEvent<object> AsEvent() => new()
    {
        Id = Id,
        Timestamp = Timestamp,
        ActorId = ActorId,
        Capacity = Capacity,
        Action = Action,
        Resource = Resource,
        CorrelationId = CorrelationId,
        BeforeStateJson = BeforeStateJson,
        AfterStateJson = AfterStateJson,
        PreviousHash = PreviousHash,
        Hash = Hash
    };

    /// <summary>Whether this entry's own hash still matches its contents.</summary>
    public bool VerifyHash() => AsEvent().VerifyHash();
}
