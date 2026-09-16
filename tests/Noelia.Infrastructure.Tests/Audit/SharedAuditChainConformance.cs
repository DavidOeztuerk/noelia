using Noelia.Abstractions.Audit;

namespace Noelia.Infrastructure.Tests.Audit;

/// <summary>
/// What every shared audit chain has to guarantee, whatever it is built on.
/// </summary>
/// <remarks>
/// <para>A hash chain is only worth the property it enforces: that no entry can
/// be removed or reordered without the links showing it. With one writer that
/// property comes free. With several it has to be bought, and the price is that
/// storing an event and advancing the head are one operation — advance first
/// and a crash leaves a head pointing at nothing, store first and two writers
/// chain onto the same predecessor.</para>
///
/// <para>These live in a conformance suite rather than beside one
/// implementation because the promises are the port's, not Redis's. Anyone
/// writing a sink for a database, an event store or an append-only file
/// inherits this class and finds out whether they kept them.</para>
/// </remarks>
[Trait("Category", "Unit")]
public abstract class SharedAuditChainConformance
{
    /// <summary>A sink with an empty chain, for one test.</summary>
    protected abstract IChainedSovereignAuditSink CreateSink();

    [Fact]
    public async Task An_empty_chain_has_no_head()
    {
        var sink = CreateSink();

        (await sink.HeadAsync()).Should().BeNull(
            "null and the empty string are different claims, and a writer starting out "
            + "has to be able to tell 'nothing yet' from 'something I cannot read'");
    }

    [Fact]
    public async Task The_first_write_wins_against_an_empty_expectation()
    {
        var sink = CreateSink();
        var first = Event("first", previousHash: null);

        (await sink.TryWriteAsync(first, expectedHead: null)).Should().BeTrue();
        (await sink.HeadAsync()).Should().Be(first.Hash);
    }

    [Fact]
    public async Task A_writer_chaining_onto_the_current_head_wins()
    {
        var sink = CreateSink();
        var first = Event("first", null);
        await sink.TryWriteAsync(first, null);

        var second = Event("second", first.Hash);

        (await sink.TryWriteAsync(second, first.Hash)).Should().BeTrue();
        (await sink.HeadAsync()).Should().Be(second.Hash);
    }

    /// <summary>
    /// The one that matters: the second replica loses and is told so.
    /// </summary>
    /// <remarks>
    /// Both writers read the same head and build on it. Without the
    /// compare-and-set both would be stored, the head would end up at whichever
    /// wrote last, and the chain would contain two children of one predecessor
    /// — a break a verifier reports on a system nobody touched.
    /// </remarks>
    [Fact]
    public async Task Two_writers_on_one_head_produce_one_winner()
    {
        var sink = CreateSink();
        var root = Event("root", null);
        await sink.TryWriteAsync(root, null);

        var mine = Event("mine", root.Hash);
        var theirs = Event("theirs", root.Hash);

        var first = await sink.TryWriteAsync(mine, root.Hash);
        var second = await sink.TryWriteAsync(theirs, root.Hash);

        new[] { first, second }.Count(won => won).Should().Be(1,
            "exactly one of two writers chaining onto the same head may commit");
        (await sink.HeadAsync()).Should().Be(mine.Hash);
    }

    [Fact]
    public async Task A_stale_expectation_is_refused_and_changes_nothing()
    {
        var sink = CreateSink();
        var root = Event("root", null);
        await sink.TryWriteAsync(root, null);
        var second = Event("second", root.Hash);
        await sink.TryWriteAsync(second, root.Hash);

        var stale = Event("stale", root.Hash);

        (await sink.TryWriteAsync(stale, root.Hash)).Should().BeFalse();
        (await sink.HeadAsync()).Should().Be(second.Hash,
            "a refused write must leave the head where it was");
    }

    [Fact]
    public async Task An_expectation_of_nothing_on_a_started_chain_is_refused()
    {
        var sink = CreateSink();
        var root = Event("root", null);
        await sink.TryWriteAsync(root, null);

        (await sink.TryWriteAsync(Event("usurper", null), expectedHead: null))
            .Should().BeFalse("a chain that has begun cannot begin again");
    }

    [Fact]
    public async Task The_same_event_is_not_written_twice()
    {
        var sink = CreateSink();
        var root = Event("root", null);
        await sink.TryWriteAsync(root, null);

        (await sink.TryWriteAsync(root, root.Hash)).Should().BeFalse(
            "two versions of one event id in a chain is a contradiction a verifier "
            + "cannot resolve");
    }

    private static AuditEvent<object> Event(string action, string? previousHash) =>
        new AuditEvent<object>
        {
            ActorId = "conformance",
            Capacity = "operator",
            Action = action,
            Resource = "Probe",
            PreviousHash = previousHash
        }.WithComputedHash();
}
