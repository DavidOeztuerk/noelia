using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Messaging;

namespace Noelia.Infrastructure.Messaging;

/// <summary>What the dispatcher needs decided before it runs.</summary>
public sealed class OutboxDispatcherOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Outbox";

    /// <summary>How long to wait after finding nothing.</summary>
    /// <remarks>
    /// Only after an empty turn. A turn that delivered something asks again
    /// immediately, so a backlog drains at the speed of the transport rather
    /// than at the speed of this interval.
    /// </remarks>
    public TimeSpan IdleInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most messages to claim in one turn.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// After this many failed attempts a message is reported rather than
    /// retried quietly.
    /// </summary>
    /// <remarks>
    /// It is not dropped. A message nothing will accept is a decision for an
    /// operator — the payload may be unroutable, or the consumer may have been
    /// deployed wrong — and a dispatcher that binned it would take that
    /// decision silently.
    /// </remarks>
    public int AttemptsBeforeAlarm { get; set; } = 10;
}

/// <summary>
/// Delivers what the outbox recorded, one batch at a time.
/// </summary>
/// <remarks>
/// <para>Separate from <see cref="IOutbox"/> on purpose. Recording belongs to
/// the transaction that caused it; delivering is a loop with its own schedule
/// and its own failures, and running it inside a request would tie a user's
/// response time to a broker's mood.</para>
///
/// <para><strong>Delivery is at-least-once, and this does not pretend
/// otherwise.</strong> A publish that succeeds and a mark that fails leaves the
/// message to be delivered again — which is the right way round: the
/// alternative is marking first and losing the message when the publish fails.
/// Consumers see <see cref="OutboxMessage.Id"/> and decide.</para>
/// </remarks>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        while (!stoppingToken.IsCancellationRequested)
        {
            int delivered;

            try
            {
                delivered = await DeliverBatchAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The store itself failed — not one message. Log and wait:
                // a dispatcher that exits on a transient database error stops
                // delivering until someone restarts the process.
                StoreUnavailable(logger, exception);
                delivered = 0;
            }

            if (delivered == 0)
            {
                await Task.Delay(settings.IdleInterval, stoppingToken);
            }
        }
    }

    private async Task<int> DeliverBatchAsync(
        OutboxDispatcherOptions settings,
        CancellationToken cancellationToken)
    {
        // A scope per batch: the outbox is scoped because the context is, and a
        // background service that resolved it once would hold one context for
        // the lifetime of the process.
        using var scope = scopes.CreateScope();

        var reader = scope.ServiceProvider.GetRequiredService<IOutboxReader>();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var serialiser = scope.ServiceProvider.GetRequiredService<IOutboxPayloadReader>();

        var batch = await reader.ClaimAsync(settings.BatchSize, cancellationToken);
        var delivered = 0;

        foreach (var message in batch)
        {
            try
            {
                await bus.PublishAsync(serialiser.Read(message), cancellationToken);
                await reader.MarkDeliveredAsync(message.Id, cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The claim expires on its own; releasing here would need the
                // store, which is the thing that is going away.
                throw;
            }
            catch (Exception exception)
            {
                await reader.ReleaseAsync(message.Id, exception.Message, CancellationToken.None);

                if (message.Attempts >= settings.AttemptsBeforeAlarm)
                {
                    Stuck(logger, message.Id, message.Type, message.Attempts, exception);
                }
                else
                {
                    Refused(logger, message.Id, message.Type, message.Attempts, exception);
                }
            }
        }

        return delivered;
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The outbox store is unavailable; delivery is paused")]
    private static partial void StoreUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} of {MessageType} was refused on attempt {Attempts}")]
    private static partial void Refused(
        ILogger logger, Guid messageId, string messageType, int attempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Outbox message {MessageId} of {MessageType} has failed {Attempts} times and "
                  + "is not being delivered. It is still recorded and nothing has been dropped.")]
    private static partial void Stuck(
        ILogger logger, Guid messageId, string messageType, int attempts, Exception exception);
}

/// <summary>Turns a recorded payload back into the event it was.</summary>
/// <remarks>
/// A port, because the answer is the application's. The type name in the row
/// belongs to the recorder's assembly, and how — or whether — a dispatcher
/// resolves it is a decision about trust: deserialising an arbitrary named type
/// out of a table is how a database row becomes code execution.
/// </remarks>
public interface IOutboxPayloadReader
{
    /// <summary>The event this message recorded.</summary>
    /// <param name="message">The recorded message.</param>
    object Read(OutboxMessage message);
}

/// <summary>Registers the dispatcher.</summary>
public static class OutboxDispatcherExtensions
{
    /// <summary>The module id, so the composition report names it.</summary>
    public static NoeliaModule Module => new("OutboxDispatcher");

    /// <summary>
    /// Runs the loop that delivers what the outbox recorded.
    /// </summary>
    /// <remarks>
    /// One service in a deployment needs this, not every one: several
    /// dispatchers against one store are safe — claiming is a conditional
    /// update — but they are also unnecessary, and each one is another
    /// connection holding batches.
    /// </remarks>
    /// <param name="noelia">The composition.</param>
    public static NoeliaBuilder UseOutboxDispatcher(this NoeliaBuilder noelia)
    {
        ArgumentNullException.ThrowIfNull(noelia);

        return noelia.Use(
            Module,
            builder =>
            {
                builder.Services
                    .AddOptions<OutboxDispatcherOptions>()
                    .Bind(builder.Configuration.GetSection(OutboxDispatcherOptions.SectionName));

                builder.Services.AddHostedService<OutboxDispatcher>();
            },
            contract => contract
                .Requires<IOutboxReader>(new NoeliaProviderHint(
                    "Noelia.Data.EntityFrameworkCore", "AddEntityFrameworkOutbox<TContext>()"))
                .Requires<IEventBus>(new NoeliaProviderHint(
                    "Noelia.Messaging.MassTransit", "UseMassTransitMessaging(assemblies)"))
                .Requires<IOutboxPayloadReader>(new NoeliaProviderHint(
                    "your application", "AddOutboxPayloadReader(...) naming the types you accept"))
                // A hosted service, not something callers resolve. Naming it
                // anyway is the honest statement: the module's whole effect is
                // that a loop now runs, and the composition report should say
                // so rather than look like a module that does nothing.
                .Provides<IHostedService>(
                    "Noelia.Infrastructure", "UseOutboxDispatcher()"));
    }
}
