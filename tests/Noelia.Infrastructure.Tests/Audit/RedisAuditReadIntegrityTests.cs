using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Audit;
using Noelia.Infrastructure.Audit;
using Noelia.Infrastructure.Tests.Security;
using Noelia.Redis.Security.Audit;

namespace Noelia.Infrastructure.Tests.Audit;

/// <summary>Indexed entries must not disappear silently from a read-back.</summary>
[Trait("Category", "Integration")]
public sealed class RedisAuditReadIntegrityTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    private const string Canary = "synthetic-audit-payload-must-not-appear-in-errors";

    [Theory]
    [InlineData("prefix", "Broken")]
    [InlineData("inner", "Broken")]
    [InlineData("suffix", "ReadFailed")]
    [InlineData("all", "Unknown")]
    [InlineData("payload", "Broken")]
    [InlineData("recomputed", "Consistent")]
    public async Task An_independent_signed_checkpoint_rejects_truncation_and_recomputed_chains(
        string mutation, string consistency)
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Audit read-integrity tests require a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"anchored-{Guid.NewGuid():N}";
        var sink = new RedisSovereignAuditSink(connection,
            NullLogger<RedisSovereignAuditSink>.Instance, prefix);
        var trail = new AuditTrailService(sink, NullLogger<AuditTrailService>.Instance);
        for (var i = 0; i < 3; i++)
            await trail.RecordAsync<object>("synthetic", "operator", "Read", $"resource-{i}");
        var snapshot = (await sink.TryCaptureSnapshotAsync())!;
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = AuditCheckpointSignature.Sign(snapshot, signer.ExportECPrivateKeyPem(), DateTimeOffset.UtcNow);
        var path = Path.Combine(Path.GetTempPath(), $"noelia-checkpoint-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));
            var source = new SignedFileAuditCheckpointSource(path, signer.ExportSubjectPublicKeyInfoPem());
            var database = connection.GetDatabase();
            var baseline = new ServiceCollection().AddSingleton<ISovereignAuditSink>(sink)
                .AddSingleton<ITrustedAuditCheckpointSource>(source).BuildServiceProvider();
            await using (baseline)
            {
                var intact = await new AuditChainVerifier(baseline).VerifyAsync();
                intact.Evidence!.Completeness.Should().Be(AuditChainCompleteness.Complete);

                switch (mutation)
                {
                    case "prefix":
                        await database.SortedSetRemoveRangeByRankAsync($"{prefix}:audit:index", 0, 0);
                        break;
                    case "inner":
                        await database.SortedSetRemoveRangeByRankAsync($"{prefix}:audit:index", 1, 1);
                        break;
                    case "suffix":
                        await database.SortedSetRemoveRangeByRankAsync($"{prefix}:audit:index", -1, -1);
                        break;
                    case "all":
                        await database.KeyDeleteAsync($"{prefix}:audit:index");
                        await database.KeyDeleteAsync($"{prefix}:audit:chain");
                        break;
                    case "payload":
                        var altered = snapshot.Entries[1] with { Resource = "altered" };
                        await database.StringSetAsync($"{prefix}:audit:event:{altered.Id}",
                            JsonSerializer.Serialize(altered, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        break;
                    case "recomputed":
                        string? previous = null;
                        foreach (var entry in snapshot.Entries)
                        {
                            var rewritten = (entry.AsEvent() with
                            { Resource = "rewritten", PreviousHash = previous }).WithComputedHash();
                            await database.StringSetAsync($"{prefix}:audit:event:{entry.Id}",
                                JsonSerializer.Serialize(rewritten, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                            previous = rewritten.Hash;
                        }
                        await database.StringSetAsync($"{prefix}:audit:chain", previous);
                        break;
                }

                var result = await new AuditChainVerifier(baseline).VerifyAsync();
                result.Evidence!.Consistency.ToString().Should().Be(consistency);
                result.Evidence.Completeness.Should().Be(AuditChainCompleteness.Unknown);
                result.Evidence.HasIndependentCheckpoint.Should().BeFalse();
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_captured_redis_snapshot_survives_a_later_change_to_the_mutable_store()
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Audit read-integrity tests require a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"snapshot-copy-{Guid.NewGuid():N}";
        var sink = new RedisSovereignAuditSink(connection,
            NullLogger<RedisSovereignAuditSink>.Instance, prefix);
        var trail = new AuditTrailService(sink, NullLogger<AuditTrailService>.Instance);
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "first");
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "second");

        var snapshot = await sink.TryCaptureSnapshotAsync();
        snapshot.Should().NotBeNull();
        snapshot!.Entries.Should().HaveCount(2);
        snapshot.SequenceVerified.Should().BeTrue();
        snapshot.StoredHead.Should().Be(snapshot.Entries[^1].Hash);

        await connection.GetDatabase().KeyDeleteAsync($"{prefix}:audit:index");
        snapshot.Entries.Should().HaveCount(2);
        snapshot.Entries.All(entry => entry.VerifyHash()).Should().BeTrue();
    }

    [Fact]
    public async Task An_oversize_chain_falls_back_without_a_snapshot_claim()
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Audit read-integrity tests require a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"snapshot-bound-{Guid.NewGuid():N}";
        var sink = new RedisSovereignAuditSink(connection,
            NullLogger<RedisSovereignAuditSink>.Instance, prefix, snapshotMaxEntries: 1);
        var trail = new AuditTrailService(sink, NullLogger<AuditTrailService>.Instance);
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "first");
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "second");
        (await sink.TryCaptureSnapshotAsync()).Should().BeNull();

        await using var services = new ServiceCollection().AddSingleton<ISovereignAuditSink>(sink).BuildServiceProvider();
        var verification = await new AuditChainVerifier(services).VerifyAsync();
        verification.Evidence!.Consistency.Should().Be(AuditChainConsistency.Consistent);
        verification.Evidence.HasStableSnapshot.Should().BeFalse();
        verification.Evidence.Completeness.Should().Be(AuditChainCompleteness.Unknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_removed_index_suffix_or_entire_index_cannot_pass_with_the_original_head(bool all)
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Audit read-integrity tests require a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"head-boundary-{Guid.NewGuid():N}";
        var sink = new RedisSovereignAuditSink(connection,
            NullLogger<RedisSovereignAuditSink>.Instance, prefix);
        var trail = new AuditTrailService(sink, NullLogger<AuditTrailService>.Instance);
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "first");
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "second");
        var database = connection.GetDatabase();
        if (all)
            await database.KeyDeleteAsync($"{prefix}:audit:index");
        else
            await database.SortedSetRemoveRangeByRankAsync($"{prefix}:audit:index", -1, -1);

        await using var services = new ServiceCollection().AddSingleton<ISovereignAuditSink>(sink).BuildServiceProvider();
        var verification = await new AuditChainVerifier(services).VerifyAsync();
        verification.Evidence!.Consistency.Should().Be(AuditChainConsistency.ReadFailed);
        verification.Evidence.Completeness.Should().Be(AuditChainCompleteness.Unknown);
        verification.Evidence.HasIndependentCheckpoint.Should().BeFalse();
    }

    [Fact]
    public async Task Valid_pages_and_a_time_suffix_are_read_but_a_missing_last_page_entry_is_not_skipped()
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Audit read-integrity tests require a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"read-pages-{Guid.NewGuid():N}";
        var sink = new RedisSovereignAuditSink(connection,
            NullLogger<RedisSovereignAuditSink>.Instance, prefix);
        var start = DateTimeOffset.UtcNow;
        var expected = new List<string>();
        string? head = null;
        for (var index = 0; index < 501; index++)
        {
            var entry = new AuditEvent<object>
            {
                ActorId = "synthetic-operator", Capacity = "operator", Action = "Read",
                Resource = "synthetic-resource", Timestamp = start.AddMilliseconds(index), PreviousHash = head
            }.WithComputedHash();
            (await sink.TryWriteAsync(entry, head)).Should().BeTrue();
            expected.Add(entry.Id);
            head = entry.Hash;
        }

        var complete = new List<string>();
        await foreach (var entry in sink.ReadAsync()) complete.Add(entry.Id);
        complete.Should().Equal(expected);
        var suffix = new List<string>();
        await foreach (var entry in sink.ReadAsync(start.AddMilliseconds(250))) suffix.Add(entry.Id);
        suffix.Should().Equal(expected.Skip(250));

        await connection.GetDatabase().KeyDeleteAsync($"{prefix}:audit:event:{expected[^1]}");
        var read = async () =>
        {
            await foreach (var entry in sink.ReadAsync()) _ = entry;
        };
        await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*index position 500:*");
    }

    [Theory]
    [InlineData("missing", 0)]
    [InlineData("missing", 1)]
    [InlineData("missing", 2)]
    [InlineData("all-missing", 0)]
    [InlineData("empty", 0)]
    [InlineData("null", 0)]
    [InlineData("null", 2)]
    [InlineData("malformed", 1)]
    [InlineData("wrong-id", 0)]
    [InlineData("wrong-id", 2)]
    public async Task An_indexed_unreadable_or_mismatched_entry_fails_explicitly(string corruption, int position)
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Audit read-integrity tests require a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"read-integrity-{Guid.NewGuid():N}";
        var sink = new RedisSovereignAuditSink(connection,
            NullLogger<RedisSovereignAuditSink>.Instance, prefix);
        var entries = new List<AuditEvent<object>>();
        string? head = null;
        for (var index = 0; index < 3; index++)
        {
            var entry = new AuditEvent<object>
            {
                ActorId = "synthetic-operator", Capacity = "operator",
                Action = "Read", Resource = "synthetic-resource", PreviousHash = head
            }.WithComputedHash();
            (await sink.TryWriteAsync(entry, head)).Should().BeTrue();
            entries.Add(entry);
            head = entry.Hash;
        }

        var database = connection.GetDatabase();
        var key = $"{prefix}:audit:event:{entries[position].Id}";
        switch (corruption)
        {
            case "missing":
                await database.KeyDeleteAsync(key);
                break;
            case "all-missing":
                foreach (var entry in entries)
                    await database.KeyDeleteAsync($"{prefix}:audit:event:{entry.Id}");
                break;
            case "empty":
                await database.StringSetAsync(key, "");
                break;
            case "null":
                await database.StringSetAsync(key, "null");
                break;
            case "malformed":
                await database.StringSetAsync(key, $"{{\"{Canary}\":");
                break;
            case "wrong-id":
                await database.StringSetAsync(key, JsonSerializer.Serialize(entries[(position + 1) % 3],
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                break;
        }

        var read = async () =>
        {
            await foreach (var entry in sink.ReadAsync()) _ = entry;
        };
        var error = await read.Should().ThrowAsync<InvalidDataException>();
        error.Which.Message.Should().Contain("index position").And.NotContain(Canary);
        error.Which.InnerException.Should().BeNull("raw payload diagnostics must not escape via an inner exception");

        await using var services = new ServiceCollection().AddSingleton<ISovereignAuditSink>(sink).BuildServiceProvider();
        var verification = await new AuditChainVerifier(services).VerifyAsync();
        verification.IsIntact.Should().BeFalse();
        verification.FirstBreak.Should().BeNull("a read failure is not a proven hash/link break");
        verification.Evidence!.Consistency.Should().Be(AuditChainConsistency.ReadFailed);
        verification.Evidence.Completeness.Should().Be(AuditChainCompleteness.Unknown);
        JsonSerializer.Serialize(verification).Should().NotContain(Canary);
    }
}
