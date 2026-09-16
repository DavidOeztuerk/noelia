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
        var id = await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        await outbox.ClaimAsync(10);
        await outbox.MarkDeliveredAsync(id);

        (await outbox.ClaimAsync(10)).Should().BeEmpty();

        await using var other = NewContext();
        (await other.Set<NoeliaOutboxMessage>().CountAsync()).Should().Be(1,
            "a delivered message is the evidence that the intent was carried out");
    }

    [Fact]
    public async Task A_released_message_comes_back_with_the_reason()
    {
        var id = await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        await outbox.ClaimAsync(10);
        await outbox.ReleaseAsync(id, "the broker refused the routing key");

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
        var id = await Record(new JobFinished("job-1"));

        await using var context = NewContext();
        var outbox = new EntityFrameworkOutbox<OutboxProbeContext>(context, _time);
        await outbox.ClaimAsync(10);
        await outbox.ReleaseAsync(id, new string('x', 5000));

        await using var other = NewContext();
        var row = await other.Set<NoeliaOutboxMessage>().SingleAsync();
        row.LastError!.Length.Should().Be(500,
            "a transport that answers with a page of text would otherwise put a page "
            + "of text in every row");
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
