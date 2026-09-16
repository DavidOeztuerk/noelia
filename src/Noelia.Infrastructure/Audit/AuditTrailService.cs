using Microsoft.Extensions.Logging;
using Noelia.Abstractions.Audit;

namespace Noelia.Infrastructure.Audit;

/// <summary>
/// Default implementation of <see cref="IAuditTrailService"/> with in-memory
/// hash-chain state.
/// </summary>
/// <remarks>
/// Concurrent calls are serialised end to end — the hash is chained and the
/// event is written to the sink inside the same turn. Advancing the chain alone
/// under a lock is not enough: the writes would then reach the sink in another
/// order than they were chained in, and a verifier reading the store back would
/// find a broken chain on a system where nothing was tampered with.
/// <para>
/// Where several replicas run, each holds its own chain — the sink decides
/// whether that matters.
/// </para>
/// </remarks>
public sealed class AuditTrailService : IAuditTrailService, IDisposable
{
    private readonly ISovereignAuditSink _sink;
    private readonly ILogger<AuditTrailService> _logger;

    // A semaphore rather than a lock: the sink write belongs inside the
    // serialised turn, and it is asynchronous.
    private readonly SemaphoreSlim _chain = new(1, 1);
    private readonly Queue<AuditTrailEntry> _recent = new();
    private string? _previousHash;
    private long _length;
    private bool _chainValid = true;

    public AuditTrailService(ISovereignAuditSink sink, ILogger<AuditTrailService> logger)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AuditEvent<T>> RecordAsync<T>(
        string actorId,
        string capacity,
        string action,
        string resource,
        T? before = default,
        T? after = default,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capacity);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        AuditEvent<T> auditEvent;

        await _chain.WaitAsync(cancellationToken);
        try
        {
            AuditEvent<T> Build(string? previousHash) => new AuditEvent<T>
            {
                ActorId = actorId,
                Capacity = capacity,
                Action = action,
                Resource = resource,
                CorrelationId = correlationId,
                BeforeStateJson = AuditEvent<T>.ToJson(before),
                AfterStateJson = AuditEvent<T>.ToJson(after),
                PreviousHash = previousHash
            }.WithComputedHash();

            if (_sink is IChainedSovereignAuditSink shared)
            {
                auditEvent = await AppendToSharedChainAsync(shared, Build, cancellationToken);
            }
            else
            {
                auditEvent = Build(_previousHash);
                await _sink.WriteAsync(auditEvent, cancellationToken);
            }

            // Against the head this event actually chained onto. With a shared
            // sink that is the head the store held when this writer won, not
            // the one this process last saw.
            _chainValid &= auditEvent.VerifyHash();
            _length++;
            _recent.Enqueue(new AuditTrailEntry(
                auditEvent.Timestamp,
                auditEvent.ActorId,
                auditEvent.Capacity,
                auditEvent.Action,
                auditEvent.Resource));

            while (_recent.Count > 100)
            {
                _recent.Dequeue();
            }

            // Only once the sink has it: a chain advanced past an event that was
            // never stored leaves a gap no verifier can close.
            _previousHash = auditEvent.Hash;
        }
        finally
        {
            _chain.Release();
        }

        _logger.LogDebug(
            "Audit event {AuditEventId}: {Action} on {Resource} by {ActorId} [{Capacity}]",
            auditEvent.Id, action, resource, actorId, capacity);

        return auditEvent;
    }

    /// <summary>
    /// Appends to a chain other replicas also write to.
    /// </summary>
    /// <remarks>
    /// Read the head, build the event on it, and ask the store to store it and
    /// advance the head in one operation. A writer that loses the race rebuilds
    /// on the head that won, which is why the event is a function of the head
    /// rather than a value computed once: the hash depends on what it chains
    /// onto, so a retry cannot reuse the previous attempt.
    /// <para>
    /// The attempt limit is not a timeout in disguise. Each round trip means
    /// another writer committed in between, so exhausting it says the chain is
    /// under more contention than a single sequence can absorb — which an
    /// operator has to hear about rather than have smoothed over.
    /// </para>
    /// </remarks>
    private static async Task<AuditEvent<T>> AppendToSharedChainAsync<T>(
        IChainedSovereignAuditSink sink,
        Func<string?, AuditEvent<T>> build,
        CancellationToken cancellationToken)
    {
        const int attempts = 32;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var head = await sink.HeadAsync(cancellationToken);
            var candidate = build(head);

            if (await sink.TryWriteAsync(candidate, head, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"The shared audit chain could not be extended in {attempts} attempts. "
            + "Another writer committed before each of them.");
    }

    /// <inheritdoc />
    public Task<AuditEvent<T>> RecordAsync<T>(
        string actorId,
        Noelia.Core.Identity.Capacity capacity,
        string action,
        string resource,
        T? before = default,
        T? after = default,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        return RecordAsync(actorId, capacity.ToString(), action, resource, before, after, correlationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AuditTrailInspection> InspectAsync(
        int latest = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(latest, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(latest, 100);

        await _chain.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new AuditTrailInspection(
                true,
                _length,
                _chainValid,
                false,
                _recent.TakeLast(latest).ToArray());
        }
        finally
        {
            _chain.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _chain.Dispose();
}
