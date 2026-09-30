using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Audit;
using Noelia.Infrastructure.Audit;

namespace Noelia.Infrastructure.Tests.Audit;

/// <summary>
/// Reading the chain back and recomputing it.
/// </summary>
/// <remarks>
/// Until 6.0.0 the trail was hash-chained and nothing could check it. These
/// tests are the difference between a promise and its redemption: an edited
/// entry, a removed entry and a reordered entry each have to be caught, and
/// each has to be reported as the thing it is.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AuditChainVerifierTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_full_walk_rejects_a_missing_genesis_prefix(int removed)
    {
        var entries = (await ChainOf(4)).Entries.Skip(removed).ToArray();
        var result = await Verify(new ProbeSink(entries));
        result.IsIntact.Should().BeFalse();
        result.FirstBreak!.Kind.Should().Be(AuditChainBreakKind.LinkBroken);
        result.FirstBreak.EntryId.Should().Be(entries[0].Id);
        AssertEvidence(result, "Broken", "Unknown", "AvailableStore");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task An_unanchored_walk_cannot_prove_completeness_even_when_hashes_hold(int retained)
    {
        var entries = (await ChainOf(4)).Entries.Take(retained).ToArray();
        var result = await Verify(new ProbeSink(entries));
        // The legacy flag is a hash/link result, not a completeness verdict.
        result.IsIntact.Should().BeTrue();
        AssertEvidence(result, retained == 0 ? "Unknown" : "Consistent", "Unknown", "AvailableStore");
    }

    [Fact]
    public async Task Recomputing_every_hash_is_not_an_independent_integrity_or_completeness_proof()
    {
        var entries = (await ChainOf(4)).Entries.ToArray();
        string? previous = null;
        for (var index = 0; index < entries.Length; index++)
        {
            var rewritten = (entries[index] with { Resource = "rewritten", PreviousHash = previous })
                .AsEvent().WithComputedHash();
            entries[index] = entries[index] with { Resource = "rewritten", PreviousHash = previous, Hash = rewritten.Hash };
            previous = rewritten.Hash;
        }
        var result = await Verify(new ProbeSink(entries));
        result.IsIntact.Should().BeTrue();
        AssertEvidence(result, "Consistent", "Unknown", "AvailableStore");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task A_shared_store_head_exposes_a_missing_suffix_or_whole_index(int retained)
    {
        var entries = (await ChainOf(4)).Entries;
        var result = await Verify(new HeadProbeSink(entries.Take(retained).ToArray(),
            entries[^1].Hash, entries[^1].Hash));
        result.Evidence!.Consistency.Should().Be(AuditChainConsistency.ReadFailed);
        result.Evidence.Completeness.Should().Be(AuditChainCompleteness.Unknown);
        result.Evidence.HasIndependentCheckpoint.Should().BeFalse();
        result.Note.Should().Contain("head");
    }

    [Fact]
    public async Task A_concurrent_append_does_not_yield_a_positive_verdict_from_a_moving_head()
    {
        var entries = (await ChainOf(4)).Entries;
        var result = await Verify(new HeadProbeSink(entries.Take(3).ToArray(),
            entries[2].Hash, entries[3].Hash));
        result.Evidence!.Consistency.Should().Be(AuditChainConsistency.ReadFailed);
        result.FirstBreak.Should().BeNull("a moving boundary is not a proven hash break");
    }

    [Fact]
    public async Task A_stable_shared_head_allows_a_consistency_result_without_proving_completeness()
    {
        var entries = (await ChainOf(4)).Entries;
        var result = await Verify(new HeadProbeSink(entries, entries[^1].Hash, entries[^1].Hash));
        AssertEvidence(result, "Consistent", "Unknown", "AvailableStore");
    }

    [Fact]
    public async Task An_explicit_time_suffix_reports_partial_scope_not_a_missing_genesis()
    {
        var sink = await ChainOf(4);
        var result = await Verify(sink, sink.Entries[2].Timestamp);
        result.IsIntact.Should().BeTrue();
        result.IsPartial.Should().BeTrue();
        AssertEvidence(result, "Consistent", "Partial", "TimeSuffix");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_fault_is_neither_consistency_nor_a_hash_break_and_does_not_leak(bool afterEntries)
    {
        const string canary = "synthetic-storage-exception-secret";
        var entries = (await ChainOf(2)).Entries;
        var result = await Verify(new FailingSink(afterEntries ? entries : [], new InvalidDataException(canary)));
        result.IsSupported.Should().BeTrue();
        result.IsIntact.Should().BeFalse();
        result.FirstBreak.Should().BeNull();
        result.EntriesVerified.Should().Be(afterEntries ? 2 : 0);
        result.Note.Should().NotContain(canary);
        AssertEvidence(result, "ReadFailed", "Unknown", "AvailableStore");
    }

    [Fact]
    public async Task Cancellation_is_propagated_not_reported_as_a_storage_fault()
    {
        var invoke = () => Verify(new FailingSink([], new OperationCanceledException()));
        await invoke.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Unsupported_verification_has_no_consistency_or_completeness_claim()
    {
        AssertEvidence(AuditChainVerification.Unsupported("No reader"), "Unknown", "Unknown", "Unknown");
    }

    private static void AssertEvidence(AuditChainVerification result, string consistency, string completeness, string scope)
    {
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var evidence = document.RootElement.GetProperty("evidence");
        evidence.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        evidence.GetProperty("consistency").GetString().Should().Be(consistency);
        evidence.GetProperty("completeness").GetString().Should().Be(completeness);
        evidence.GetProperty("scope").GetString().Should().Be(scope);
        evidence.GetProperty("hasStableSnapshot").GetBoolean().Should().BeFalse();
        evidence.GetProperty("hasIndependentCheckpoint").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task An_untouched_chain_verifies()
    {
        var sink = await ChainOf(4);

        var result = await Verify(sink);

        result.IsSupported.Should().BeTrue();
        result.IsIntact.Should().BeTrue();
        result.EntriesVerified.Should().Be(4);
        result.FirstBreak.Should().BeNull();
        result.Head.Should().NotBeNullOrWhiteSpace();
        result.IsPartial.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_chain_verifies_and_says_it_saw_nothing()
    {
        var result = await Verify(new ProbeSink([]));

        result.IsIntact.Should().BeTrue();
        result.EntriesVerified.Should().Be(0);
        result.Head.Should().BeNull();
    }

    /// <summary>
    /// The edit a hash chain exists to catch: a record rewritten in place.
    /// </summary>
    [Fact]
    public async Task A_rewritten_entry_is_caught_and_named()
    {
        var entries = (await ChainOf(4)).Entries.ToList();
        entries[2] = entries[2] with { Resource = "something-else" };

        var result = await Verify(new ProbeSink(entries));

        result.IsIntact.Should().BeFalse();
        result.FirstBreak.Should().NotBeNull();
        result.FirstBreak!.Kind.Should().Be(AuditChainBreakKind.ContentsChanged);
        result.FirstBreak.EntryId.Should().Be(entries[2].Id);
        result.EntriesVerified.Should().Be(3, "the walk stops at the first break");
    }

    /// <summary>
    /// The edit that leaves every record intact and only the sequence a lie.
    /// </summary>
    /// <remarks>
    /// Removing an entry is how someone deletes the record of what they did
    /// while leaving everything around it perfectly well-formed. Only the links
    /// notice.
    /// </remarks>
    [Fact]
    public async Task A_removed_entry_is_caught_as_a_broken_link()
    {
        var entries = (await ChainOf(4)).Entries.ToList();
        entries.RemoveAt(1);

        var result = await Verify(new ProbeSink(entries));

        result.IsIntact.Should().BeFalse();
        result.FirstBreak!.Kind.Should().Be(AuditChainBreakKind.LinkBroken);
        result.FirstBreak.Detail.Should().Contain("Expected to follow");
    }

    [Fact]
    public async Task A_reordered_chain_is_caught_as_a_broken_link()
    {
        var entries = (await ChainOf(4)).Entries.ToList();
        (entries[1], entries[2]) = (entries[2], entries[1]);

        var result = await Verify(new ProbeSink(entries));

        result.IsIntact.Should().BeFalse();
        result.FirstBreak!.Kind.Should().Be(AuditChainBreakKind.LinkBroken);
    }

    /// <summary>
    /// A break has to be locatable, and a location must not be a second leak.
    /// </summary>
    [Fact]
    public async Task A_break_names_where_without_printing_whole_hashes()
    {
        var entries = (await ChainOf(3)).Entries.ToList();
        var full = entries[1].Hash;
        entries.RemoveAt(1);

        var result = await Verify(new ProbeSink(entries));

        result.FirstBreak!.Detail.Should().NotContain(full,
            "the full hashes live in the store; this text lands on a screen");
        result.FirstBreak.Detail.Should().Contain(full[..12]);
    }

    /// <summary>
    /// Not verified and verified-and-fine must never look alike.
    /// </summary>
    [Fact]
    public async Task A_sink_that_cannot_be_read_back_is_unsupported_and_not_intact()
    {
        var services = new ServiceCollection()
            .AddSingleton<ISovereignAuditSink, WriteOnlySink>()
            .BuildServiceProvider();

        var result = await new AuditChainVerifier(services).VerifyAsync();

        result.IsSupported.Should().BeFalse();
        result.IsIntact.Should().BeFalse(
            "\"we never checked\" must not read as \"we checked and it was fine\"");
        result.Note.Should().Contain("AddRedisSovereignAudit",
            "an operator told it cannot be done here should be told what would make it possible");
    }

    /// <summary>
    /// A partial walk is a smaller claim, and has to say so.
    /// </summary>
    [Fact]
    public async Task Verifying_from_a_point_says_that_it_is_partial()
    {
        var sink = await ChainOf(4);
        var from = sink.Entries[2].Timestamp;

        var result = await Verify(sink, from);

        result.IsIntact.Should().BeTrue();
        result.IsPartial.Should().BeTrue(
            "everything before the starting point was taken on trust");
        result.EntriesVerified.Should().Be(2);
    }

    /// <summary>
    /// The in-memory sink is a real implementation, not only a test double.
    /// </summary>
    /// <remarks>
    /// It is what Development runs, so it is where a developer first sees the
    /// verifier work. A read-back only the Redis sink supported would make the
    /// feature invisible in the one stage where people try things out.
    /// </remarks>
    [Fact]
    public async Task The_in_memory_sink_can_be_read_back_and_verified()
    {
        var sink = new InMemorySovereignAuditSink();
        var trail = new AuditTrailService(sink, NullLogger<AuditTrailService>.Instance);

        await trail.RecordAsync<object>("operator", "operator", "Viewed", "Noelia.Dashboard");
        await trail.RecordAsync<object>("operator", "operator", "Viewed", "Noelia.Dashboard");

        var services = new ServiceCollection()
            .AddSingleton<ISovereignAuditSink>(sink)
            .BuildServiceProvider();

        var result = await new AuditChainVerifier(services).VerifyAsync();

        result.IsSupported.Should().BeTrue();
        result.IsIntact.Should().BeTrue();
        result.EntriesVerified.Should().Be(2);
        result.Evidence!.SchemaVersion.Should().Be(2);
        result.Evidence.HasStableSnapshot.Should().BeTrue();
        result.Evidence.SequenceVerified.Should().BeTrue();
        result.Evidence.SnapshotEntryCount.Should().Be(2);
        result.Evidence.Completeness.Should().Be(AuditChainCompleteness.Unknown,
            "an atomic copy is not an independent completeness checkpoint");
    }

    /// <summary>
    /// The finding travels by name, not by ordinal.
    /// </summary>
    /// <remarks>
    /// A control plane stores these. As a number, reordering the enum one day
    /// would turn every stored "contents changed" into "link broken" without a
    /// single file changing.
    /// </remarks>
    [Fact]
    public async Task A_break_serialises_its_kind_by_name()
    {
        var entries = (await ChainOf(3)).Entries.ToList();
        entries[1] = entries[1] with { Resource = "something-else" };

        var result = await Verify(new ProbeSink(entries));
        var json = System.Text.Json.JsonSerializer.Serialize(
            result, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));

        json.Should().Contain("\"kind\":\"ContentsChanged\"");
    }

    private static async Task<AuditChainVerification> Verify(
        IReadableSovereignAuditSink sink,
        DateTimeOffset? since = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISovereignAuditSink>(sink)
            .BuildServiceProvider();

        return await new AuditChainVerifier(services).VerifyAsync(since);
    }

    /// <summary>
    /// Builds a genuinely chained run through the real hashing, so the tests
    /// verify what production writes rather than what a fixture invented.
    /// </summary>
    private static async Task<ProbeSink> ChainOf(int count)
    {
        var sink = new InMemorySovereignAuditSink();
        var trail = new AuditTrailService(sink, NullLogger<AuditTrailService>.Instance);

        for (var i = 0; i < count; i++)
        {
            await trail.RecordAsync<object>("operator", "operator", "Viewed", $"resource-{i}");
        }

        var entries = new List<StoredAuditEntry>();
        await foreach (var entry in sink.ReadAsync())
        {
            entries.Add(entry);
        }

        return new ProbeSink(entries);
    }

    /// <summary>Hands back exactly the entries it was given, in that order.</summary>
    private sealed class ProbeSink(IReadOnlyList<StoredAuditEntry> entries)
        : IReadableSovereignAuditSink
    {
        public IReadOnlyList<StoredAuditEntry> Entries { get; } = entries;

        public Task WriteAsync<T>(
            AuditEvent<T> auditEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<StoredAuditEntry> ReadAsync(
            DateTimeOffset? since = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var started = since is null;

            foreach (var entry in Entries)
            {
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

    private sealed class FailingSink(IReadOnlyList<StoredAuditEntry> entries, Exception failure) : IReadableSovereignAuditSink
    {
        public Task WriteAsync<T>(AuditEvent<T> auditEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<StoredAuditEntry> ReadAsync(DateTimeOffset? since = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries) yield return entry;
            await Task.CompletedTask;
            throw failure;
        }
    }

    private sealed class HeadProbeSink(IReadOnlyList<StoredAuditEntry> entries, string? before, string? after)
        : IReadableSovereignAuditSink, IChainedSovereignAuditSink
    {
        private int _headReads;
        public Task<string?> HeadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Increment(ref _headReads) == 1 ? before : after);
        public Task<bool> TryWriteAsync<T>(AuditEvent<T> auditEvent, string? expectedHead,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task WriteAsync<T>(AuditEvent<T> auditEvent,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<StoredAuditEntry> ReadAsync(DateTimeOffset? since = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries) yield return entry;
            await Task.CompletedTask;
        }
    }

    private sealed class WriteOnlySink : ISovereignAuditSink
    {
        public Task WriteAsync<T>(
            AuditEvent<T> auditEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
