using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Messaging;
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
    public async Task A_refused_message_is_released_with_the_reason_and_not_marked()
    {
        var store = new RecordingReader(Message("one"));
        var bus = new RecordingBus { Refuse = new InvalidOperationException("no route to queue") };

        await RunOnce(store, bus);

        store.Delivered.Should().BeEmpty("a message that did not reach the transport is not sent");
        store.Released.Should().ContainSingle()
            .Which.Reason.Should().Be("no route to queue");
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

    private static async Task RunOnce(RecordingReader store, RecordingBus bus)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxReader>(store);
        services.AddSingleton<IEventBus>(bus);
        services.AddSingleton<IOutboxPayloadReader>(new PassThroughReader());
        await using var provider = services.BuildServiceProvider();

        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxDispatcherOptions { IdleInterval = TimeSpan.FromMilliseconds(5) }),
            NullLogger<OutboxDispatcher>.Instance);

        using var stop = new CancellationTokenSource();
        await dispatcher.StartAsync(stop.Token);
        await Task.Delay(120, CancellationToken.None);
        await stop.CancelAsync();
        await dispatcher.StopAsync(CancellationToken.None);
    }

    private static OutboxMessage Message(string id) =>
        new(Guid.NewGuid(), "Probe", $$"""{"id":"{{id}}"}""", null, DateTimeOffset.UtcNow, 1);

    private sealed class PassThroughReader : IOutboxPayloadReader
    {
        public object Read(OutboxMessage message) => message.Payload;
    }

    private sealed class RecordingBus : IEventBus
    {
        public List<object> Published { get; } = [];
        public Exception? Refuse { get; init; }
        public Exception? RefuseFirstOnly { get; init; }
        private int _seen;

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : class
        {
            var first = Interlocked.Increment(ref _seen) == 1;

            if (Refuse is not null) return Task.FromException(Refuse);
            if (first && RefuseFirstOnly is not null) return Task.FromException(RefuseFirstOnly);

            Published.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReader(params OutboxMessage[] batch) : IOutboxReader
    {
        private bool _handedOut;

        public List<Guid> Delivered { get; } = [];
        public List<(Guid Id, string Reason)> Released { get; } = [];

        public Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
            int batchSize, CancellationToken cancellationToken = default)
        {
            if (_handedOut) return Task.FromResult<IReadOnlyList<OutboxMessage>>([]);
            _handedOut = true;
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(batch);
        }

        public Task MarkDeliveredAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Delivered.Add(id);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(Guid id, string reason, CancellationToken cancellationToken = default)
        {
            Released.Add((id, reason));
            return Task.CompletedTask;
        }
    }
}
