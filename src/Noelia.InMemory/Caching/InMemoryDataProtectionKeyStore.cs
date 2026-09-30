using System.Collections.Concurrent;
using Noelia.Abstractions.Security.Keys;

namespace Noelia.InMemory.Caching;

/// <summary>Process-local key ring with no cache expiration; appropriate for one instance.</summary>
public sealed class InMemoryDataProtectionKeyStore : IDataProtectionKeyStore
{
    private readonly ConcurrentDictionary<string, string> _elements = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IReadOnlyCollection<DataProtectionStoredElement>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyCollection<DataProtectionStoredElement> copy = _elements
            .Select(pair => new DataProtectionStoredElement(pair.Key, pair.Value)).ToArray();
        return Task.FromResult(copy);
    }

    /// <inheritdoc />
    public Task AppendAsync(string id, string xml, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_elements.TryAdd(id, xml) && _elements[id] != xml)
            throw new InvalidOperationException("A different data protection element already uses this id.");
        return Task.CompletedTask;
    }
}
