using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noelia.Abstractions.Messaging;
using Noelia.Data.EntityFrameworkCore.Messaging;

namespace Noelia.Infrastructure.Tests.Messaging;

/// <summary>
/// The promise an outbox exists to keep: the intent and the change commit
/// together, or neither does.
/// </summary>
/// <remarks>
/// Against a real SQLite database rather than a double. The whole mechanism is
/// about transaction boundaries and conditional updates, and an in-memory
/// dictionary has neither — a double would agree with whatever the test
/// expected and prove nothing.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class OutboxTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-16T12:00:00Z"));

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        await using var context = NewContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Recording_alone_writes_nothing()
    {
        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);

        await outbox.RecordAsync(new JobFinished("job-1"));

        await using var other = NewContext();
        (await other.Set<NoeliaOutboxMessage>().CountAsync()).Should().Be(0,
            "an outbox that saved by itself would leave a row behind a rolled-back change "
            + "and announce something that never happened");
    }

    [Fact]
    public async Task The_intent_commits_with_the_change()
    {
        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);

        context.Jobs.Add(new Job { Id = "job-1", State = "finished" });
        await outbox.RecordAsync(new JobFinished("job-1"));
        await context.SaveChangesAsync();

        await using var other = NewContext();
        (await other.Jobs.CountAsync()).Should().Be(1);
        (await other.Set<NoeliaOutboxMessage>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_rolled_back_change_takes_the_intent_with_it()
    {
        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Jobs.Add(new Job { Id = "job-1", State = "finished" });
            await outbox.RecordAsync(new JobFinished("job-1"));
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var other = NewContext();
        (await other.Jobs.CountAsync()).Should().Be(0);
        (await other.Set<NoeliaOutboxMessage>().CountAsync()).Should().Be(0,
            "this is the gap the outbox exists to close");
    }

    [Fact]
    public async Task A_claimed_message_is_not_handed_out_again()
    {
        await Record(new JobFinished("job-1"));

        await using var first = NewContext();
        await using var second = NewContext();

        var mine = await new EntityFrameworkOutbox<OutboxProbeContext>(first, _time).ClaimAsync(10);
        var theirs = await new EntityFrameworkOutbox<OutboxProbeContext>(second, _time).ClaimAsync(10);

        mine.Should().HaveCount(1);
        theirs.Should().BeEmpty("two dispatchers must not both deliver one message");
    }

    [Fact]
    public async Task A_claim_that_was_never_released_expires()
    {
        await Record(new JobFinished("job-1"));

        await using var abandoned = NewContext();
        await new EntityFrameworkOutbox<OutboxProbeContext>(abandoned, _time).ClaimAsync(10);

        // The dispatcher that took it died here.
        _time.Advance(TimeSpan.FromMinutes(6));

        await using var next = NewContext();
        var recovered = await new EntityFrameworkOutbox<OutboxProbeContext>(next, _time).ClaimAsync(10);

        recovered.Should().HaveCount(1,
            "losing a dispatcher must not mean losing a message");
        recovered.Single().Attempts.Should().Be(2, "the second claim is a second attempt");
    }

    [Fact]
    public async Task A_delivered_message_is_not_claimed_again_but_is_still_there()
    {
        await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        var claim = (await outbox.ClaimAsync(10)).Single();
        (await outbox.MarkDeliveredAsync(claim)).Should().BeTrue();

        (await outbox.ClaimAsync(10)).Should().BeEmpty();

        await using var other = NewContext();
        (await other.Set<NoeliaOutboxMessage>().CountAsync()).Should().Be(1,
            "a delivered message is the evidence that the intent was carried out");
    }

    [Fact]
    public async Task A_released_message_comes_back_with_the_reason()
    {
        await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        var claim = (await outbox.ClaimAsync(10)).Single();
        await outbox.ReleaseAsync(claim, "the broker refused the routing key", _time.GetUtcNow());

        var again = await outbox.ClaimAsync(10);

        again.Should().HaveCount(1);
        again.Single().Attempts.Should().Be(2);

        await using var other = NewContext();
        var row = await other.Set<NoeliaOutboxMessage>().SingleAsync();
        row.LastError.Should().Be("the broker refused the routing key");
    }

    [Fact]
    public async Task Messages_are_handed_out_oldest_first()
    {
        await Record(new JobFinished("first"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await Record(new JobFinished("second"));

        await using var context = NewContext();
        var batch = await new EntityFrameworkOutbox<OutboxProbeContext>(context, _time).ClaimAsync(10);

        batch.Select(m => m.Payload).Should().ContainInOrder(
            """{"jobId":"first"}""", """{"jobId":"second"}""");
    }

    [Fact]
    public async Task A_long_refusal_does_not_fill_the_row()
    {
        await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        var claim = (await outbox.ClaimAsync(10)).Single();
        await outbox.ReleaseAsync(claim, new string('x', 5000), _time.GetUtcNow());

        await using var other = NewContext();
        var row = await other.Set<NoeliaOutboxMessage>().SingleAsync();
        row.LastError!.Length.Should().Be(500,
            "a transport that answers with a page of text would otherwise put a page "
            + "of text in every row");
    }

    [Fact]
    public async Task A_released_message_is_not_claimable_before_its_not_before_and_is_after()
    {
        await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        var claim = (await outbox.ClaimAsync(10)).Single();
        await outbox.ReleaseAsync(claim, "Refused", _time.GetUtcNow().AddSeconds(30));

        (await outbox.ClaimAsync(10)).Should().BeEmpty("the back-off has not elapsed");

        _time.Advance(TimeSpan.FromSeconds(29));
        (await outbox.ClaimAsync(10)).Should().BeEmpty("one second is still missing");

        _time.Advance(TimeSpan.FromSeconds(1));
        (await outbox.ClaimAsync(10)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_backed_off_message_does_not_hold_back_a_newer_one()
    {
        await Record(new JobFinished("old"));
        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        var claim = (await outbox.ClaimAsync(1)).Single();
        await outbox.ReleaseAsync(claim, "Refused", _time.GetUtcNow().AddMinutes(1));

        _time.Advance(TimeSpan.FromSeconds(1));
        await Record(new JobFinished("new"));

        var batch = await outbox.ClaimAsync(1);

        batch.Should().ContainSingle().Which.Payload.Should().Contain("new",
            "batch size 1 with the old message eligible would starve the new one for ever");
    }

    [Fact]
    public async Task A_quarantined_message_is_never_claimed_until_requeued()
    {
        var id = await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        var claim = (await outbox.ClaimAsync(10)).Single();
        (await outbox.QuarantineAsync(claim, "Refused")).Should().BeTrue();

        _time.Advance(TimeSpan.FromDays(30));
        (await outbox.ClaimAsync(10)).Should().BeEmpty("quarantine has no timeout");

        (await outbox.RequeueAsync(id)).Should().BeTrue();

        var again = (await outbox.ClaimAsync(10)).Should().ContainSingle().Subject;
        again.Attempts.Should().Be(1, "a requeued message gets a fresh budget");

        await using var other = NewContext();
        var row = await other.Set<NoeliaOutboxMessage>().SingleAsync();
        row.QuarantinedAt.Should().BeNull();
        row.LastError.Should().Contain("Requeued after 1 attempts").And.Contain("Refused",
            "the history must not vanish silently");
    }

    [Fact]
    public async Task Requeue_only_touches_quarantined_undelivered_messages()
    {
        var id = await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);

        (await outbox.RequeueAsync(id)).Should().BeFalse("it was never quarantined");
        (await outbox.RequeueAsync(Guid.NewGuid())).Should().BeFalse();

        var claim = (await outbox.ClaimAsync(10)).Single();
        await outbox.MarkDeliveredAsync(claim);
        (await outbox.RequeueAsync(id)).Should().BeFalse("a delivered message is not resumed");
    }

    [Fact]
    public async Task A_dispatcher_that_lost_its_lease_cannot_clobber_the_one_that_took_over()
    {
        await Record(new JobFinished("job-1"));

        await using var a = NewContext();
        await using var b = NewContext();
        var storeA = new EntityFrameworkOutbox<OutboxProbeContext>(a, _time);
        var storeB = new EntityFrameworkOutbox<OutboxProbeContext>(b, _time);

        var claimA = (await storeA.ClaimAsync(10)).Single();
        _time.Advance(TimeSpan.FromMinutes(6));
        var claimB = (await storeB.ClaimAsync(10)).Single();
        claimB.ClaimToken.Should().NotBe(claimA.ClaimToken);

        (await storeA.ReleaseAsync(claimA, "Late", _time.GetUtcNow())).Should().BeFalse();
        (await storeA.QuarantineAsync(claimA, "Late")).Should().BeFalse();
        (await storeA.MarkDeliveredAsync(claimA)).Should().BeFalse();

        await using (var check = NewContext())
        {
            var row = await check.Set<NoeliaOutboxMessage>().SingleAsync();
            row.ClaimToken.Should().Be(claimB.ClaimToken, "B's claim is untouched");
            row.DeliveredAt.Should().BeNull();
            row.QuarantinedAt.Should().BeNull();
            row.LastError.Should().BeNull();
        }

        (await storeB.MarkDeliveredAsync(claimB)).Should().BeTrue();

        await using var final = NewContext();
        (await final.Set<NoeliaOutboxMessage>().SingleAsync()).DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Every_claim_call_stamps_its_own_token()
    {
        await Record(new JobFinished("one"));
        await Record(new JobFinished("two"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);

        var first = await outbox.ClaimAsync(1);
        var second = await outbox.ClaimAsync(1);

        first.Single().ClaimToken.Should().NotBe(second.Single().ClaimToken);
        first.Single().ClaimToken.Should().NotBe(Guid.Empty);
    }

    private async Task<Guid> Record<TEvent>(TEvent @event) where TEvent : class
    {
        await using var context = NewContext();
        var id = await new EntityFrameworkOutbox<OutboxProbeContext>(context, _time)
            .RecordAsync(@event);
        await context.SaveChangesAsync();
        return id;
    }

    private OutboxProbeContext NewContext() =>
        new(new DbContextOptionsBuilder<OutboxProbeContext>().UseSqlite(_connection).Options);

    private sealed record JobFinished(string JobId);

    private sealed class Job
    {
        public string Id { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
    }

    private sealed class OutboxProbeContext(DbContextOptions<OutboxProbeContext> options)
        : DbContext(options)
    {
        public DbSet<Job> Jobs => Set<Job>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Job>().HasKey(j => j.Id);
            modelBuilder.MapNoeliaOutbox();
        }
    }
}
