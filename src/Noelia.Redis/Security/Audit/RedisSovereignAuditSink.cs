using System.Runtime.CompilerServices;
using System.Globalization;
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
public sealed class RedisSovereignAuditSink : IChainedSovereignAuditSink, IStableAuditSnapshotReader
{
    /// <summary>How many ids to take out of the index at a time.</summary>
    private const int ReadPageSize = 500;

    // Redis executes this script without interleaving a writer. The hard limits
    // prevent an audit read from monopolising the server or its memory. Larger
    // logs retain the streaming contract without a stable-snapshot claim.
    //
    // Not Redis-Cluster-compatible, and knowingly so. The event keys are built
    // inside the script (ARGV[1] .. id) from ids read out of the index, so they
    // are not declared in KEYS[]. Cluster requires every key a script touches to
    // be declared up front and to hash to one slot; an undeclared key is either
    // rejected or, worse, read from a node that does not own it. Declaring them
    // is impossible here: the ids are only known after reading the index, which
    // is the very thing that has to happen atomically with reading the payloads.
    // On a cluster the script therefore cannot give the stable snapshot, and the
    // README says so; a deployment on one Redis primary (or Sentinel) is the
    // supported shape. Behaviour is deliberately unchanged.
    private const string SnapshotScript = @"
        if redis.call('ZCARD', KEYS[1]) > tonumber(ARGV[2]) then return {0} end
        local ids = redis.call('ZRANGE', KEYS[1], 0, -1, 'WITHSCORES')
        local result = {1, redis.call('GET', KEYS[2]) or ''}
        local bytes = 0
        for i = 1, #ids, 2 do
            local payload = redis.call('GET', ARGV[1] .. ids[i])
            if not payload then return {-1, (i - 1) / 2} end
            bytes = bytes + string.len(payload)
            if bytes > tonumber(ARGV[3]) then return {0} end
            table.insert(result, ids[i])
            table.insert(result, ids[i + 1])
            table.insert(result, payload)
        end
        return result";

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
        local expectedHead = ARGV[3]
        local newHead = ARGV[4]

        if redis.call('EXISTS', eventKey) == 1 then
            return -1
        end

        local head = redis.call('GET', chainKey)
        if not head then head = '' end
        if head ~= expectedHead then
            return 0
        end

        redis.call('SET', eventKey, payload)
        redis.call('ZADD', indexKey, NextSequence(indexKey), eventId)
        redis.call('SET', chainKey, newHead)
        return 1";

    /// <summary>
    /// The index score, and why it is not the timestamp.
    /// </summary>
    /// <remarks>
    /// <para>The order of a chain is the order the entries were appended, which
    /// the compare-and-set decides — not the order of the clocks that stamped
    /// them. Two replicas can append inside the same millisecond, and indexing
    /// by timestamp would hand a verifier those two entries in whichever order
    /// the store happened to break the tie. It would then report a broken link
    /// in a chain nobody had touched, which is worse than not checking at
    /// all.</para>
    ///
    /// <para>It continues from the highest score present rather than from the
    /// count, so a chain indexed by timestamps before 6.0.0 keeps its order and
    /// needs no migration: the next entry simply lands above the largest
    /// timestamp already there, and every entry after it above that.</para>
    /// </remarks>
    private const string NextSequenceFunction = @"
        local function NextSequence(indexKey)
            local last = redis.call('ZRANGE', indexKey, -1, -1, 'WITHSCORES')
            if last[2] then
                return tonumber(last[2]) + 1
            end
            return 0
        end
        ";

    /// <summary>The unchained store, kept atomic for the same ordering reason.</summary>
    private const string WriteScript = @"
        local eventKey = KEYS[1]
        local indexKey = KEYS[2]
        local payload = ARGV[1]
        local eventId = ARGV[2]

        redis.call('SET', eventKey, payload)
        redis.call('ZADD', indexKey, NextSequence(indexKey), eventId)
        return 1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisSovereignAuditSink> _logger;
    private readonly string _prefix;
    private readonly int _snapshotMaxEntries;
    private readonly int _snapshotMaxBytes;

    /// <summary>Takes the shared connection and the prefix the chain lives under.</summary>
    /// <param name="connection">The shared multiplexer.</param>
    /// <param name="logger">Where a refusal is recorded.</param>
    /// <param name="keyPrefix">
    /// Separates one system's chain from another's on a shared server. Two
    /// deployments that should share a chain share this; two that should not,
    /// must not — a chain is only meaningful for the writers inside it.
    /// </param>
    /// <param name="snapshotMaxEntries">Maximum entries copied in one atomic snapshot.</param>
    /// <param name="snapshotMaxBytes">Maximum total payload bytes copied in one atomic snapshot.</param>
    public RedisSovereignAuditSink(
        IConnectionMultiplexer connection,
        ILogger<RedisSovereignAuditSink> logger,
        string keyPrefix = "noelia",
        int snapshotMaxEntries = 4096,
        int snapshotMaxBytes = 4 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotMaxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotMaxBytes, 1);

        _connection = connection;
        _logger = logger;
        _prefix = keyPrefix.TrimEnd(':');
        _snapshotMaxEntries = snapshotMaxEntries;
        _snapshotMaxBytes = snapshotMaxBytes;
    }

    private string ChainKey => $"{_prefix}:audit:chain";

    private string IndexKey => $"{_prefix}:audit:index";

    private string EventKey(string id) => $"{_prefix}:audit:event:{id}";

    /// <inheritdoc />
    public async Task<AuditReadSnapshot?> TryCaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _connection.GetDatabase().ScriptEvaluateAsync(SnapshotScript,
            [IndexKey, ChainKey],
            [$"{_prefix}:audit:event:", _snapshotMaxEntries, _snapshotMaxBytes]);
        cancellationToken.ThrowIfCancellationRequested();
        var values = (RedisResult[])raw!;
        var status = (int)values[0];
        if (status == 0) return null;
        if (status == -1)
            throw InvalidEntry((long)values[1], "payload is missing");
        if (status != 1 || (values.Length - 2) % 3 != 0)
            throw new InvalidDataException("The audit snapshot had an unsupported shape.");

        var entries = new List<StoredAuditEntry>((values.Length - 2) / 3);
        var sequenceVerified = true;
        double previousScore = -1;
        for (var position = 2; position < values.Length; position += 3)
        {
            var index = (position - 2) / 3;
            var id = (string)values[position]!;
            var scoreText = (string)values[position + 1]!;
            if (!double.TryParse(scoreText, NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
                || !double.IsFinite(score))
                throw InvalidEntry(index, "index sequence is invalid");
            sequenceVerified &= score == previousScore + 1;
            previousScore = score;

            StoredAuditEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<StoredAuditEntry>((string)values[position + 2]!, Json);
            }
            catch (JsonException)
            {
                throw InvalidEntry(index, "payload is not a valid audit entry");
            }
            if (entry is null || !string.Equals(entry.Id, id, StringComparison.Ordinal))
                throw InvalidEntry(index, "payload identity does not match its index entry");
            entries.Add(entry);
        }

        var head = (string)values[1]!;
        return new AuditReadSnapshot(_prefix, entries, head.Length == 0 ? null : head, sequenceVerified);
    }

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
            NextSequenceFunction + AppendScript,
            [EventKey(auditEvent.Id), IndexKey, ChainKey],
            [
                JsonSerializer.Serialize(auditEvent, Json),
                auditEvent.Id,
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

        await _connection.GetDatabase().ScriptEvaluateAsync(
            NextSequenceFunction + WriteScript,
            [EventKey(auditEvent.Id), IndexKey],
            [JsonSerializer.Serialize(auditEvent, Json), auditEvent.Id]);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">
    /// An indexed payload is missing, malformed or belongs to another identity.
    /// Entries already yielded do not make a failed read a complete result.
    /// </exception>
    public async IAsyncEnumerable<StoredAuditEntry> ReadAsync(
        DateTimeOffset? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var database = _connection.GetDatabase();
        var started = since is null;

        for (var page = 0L; ; page += ReadPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = await database.SortedSetRangeByRankAsync(
                IndexKey, page, page + ReadPageSize - 1);

            if (ids.Length == 0)
            {
                yield break;
            }

            var payloads = await database.StringGetAsync(
                [.. ids.Select(id => (RedisKey)EventKey(id.ToString()))]);

            for (var offset = 0; offset < payloads.Length; offset++)
            {
                var payload = payloads[offset];
                var position = page + offset;
                if (payload.IsNullOrEmpty)
                {
                    // A missing first/last entry has no adjacent pair that
                    // necessarily reveals it. Never turn a failed read into a
                    // shorter apparently valid chain, including an empty one.
                    throw InvalidEntry(position, "payload is missing or empty");
                }

                StoredAuditEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<StoredAuditEntry>((string)payload!, Json);
                }
                catch (JsonException)
                {
                    // JSON diagnostics can include attacker-controlled paths.
                    // Do not expose raw audit content through an inner error.
                    throw InvalidEntry(position, "payload is not a valid audit entry");
                }

                if (entry is null)
                {
                    throw InvalidEntry(position, "payload is null");
                }

                if (!string.Equals(entry.Id, ids[offset].ToString(), StringComparison.Ordinal))
                {
                    throw InvalidEntry(position, "payload identity does not match its index entry");
                }

                // A contiguous suffix, not a filter. Dropping individual
                // entries by timestamp would leave gaps wherever two replicas'
                // clocks disagree, and every gap reads as a broken link.
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

            if (ids.Length < ReadPageSize)
            {
                yield break;
            }
        }
    }

    private static InvalidDataException InvalidEntry(long position, string reason) =>
        new($"Cannot read audit index position {position}: {reason}.");

    private bool Duplicate(string id)
    {
        _logger.LogWarning("Audit event {AuditEventId} is already in the chain", id);
        return false;
    }
}
