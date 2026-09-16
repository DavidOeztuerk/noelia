using System.Runtime.CompilerServices;
using Noelia.Abstractions.Audit;

namespace Noelia.Infrastructure.Audit;

/// <summary>
/// In-memory sovereign audit sink for tests and local development.
/// </summary>
public sealed class InMemorySovereignAuditSink : ISovereignAuditSink, IReadableSovereignAuditSink
{
    private readonly List<object> _events = [];
    private readonly List<StoredAuditEntry> _stored = [];
    private readonly object _lock = new();

    /// <inheritdoc />
    public Task WriteAsync<T>(AuditEvent<T> auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        lock (_lock)
        {
            _events.Add(auditEvent);

            // Kept beside the typed list rather than derived from it on read:
            // the typed list holds AuditEvent<T> for every T a process used,
            // and a reader that had to re-open those generics to get at string
            // fields would be doing reflection to recover what was never
            // type-dependent in the first place.
            _stored.Add(new StoredAuditEntry
            {
                Id = auditEvent.Id,
                Timestamp = auditEvent.Timestamp,
                ActorId = auditEvent.ActorId,
                Capacity = auditEvent.Capacity,
                Action = auditEvent.Action,
                Resource = auditEvent.Resource,
                CorrelationId = auditEvent.CorrelationId,
                BeforeStateJson = auditEvent.BeforeStateJson,
                AfterStateJson = auditEvent.AfterStateJson,
                PreviousHash = auditEvent.PreviousHash,
                Hash = auditEvent.Hash
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// All recorded audit events in chronological order.
    /// </summary>
    public IReadOnlyList<object> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>
    /// Returns recorded events of type <typeparamref name="T"/>.
    /// </summary>
    public IReadOnlyList<AuditEvent<T>> EventsOf<T>()
    {
        lock (_lock)
        {
            return _events.OfType<AuditEvent<T>>().ToList();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// In append order, which for a single process is the chain order: this
    /// sink is written to under a lock, so there is no race for the sequence to
    /// disambiguate.
    /// </remarks>
    public async IAsyncEnumerable<StoredAuditEntry> ReadAsync(
        DateTimeOffset? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StoredAuditEntry[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _stored];
        }

        var started = since is null;

        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A contiguous suffix, not a filter: dropping individual entries
            // would leave a gap that reads as a broken link.
            if (!started)
            {
                if (entry.Timestamp < since)
                {
                    continue;
                }

                started = true;
            }

            yield return entry;
        }

        await Task.CompletedTask;
    }
}
