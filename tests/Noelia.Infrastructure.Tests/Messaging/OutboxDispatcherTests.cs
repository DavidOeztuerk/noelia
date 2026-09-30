using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Noelia.Abstractions.Messaging;
using Noelia.Data.EntityFrameworkCore.Messaging;
using Noelia.Infrastructure.Messaging;

namespace Noelia.Infrastructure.Tests.Messaging;

/// <summary>
/// What the delivery loop does with what it claimed.
/// </summary>
/// <remarks>
/// The order of publish and mark is the whole design. Publishing first and
/// marking second means a crash in between delivers the message twice, which is
/// what at-least-once means and what consumers are told to expect. Marking
/// first would mean a crash in between loses it, which nothing can recover.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class OutboxDispatcherTests
{
    private const string Canary = "SECRET-CANARY-4711";

    [Fact]
    public async Task A_delivered_message_is_published_then_marked()
    {
        var store = new RecordingReader(Message("one"));
        var bus = new RecordingBus();

        await RunOnce(store, bus);

        bus.Published.Should().ContainSingle();
        store.Delivered.Should().ContainSingle();
        store.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_message_is_released_with_a_classification_and_a_delay_and_not_marked()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T10:00:00Z"));
        var store = new RecordingReader(Message("one", attempts: 1));
        var bus = new RecordingBus { Refuse = new InvalidOperationException($"no route for {Canary}") };

        await RunOnce(store, bus, time: time);

        store.Delivered.Should().BeEmpty("a message that did not reach the transport is not sent");
        var released = store.Released.Should().ContainSingle().Subject;
        released.Reason.Should().Be("InvalidOperationException",
            "the exception text echoes payloads and secrets; the type is enough for an operator");
        released.NotBefore.Should().Be(time.GetUtcNow().AddSeconds(5));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(9, 300)]
    [InlineData(50, 300)]
    public async Task The_retry_delay_doubles_and_is_capped(int attempts, int expectedSeconds)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T10:00:00Z"));
        var store = new RecordingReader(Message("one", attempts));
        var bus = new RecordingBus { Refuse = new InvalidOperationException("x") };

        await RunOnce(
            store, bus, time: time,
            configure: o =>
            {
                o.MaxAttempts = 100;
                o.MaxRetryDelay = TimeSpan.FromMinutes(5);
            });

        store.Released.Should().ContainSingle().Which.NotBefore
            .Should().Be(time.GetUtcNow().AddSeconds(expectedSeconds));
    }

    [Fact]
    public async Task A_message_out_of_attempts_is_quarantined_not_released()
    {
        var store = new RecordingReader(Message("one", attempts: 3));
        var bus = new RecordingBus { Refuse = new InvalidOperationException(Canary) };

        await RunOnce(store, bus, configure: o => o.MaxAttempts = 3);

        store.Released.Should().BeEmpty();
        store.Quarantined.Should().ContainSingle().Which.Reason.Should().Be("InvalidOperationException");
    }

    [Fact]
    public async Task One_refusal_does_not_stop_the_rest_of_the_batch()
    {
        var store = new RecordingReader(Message("bad"), Message("good"));
        var bus = new RecordingBus { RefuseFirstOnly = new InvalidOperationException("nope") };

        await RunOnce(store, bus);

        store.Delivered.Should().ContainSingle("the second message has nothing to do with the first");
        store.Released.Should().ContainSingle();
    }

    [Fact]
    public async Task A_stale_completion_is_logged_and_does_not_count_as_a_failure()
    {
        var store = new RecordingReader(Message("one")) { Stale = true };
        var bus = new RecordingBus();
        var log = new CapturingLogger();

        await RunOnce(store, bus, logger: log);

        bus.Published.Should().ContainSingle();
        store.Released.Should().BeEmpty("the newer claim owns the outcome");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("taken over"));
    }

    [Fact]
    public async Task A_full_batch_of_poison_messages_does_not_starve_a_newer_good_one()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var poison = new[]
        {
            await fixture.RecordAsync($"poison-1 {Canary}"),
            await fixture.RecordAsync($"poison-2 {Canary}"),
            await fixture.RecordAsync($"poison-3 {Canary}")
        };
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var good = await fixture.RecordAsync("good");

        // Batch size equals the number of poison messages: before the back-off
        // they were the oldest N on every turn, for ever.
        var bus = new RecordingBus { RefuseWhen = p => p.Contains("poison"), RefusalText = Canary };
        // Wait for the delivery itself, not for the claim: Attempts rises when a
        // row is claimed, before it is published and marked, so stopping on it
        // raced the dispatcher. A starved message still fails at the deadline.
        await fixture.RunDispatcherAsync(
            bus,
            o => o.BatchSize = 3,
            until: rows => rows.Single(r => r.Id == good).DeliveredAt != null
                           && poison.All(id => rows.Single(r => r.Id == id).Attempts > 0));

        var rows = await fixture.RowsAsync();
        rows.Single(r => r.Id == good).DeliveredAt.Should().NotBeNull(
            "the good message must be reached within a bounded number of turns");
        foreach (var id in poison)
        {
            var row = rows.Single(r => r.Id == id);
            row.Attempts.Should().Be(1, "the back-off keeps them out of the next turns");
            row.NextAttemptAt.Should().NotBeNull();
            row.DeliveredAt.Should().BeNull();
        }
    }

    [Fact]
    public async Task What_is_stored_and_logged_about_a_failure_carries_no_payload_or_exception_text()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await fixture.RecordAsync($"poison {Canary}");
        var log = new CapturingLogger();

        var bus = new RecordingBus { RefuseWhen = _ => true, RefusalText = Canary };
        await fixture.RunDispatcherAsync(bus, logger: log);

        var row = (await fixture.RowsAsync()).Single();
        row.LastError.Should().Be("InvalidOperationException");
        log.Entries.Should().NotBeEmpty();
        log.Entries.Should().OnlyContain(e => !e.Text.Contains(Canary) && e.Exception == null);
    }

    [Fact]
    public async Task A_message_is_quarantined_after_max_attempts_logged_once_and_never_claimed_again()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var id = await fixture.RecordAsync("poison");
        var log = new CapturingLogger();
        var bus = new RecordingBus { RefuseWhen = _ => true };

        await fixture.RunDispatcherAsync(
            bus,
            o =>
            {
                o.MaxAttempts = 3;
                o.BaseRetryDelay = TimeSpan.FromSeconds(1);
                o.MaxRetryDelay = TimeSpan.FromSeconds(1);
            },
            log,
            advanceEachTick: TimeSpan.FromSeconds(2),
            until: rows => rows.All(r => r.QuarantinedAt != null));

        var row = (await fixture.RowsAsync()).Single(r => r.Id == id);
        row.QuarantinedAt.Should().NotBeNull();
        row.Attempts.Should().Be(3);
        bus.Calls.Should().Be(3, "a quarantined message is not published again");
        log.Entries.Count(e => e.Level == LogLevel.Error).Should().Be(1,
            "the operator is told once, not on every turn");
    }

    [Fact]
    public async Task A_requeued_message_is_delivered_by_the_dispatcher_once_the_cause_is_fixed()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var id = await fixture.RecordAsync("flaky");
        var broken = new RecordingBus { RefuseWhen = _ => true };
        await fixture.RunDispatcherAsync(
            broken, o => o.MaxAttempts = 1, until: rows => rows.All(r => r.QuarantinedAt != null));
        (await fixture.RowsAsync()).Single().QuarantinedAt.Should().NotBeNull();

        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IOutboxReader>().RequeueAsync(id))
                .Should().BeTrue();
        }

        await fixture.RunDispatcherAsync(
            new RecordingBus(), until: rows => rows.All(r => r.DeliveredAt != null));

        (await fixture.RowsAsync()).Single().DeliveredAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(10, 0, 1, 1)]
    [InlineData(10, 1, 0, 1)]
    [InlineData(10, 1, 10, 5)]
    public void Options_that_cannot_work_are_rejected(
        int batchSize, int maxAttempts, int baseSeconds, int maxSeconds)
    {
        var options = new OutboxDispatcherOptions
        {
            BatchSize = batchSize,
            MaxAttempts = maxAttempts,
            BaseRetryDelay = TimeSpan.FromSeconds(baseSeconds),
            MaxRetryDelay = TimeSpan.FromSeconds(maxSeconds)
        };

        new OutboxDispatcherOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void The_defaults_are_valid_and_invalid_options_fail_when_resolved()
    {
        new OutboxDispatcherOptionsValidator().Validate(null, new OutboxDispatcherOptions())
            .Succeeded.Should().BeTrue();

        var services = new ServiceCollection();
        services.AddOptions<OutboxDispatcherOptions>().Configure(o => o.MaxAttempts = 0);
        services.AddSingleton<IValidateOptions<OutboxDispatcherOptions>, OutboxDispatcherOptionsValidator>();
        using var provider = services.BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptions<OutboxDispatcherOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("MaxAttempts");
    }

    private static async Task RunOnce(
        RecordingReader store,
        RecordingBus bus,
        Action<OutboxDispatcherOptions>? configure = null,
        FakeTimeProvider? time = null,
        ILogger<OutboxDispatcher>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxReader>(store);
        services.AddSingleton<IEventBus>(bus);
        services.AddSingleton<IOutboxPayloadReader>(new PassThroughReader());
        await using var provider = services.BuildServiceProvider();

        var options = new OutboxDispatcherOptions { IdleInterval = TimeSpan.FromMilliseconds(5) };
        configure?.Invoke(options);

        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            logger ?? NullLogger<OutboxDispatcher>.Instance,
            time);

        using var stop = new CancellationTokenSource();
        await dispatcher.StartAsync(stop.Token);
        await Task.Delay(120, CancellationToken.None);
        await stop.CancelAsync();
        await dispatcher.StopAsync(CancellationToken.None);
    }

    private static OutboxMessage Message(string id, int attempts = 1) =>
        new(Guid.NewGuid(), "Probe", $$"""{"id":"{{id}}"}""", null, DateTimeOffset.UtcNow, attempts, Guid.NewGuid());

    private sealed class PassThroughReader : IOutboxPayloadReader
    {
        public object Read(OutboxMessage message) => message.Payload;
    }

    private sealed class RecordingBus : IEventBus
    {
        public List<object> Published { get; } = [];
        public Exception? Refuse { get; init; }
        public Exception? RefuseFirstOnly { get; init; }
        public Func<string, bool>? RefuseWhen { get; init; }
        public string RefusalText { get; init; } = "refused";
        private int _seen;

        public int Calls => Volatile.Read(ref _seen);

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : class
        {
            var first = Interlocked.Increment(ref _seen) == 1;

            if (Refuse is not null) return Task.FromException(Refuse);
            if (first && RefuseFirstOnly is not null) return Task.FromException(RefuseFirstOnly);
            if (RefuseWhen is not null && RefuseWhen(@event.ToString()!))
                return Task.FromException(new InvalidOperationException(RefusalText));

            lock (Published) Published.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReader(params OutboxMessage[] batch) : IOutboxReader
    {
        private bool _handedOut;

        public bool Stale { get; init; }
        public List<Guid> Delivered { get; } = [];
        public List<(Guid Id, string Reason, DateTimeOffset NotBefore)> Released { get; } = [];
        public List<(Guid Id, string Reason)> Quarantined { get; } = [];

        public Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
            int batchSize, CancellationToken cancellationToken = default)
        {
            if (_handedOut) return Task.FromResult<IReadOnlyList<OutboxMessage>>([]);
            _handedOut = true;
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(batch);
        }

        public Task<bool> MarkDeliveredAsync(OutboxMessage claim, CancellationToken cancellationToken = default)
        {
            if (Stale) return Task.FromResult(false);
            Delivered.Add(claim.Id);
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseAsync(
            OutboxMessage claim, string reason, DateTimeOffset notBefore,
            CancellationToken cancellationToken = default)
        {
            Released.Add((claim.Id, reason, notBefore));
            return Task.FromResult(true);
        }

        public Task<bool> QuarantineAsync(
            OutboxMessage claim, string reason, CancellationToken cancellationToken = default)
        {
            Quarantined.Add((claim.Id, reason));
            return Task.FromResult(true);
        }

        public Task<bool> RequeueAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class CapturingLogger : ILogger<OutboxDispatcher>
    {
        private readonly List<(LogLevel Level, string Text, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Text, Exception? Exception)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.MapNoeliaOutbox();
    }

    /// <summary>A real SQLite outbox behind a real dispatcher, on a fake clock.</summary>
    /// <remarks>
    /// A file, not one shared <c>:memory:</c> connection. The dispatcher and the
    /// test's polling read at the same time, and a single SqliteConnection is not
    /// safe for that: it failed about one run in five with "database is locked"
    /// while a second context initialised on it. With a file every context opens
    /// its own connection and SQLite's busy wait does the rest, which is also how
    /// two real dispatchers meet.
    /// </remarks>
    private sealed class SqliteFixture : IAsyncDisposable
    {
        private readonly string _path;
        private readonly string _connectionString;

        private SqliteFixture(string path, string connectionString, ServiceProvider provider, FakeTimeProvider time)
        {
            _path = path;
            _connectionString = connectionString;
            Provider = provider;
            Time = time;
        }

        public ServiceProvider Provider { get; }
        public FakeTimeProvider Time { get; }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"noelia-outbox-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString();
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T10:00:00Z"));

            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddDbContext<ProbeContext>(o => o.UseSqlite(connectionString));
            services.AddEntityFrameworkOutbox<ProbeContext>();
            services.AddSingleton<IOutboxPayloadReader>(new PassThroughReader());
            var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ProbeContext>().Database.EnsureCreatedAsync();
            }

            return new SqliteFixture(path, connectionString, provider, time);
        }

        public async Task<Guid> RecordAsync(string payload)
        {
            await using var scope = Provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ProbeContext>();
            var id = Guid.NewGuid();
            context.Set<NoeliaOutboxMessage>().Add(new NoeliaOutboxMessage
            {
                Id = id,
                Type = "Probe",
                Payload = payload,
                RecordedAt = Time.GetUtcNow().UtcDateTime
            });
            await context.SaveChangesAsync();
            return id;
        }

        public async Task<List<NoeliaOutboxMessage>> RowsAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ProbeContext>()
                .Set<NoeliaOutboxMessage>().AsNoTracking().ToListAsync();
        }

        public async Task RunDispatcherAsync(
            RecordingBus bus,
            Action<OutboxDispatcherOptions>? configure = null,
            ILogger<OutboxDispatcher>? logger = null,
            TimeSpan? advanceEachTick = null,
            Func<IReadOnlyList<NoeliaOutboxMessage>, bool>? until = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(Time);
            services.AddSingleton<TimeProvider>(Time);
            services.AddDbContext<ProbeContext>(o => o.UseSqlite(_connectionString));
            services.AddEntityFrameworkOutbox<ProbeContext>();
            services.AddSingleton<IEventBus>(bus);
            services.AddSingleton<IOutboxPayloadReader>(new PassThroughReader());
            await using var provider = services.BuildServiceProvider();

            var options = new OutboxDispatcherOptions { IdleInterval = TimeSpan.FromMilliseconds(5) };
            configure?.Invoke(options);

            var dispatcher = new OutboxDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(options),
                logger ?? NullLogger<OutboxDispatcher>.Instance,
                Time);

            using var stop = new CancellationTokenSource();
            await dispatcher.StartAsync(stop.Token);

            // Poll for the outcome instead of sleeping a fixed time: the first
            // turn pays for model building and JIT, and a fixed wait is a flake
            // on a cold run.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, CancellationToken.None);
                if (advanceEachTick is { } step) Time.Advance(step);
                if ((until ?? Settled)(await RowsAsync())) break;
            }

            // Then a grace period, so a dispatcher that would keep doing
            // something it should not has the chance to be caught at it.
            await Task.Delay(150, CancellationToken.None);

            await stop.CancelAsync();
            await dispatcher.StopAsync(CancellationToken.None);
        }

        // Default: every row is delivered, quarantined or has been tried and backed off.
        private static bool Settled(IReadOnlyList<NoeliaOutboxMessage> rows) =>
            rows.All(r => r.DeliveredAt != null || r.QuarantinedAt != null || r.Attempts > 0);

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            File.Delete(_path);
        }
    }
}
