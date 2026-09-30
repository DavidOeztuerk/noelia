using Noelia.Abstractions.Security.Keys;
using StackExchange.Redis;

namespace Noelia.Redis.Security;

/// <summary>A non-expiring Redis hash; HSETNX appends and indexes in one atomic operation.</summary>
public sealed class RedisDataProtectionKeyStore : IDataProtectionKeyStore
{
    private readonly IConnectionMultiplexer _connection;
    private readonly string _key;

    /// <summary>Uses the same prefix as the application's Redis cache registration.</summary>
    public RedisDataProtectionKeyStore(IConnectionMultiplexer connection, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        _connection = connection;
        // Keep the durable ring outside the generic cache namespace. Cache
        // RemoveByPatternAsync("*") must never erase signing material.
        _key = $"__noelia:keyring:v2:{keyPrefix.TrimEnd(':')}";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<DataProtectionStoredElement>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _connection.GetDatabase().HashGetAllAsync(_key).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return entries.Select(entry => new DataProtectionStoredElement((string)entry.Name!, (string)entry.Value!)).ToArray();
    }

    /// <inheritdoc />
    public async Task AppendAsync(string id, string xml, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        cancellationToken.ThrowIfCancellationRequested();
        var database = _connection.GetDatabase();
        if (await database.HashSetAsync(_key, id, xml, When.NotExists).ConfigureAwait(false)) return;
        var stored = await database.HashGetAsync(_key, id).ConfigureAwait(false);
        if (stored != xml)
            throw new InvalidOperationException("A different data protection element already uses this id.");
    }
}
