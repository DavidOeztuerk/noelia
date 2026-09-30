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
    /// How many delivery attempts a message gets before it is quarantined.
    /// </summary>
    /// <remarks>
    /// Quarantine, not deletion: the row stays with its reason, is never claimed
    /// again and is logged once at error level. A payload nothing will accept is a
    /// decision for an operator, who resumes it with
    /// <see cref="IOutboxReader.RequeueAsync"/> once the cause is fixed. Without
    /// this ceiling a message that never succeeds is retried for ever.
    /// </remarks>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The delay after the first failed attempt; each further failure doubles it.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest a failed message waits before it is tried again.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>Rejects dispatcher settings that cannot work, at startup.</summary>
/// <remarks>
/// A zero batch size or a negative delay does not fail anywhere near its cause:
/// the loop simply never delivers, or retries in a tight spin.
/// </remarks>
public sealed class OutboxDispatcherOptionsValidator : IValidateOptions<OutboxDispatcherOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, OutboxDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.BatchSize < 1)
            failures.Add($"{OutboxDispatcherOptions.SectionName}:BatchSize must be at least 1.");
        if (options.IdleInterval <= TimeSpan.Zero)
            failures.Add($"{OutboxDispatcherOptions.SectionName}:IdleInterval must be positive.");
        if (options.MaxAttempts < 1)
            failures.Add($"{OutboxDispatcherOptions.SectionName}:MaxAttempts must be at least 1.");
        if (options.BaseRetryDelay <= TimeSpan.Zero)
            failures.Add($"{OutboxDispatcherOptions.SectionName}:BaseRetryDelay must be positive.");
        if (options.MaxRetryDelay < options.BaseRetryDelay)
            failures.Add($"{OutboxDispatcherOptions.SectionName}:MaxRetryDelay must not be shorter than BaseRetryDelay.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
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
/// Consumers see <see cref="OutboxMessage.Id"/> and must deduplicate by it.</para>
///
/// <para><strong>Failure policy lives here, not in the store.</strong> A refused
/// message is released with an exponentially growing delay
/// (<see cref="OutboxDispatcherOptions.BaseRetryDelay"/> up to
/// <see cref="OutboxDispatcherOptions.MaxRetryDelay"/>), so a message nothing
/// accepts cannot occupy every batch and starve newer ones. After
/// <see cref="OutboxDispatcherOptions.MaxAttempts"/> it is quarantined. What is
/// stored and logged about a failure is the exception's type name, never its
/// message: those routinely echo the payload or connection details.</para>
/// </remarks>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<OutboxDispatcher> logger,
    TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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

                if (await reader.MarkDeliveredAsync(message, cancellationToken))
                {
                    delivered++;
                }
                else
                {
                    // Published, but the lease had been lost and someone else
                    // owns the outcome. That is the at-least-once path, not a
                    // failure; say so once and change nothing.
                    StaleCompletion(logger, message.Id, message.Type);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The claim expires on its own; releasing here would need the
                // store, which is the thing that is going away.
                throw;
            }
            catch (Exception exception)
            {
                // The type name, never the message: exception texts echo
                // payloads, routing keys and connection strings, and this ends
                // up in a table and in logs.
                var reason = Classify(exception);

                if (message.Attempts >= settings.MaxAttempts)
                {
                    if (await reader.QuarantineAsync(message, reason, CancellationToken.None))
                    {
                        Quarantined(logger, message.Id, message.Type, message.Attempts, reason);
                    }
                    else
                    {
                        StaleCompletion(logger, message.Id, message.Type);
                    }
                }
                else
                {
                    var delay = RetryDelay(settings, message.Attempts);

                    if (await reader.ReleaseAsync(
                            message, reason, _time.GetUtcNow() + delay, CancellationToken.None))
                    {
                        Refused(logger, message.Id, message.Type, message.Attempts, reason, delay);
                    }
                    else
                    {
                        StaleCompletion(logger, message.Id, message.Type);
                    }
                }
            }
        }

        return delivered;
    }

    private static string Classify(Exception exception) => exception.GetType().Name;

    // base * 2^(attempts-1), capped. The exponent is clamped before the shift so
    // a large attempt count cannot overflow into a negative delay.
    private static TimeSpan RetryDelay(OutboxDispatcherOptions settings, int attempts)
    {
        var factor = Math.Pow(2, Math.Clamp(attempts - 1, 0, 30));
        var ticks = settings.BaseRetryDelay.Ticks * factor;

        return ticks >= settings.MaxRetryDelay.Ticks
            ? settings.MaxRetryDelay
            : TimeSpan.FromTicks((long)ticks);
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The outbox store is unavailable; delivery is paused")]
    private static partial void StoreUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} of {MessageType} was refused on attempt {Attempts} "
                  + "({Reason}); next attempt in {Delay}")]
    private static partial void Refused(
        ILogger logger, Guid messageId, string messageType, int attempts, string reason, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Outbox message {MessageId} of {MessageType} failed {Attempts} times ({Reason}) and "
                  + "is quarantined. It is still recorded and nothing has been dropped; "
                  + "IOutboxReader.RequeueAsync resumes it.")]
    private static partial void Quarantined(
        ILogger logger, Guid messageId, string messageType, int attempts, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} of {MessageType} was completed after its claim had "
                  + "been taken over; the newer claim's outcome stands")]
    private static partial void StaleCompletion(ILogger logger, Guid messageId, string messageType);
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
                    .Bind(builder.Configuration.GetSection(OutboxDispatcherOptions.SectionName))
                    .ValidateOnStart();

                builder.Services.AddSingleton<
                    IValidateOptions<OutboxDispatcherOptions>, OutboxDispatcherOptionsValidator>();

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
