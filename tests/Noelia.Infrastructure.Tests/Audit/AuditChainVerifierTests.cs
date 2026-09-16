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

    private sealed class WriteOnlySink : ISovereignAuditSink
    {
        public Task WriteAsync<T>(
            AuditEvent<T> auditEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
