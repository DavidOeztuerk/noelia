using System.Text.Json;
using Microsoft.Extensions.Logging;
using Noelia.Abstractions.Audit;
using StackExchange.Redis;

namespace Noelia.Redis.Security.Audit;

/// <summary>
/// One sovereign audit chain, shared by every replica writing to this server.
/// </summary>
/// <remarks>
/// <para>Storing the event and advancing the head is one Lua call, because it
/// cannot be two. Advance first and a crash leaves a head pointing at an event
/// nobody stored; store first and a second writer chains onto the old head,
/// producing two children of one predecessor. Redis runs a script to
/// completion without interleaving another client, so the pair is atomic.</para>
///
/// <para>The same shape already guards the security audit trail in this
/// package. Transparency logs answer this by never letting two writers near one
/// tree — Trillian sequences each Merkle tree from a single signer. Compare-and
/// -set reaches the same guarantee from the other side: the writers race,
/// exactly one wins, and the loser rebuilds on the head that won.</para>
///
/// <para><strong>What this does not do.</strong> It does not verify the chain
/// it is extending. A verifier reads the events back and checks the links; a
/// writer that also judged them would be the same party doing both, which is
/// what a hash chain exists to avoid.</para>
/// </remarks>
public sealed class RedisSovereignAuditSink : IChainedSovereignAuditSink
{
    /// <summary>
    /// Store the event, advance the head, index it — or refuse, having changed
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <c>KEYS[3]</c> holds the head. An empty expectation means "the chain is
    /// empty", which is a claim like any other: two replicas starting at once
    /// both see nothing, and exactly one of them wins the first entry.
    /// </remarks>
    private const string AppendScript = @"
        local eventKey = KEYS[1]
        local indexKey = KEYS[2]
        local chainKey = KEYS[3]
        local payload = ARGV[1]
        local eventId = ARGV[2]
        local timestamp = ARGV[3]
        local expectedHead = ARGV[4]
        local newHead = ARGV[5]

        if redis.call('EXISTS', eventKey) == 1 then
            return -1
        end

        local head = redis.call('GET', chainKey)
        if not head then head = '' end
        if head ~= expectedHead then
            return 0
        end

        redis.call('SET', eventKey, payload)
        redis.call('ZADD', indexKey, timestamp, eventId)
        redis.call('SET', chainKey, newHead)
        return 1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisSovereignAuditSink> _logger;
    private readonly string _prefix;

    /// <summary>Takes the shared connection and the prefix the chain lives under.</summary>
    /// <param name="connection">The shared multiplexer.</param>
    /// <param name="logger">Where a refusal is recorded.</param>
    /// <param name="keyPrefix">
    /// Separates one system's chain from another's on a shared server. Two
    /// deployments that should share a chain share this; two that should not,
    /// must not — a chain is only meaningful for the writers inside it.
    /// </param>
    public RedisSovereignAuditSink(
        IConnectionMultiplexer connection,
        ILogger<RedisSovereignAuditSink> logger,
        string keyPrefix = "noelia")
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        _connection = connection;
        _logger = logger;
        _prefix = keyPrefix.TrimEnd(':');
    }

    private string ChainKey => $"{_prefix}:audit:chain";

    private string IndexKey => $"{_prefix}:audit:index";

    private string EventKey(string id) => $"{_prefix}:audit:event:{id}";

    /// <inheritdoc />
    public async Task<string?> HeadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var head = await _connection.GetDatabase().StringGetAsync(ChainKey);

        return head.IsNullOrEmpty ? null : head.ToString();
    }

    /// <inheritdoc />
    public async Task<bool> TryWriteAsync<T>(
        AuditEvent<T> auditEvent,
        string? expectedHead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var result = (int)await _connection.GetDatabase().ScriptEvaluateAsync(
            AppendScript,
            [EventKey(auditEvent.Id), IndexKey, ChainKey],
            [
                JsonSerializer.Serialize(auditEvent, Json),
                auditEvent.Id,
                auditEvent.Timestamp.ToUnixTimeMilliseconds(),
                expectedHead ?? string.Empty,
                auditEvent.Hash
            ]);

        return result switch
        {
            1 => true,
            0 => false,

            // The id already exists. Writing it again under a different
            // predecessor would put two versions of one event in the chain, so
            // this refuses and lets the caller build a new one.
            -1 => Duplicate(auditEvent.Id),
            _ => throw new InvalidOperationException(
                $"The audit chain script returned {result}, which this version does not know.")
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// The unchained path, for a caller that reached this sink through
    /// <see cref="ISovereignAuditSink"/> without the chain. It stores the event
    /// and leaves the head alone — which is right, because a head advanced
    /// outside the compare-and-set is a head nobody can trust.
    /// </remarks>
    public async Task WriteAsync<T>(
        AuditEvent<T> auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var database = _connection.GetDatabase();

        await database.StringSetAsync(
            EventKey(auditEvent.Id), JsonSerializer.Serialize(auditEvent, Json));

        await database.SortedSetAddAsync(
            IndexKey, auditEvent.Id, auditEvent.Timestamp.ToUnixTimeMilliseconds());
    }

    private bool Duplicate(string id)
    {
        _logger.LogWarning("Audit event {AuditEventId} is already in the chain", id);
        return false;
    }
}
