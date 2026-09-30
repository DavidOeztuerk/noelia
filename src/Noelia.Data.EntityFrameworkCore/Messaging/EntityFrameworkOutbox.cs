using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Messaging;
using Noelia.Abstractions.Observability;

namespace Noelia.Data.EntityFrameworkCore.Messaging;

/// <summary>
/// Records intents in the same transaction as the change, and hands them to a
/// dispatcher afterwards.
/// </summary>
/// <typeparam name="TContext">The application's own context.</typeparam>
/// <remarks>
/// <para><see cref="RecordAsync{TEvent}"/> adds the row and does not save. That
/// is the whole point: the application's own <c>SaveChangesAsync</c> commits
/// the change and the intent together, or neither. An implementation that saved
/// here would produce a row that survives a rolled-back change and announces
/// something that never happened.</para>
///
/// <para>Claiming is a conditional update, not a read followed by a write. Two
/// dispatchers against one database otherwise both read the same row and both
/// deliver it, and at-least-once quietly becomes at-least-twice-per-dispatcher.
/// </para>
///
/// <para>Completions are fenced by a claim token, so a dispatcher whose lease
/// expired cannot overwrite the outcome of the one that took the message over.
/// Delivery is at-least-once: consumers deduplicate by
/// <see cref="OutboxMessage.Id"/>.</para>
/// </remarks>
public sealed partial class EntityFrameworkOutbox<TContext>(
    TContext context,
    TimeProvider time,
    ILogger<EntityFrameworkOutbox<TContext>>? logger = null)
    : IOutbox, IOutboxReader
    where TContext : DbContext
{
    /// <summary>
    /// How long a claim is believed before the message is free again.
    /// </summary>
    /// <remarks>
    /// A dispatcher that dies mid-delivery holds its claim forever otherwise.
    /// Long enough that an ordinary slow publish is not retried underneath
    /// itself; short enough that a lost dispatcher is not a lost message.
    /// </remarks>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    private const int MaxErrorLength = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public Task<Guid> RecordAsync<TEvent>(
        TEvent @event,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(@event);
        cancellationToken.ThrowIfCancellationRequested();

        var message = new NoeliaOutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = typeof(TEvent).FullName ?? typeof(TEvent).Name,
            Payload = JsonSerializer.Serialize(@event, Json),
            CorrelationId = correlationId ?? CorrelationId.Current,
            RecordedAt = time.GetUtcNow().UtcDateTime
        };

        context.Set<NoeliaOutboxMessage>().Add(message);

        // Deliberately no SaveChangesAsync. The caller's transaction decides
        // whether this ever happened.
        return Task.FromResult(message.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var now = time.GetUtcNow().UtcDateTime;
        var expired = now - ClaimLease;

        // Due means: not delivered, not quarantined, past its back-off and not
        // under a live lease. Back-off and quarantine are what keep a batch of
        // messages nothing will accept from being the oldest N forever.
        var ids = await Claimable(now, expired)
            .OrderBy(m => m.RecordedAt)
            .Select(m => m.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        // One fresh token per call. The rows are read back by it rather than by
        // "ClaimedAt == now", which two dispatchers on one clock tick could both
        // match.
        var token = Guid.NewGuid();

        // One statement, so two dispatchers cannot both take the same row: the
        // WHERE runs at update time, and the second one matches nothing.
        var claimed = await Claimable(now, expired)
            .Where(m => ids.Contains(m.Id))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.ClaimedAt, now)
                    .SetProperty(m => m.ClaimToken, token)
                    .SetProperty(m => m.Attempts, m => m.Attempts + 1),
                cancellationToken);

        if (claimed == 0)
        {
            return [];
        }

        return await context.Set<NoeliaOutboxMessage>()
            .Where(m => m.ClaimToken == token)
            .OrderBy(m => m.RecordedAt)
            .Select(m => new OutboxMessage(
                m.Id,
                m.Type,
                m.Payload,
                m.CorrelationId,
                new DateTimeOffset(m.RecordedAt, TimeSpan.Zero),
                m.Attempts,
                token))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The row stays. A delivered message is the evidence that the intent was
    /// carried out, and deleting it would leave an operator unable to answer
    /// "was this ever sent?". Pruning old rows is a separate decision, the same
    /// split as the refresh token store's purge.
    /// </remarks>
    public async Task<bool> MarkDeliveredAsync(
        OutboxMessage claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var changed = await Owned(claim)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.DeliveredAt, time.GetUtcNow().UtcDateTime)
                    .SetProperty(m => m.ClaimedAt, (DateTime?)null)
                    .SetProperty(m => m.ClaimToken, (Guid?)null)
                    .SetProperty(m => m.NextAttemptAt, (DateTime?)null)
                    .SetProperty(m => m.LastError, (string?)null),
                cancellationToken);

        return changed == 1;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(
        OutboxMessage claim,
        string reason,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var trimmed = Trim(reason);
        var due = notBefore.UtcDateTime;

        var changed = await Owned(claim)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.ClaimedAt, (DateTime?)null)
                    .SetProperty(m => m.ClaimToken, (Guid?)null)
                    .SetProperty(m => m.NextAttemptAt, due)
                    .SetProperty(m => m.LastError, trimmed),
                cancellationToken);

        return changed == 1;
    }

    /// <inheritdoc />
    public async Task<bool> QuarantineAsync(
        OutboxMessage claim,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var trimmed = Trim(reason);
        var now = time.GetUtcNow().UtcDateTime;

        var changed = await Owned(claim)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.QuarantinedAt, now)
                    .SetProperty(m => m.ClaimedAt, (DateTime?)null)
                    .SetProperty(m => m.ClaimToken, (Guid?)null)
                    .SetProperty(m => m.NextAttemptAt, (DateTime?)null)
                    .SetProperty(m => m.LastError, trimmed),
                cancellationToken);

        return changed == 1;
    }

    /// <inheritdoc />
    public async Task<bool> RequeueAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await context.Set<NoeliaOutboxMessage>()
            .AsNoTracking()
            .Where(m => m.Id == id && m.QuarantinedAt != null && m.DeliveredAt == null)
            .Select(m => new { m.Attempts, m.LastError, m.QuarantinedAt })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return false;
        }

        // Attempts restart so the message has a full budget again, and what it
        // had is written into LastError rather than thrown away: a message that
        // is requeued a fourth time should look like one.
        var history = Trim(
            $"Requeued after {row.Attempts} attempts; last error: {row.LastError ?? "none"}");
        var now = time.GetUtcNow().UtcDateTime;

        var changed = await context.Set<NoeliaOutboxMessage>()
            .Where(m => m.Id == id
                        && m.QuarantinedAt == row.QuarantinedAt
                        && m.DeliveredAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.QuarantinedAt, (DateTime?)null)
                    .SetProperty(m => m.NextAttemptAt, now)
                    .SetProperty(m => m.Attempts, 0)
                    .SetProperty(m => m.LastError, history),
                cancellationToken);

        if (changed == 0)
        {
            return false;
        }

        Requeued(logger ?? NullLogger<EntityFrameworkOutbox<TContext>>.Instance, id, row.Attempts);
        return true;
    }

    private IQueryable<NoeliaOutboxMessage> Claimable(DateTime now, DateTime expired) =>
        context.Set<NoeliaOutboxMessage>()
            .Where(m => m.DeliveredAt == null
                        && m.QuarantinedAt == null
                        && (m.NextAttemptAt == null || m.NextAttemptAt <= now)
                        && (m.ClaimedAt == null || m.ClaimedAt < expired));

    // The fence: the row must still carry the token this claim was given.
    private IQueryable<NoeliaOutboxMessage> Owned(OutboxMessage claim) =>
        context.Set<NoeliaOutboxMessage>()
            .Where(m => m.Id == claim.Id
                        && m.ClaimToken == claim.ClaimToken
                        && m.DeliveredAt == null);

    private static string Trim(string value) =>
        value.Length > MaxErrorLength ? value[..MaxErrorLength] : value;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} was requeued from quarantine after {Attempts} attempts")]
    private static partial void Requeued(ILogger logger, Guid messageId, int attempts);
}

/// <summary>Registers the outbox and its table.</summary>
public static class EntityFrameworkOutboxExtensions
{
    /// <summary>
    /// Records intents in <typeparamref name="TContext"/>, in the caller's
    /// transaction.
    /// </summary>
    /// <remarks>
    /// The context has to map <see cref="NoeliaOutboxMessage"/>. Call
    /// <see cref="MapNoeliaOutbox"/> from its <c>OnModelCreating</c>, and add a
    /// migration: a table nobody created is a message nobody records.
    /// </remarks>
    /// <typeparam name="TContext">The application's own context.</typeparam>
    /// <param name="services">The container.</param>
    public static IServiceCollection AddEntityFrameworkOutbox<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<EntityFrameworkOutbox<TContext>>();
        services.AddScoped<IOutbox>(p => p.GetRequiredService<EntityFrameworkOutbox<TContext>>());
        services.AddScoped<IOutboxReader>(p => p.GetRequiredService<EntityFrameworkOutbox<TContext>>());

        return services;
    }

    /// <summary>Maps the outbox table. Call from <c>OnModelCreating</c>.</summary>
    /// <param name="builder">The model being built.</param>
    /// <param name="table">The table name. Changing it later is a migration.</param>
    public static ModelBuilder MapNoeliaOutbox(
        this ModelBuilder builder,
        string table = "noelia_outbox")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        builder.Entity<NoeliaOutboxMessage>(entity =>
        {
            entity.ToTable(table);
            entity.HasKey(m => m.Id);

            entity.Property(m => m.Type).IsRequired().HasMaxLength(512);
            entity.Property(m => m.Payload).IsRequired();
            entity.Property(m => m.CorrelationId).HasMaxLength(128);
            entity.Property(m => m.LastError).HasMaxLength(MaxError);

            // The dispatcher's claim query: undelivered, not quarantined, due,
            // oldest first. Without this index it is a table scan that grows
            // with everything ever sent, because delivered rows stay.
            entity.HasIndex(m => new { m.DeliveredAt, m.QuarantinedAt, m.NextAttemptAt, m.RecordedAt });

            // Claimed rows are read back by their token; without it that read
            // scans the same ever-growing table.
            entity.HasIndex(m => m.ClaimToken);
        });

        return builder;
    }

    private const int MaxError = 500;
}
