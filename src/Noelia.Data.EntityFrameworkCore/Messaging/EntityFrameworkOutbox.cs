using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// </remarks>
public sealed class EntityFrameworkOutbox<TContext>(TContext context, TimeProvider time)
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

        // One statement, so two dispatchers cannot both take the same row: the
        // WHERE runs at update time, and the second one matches nothing.
        var ids = await context.Set<NoeliaOutboxMessage>()
            .Where(m => m.DeliveredAt == null && (m.ClaimedAt == null || m.ClaimedAt < expired))
            .OrderBy(m => m.RecordedAt)
            .Select(m => m.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        var claimed = await context.Set<NoeliaOutboxMessage>()
            .Where(m => ids.Contains(m.Id)
                        && m.DeliveredAt == null
                        && (m.ClaimedAt == null || m.ClaimedAt < expired))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.ClaimedAt, now)
                    .SetProperty(m => m.Attempts, m => m.Attempts + 1),
                cancellationToken);

        if (claimed == 0)
        {
            return [];
        }

        return await context.Set<NoeliaOutboxMessage>()
            .Where(m => ids.Contains(m.Id) && m.ClaimedAt == now && m.DeliveredAt == null)
            .OrderBy(m => m.RecordedAt)
            .Select(m => new OutboxMessage(
                m.Id,
                m.Type,
                m.Payload,
                m.CorrelationId,
                new DateTimeOffset(m.RecordedAt, TimeSpan.Zero),
                m.Attempts))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The row stays. A delivered message is the evidence that the intent was
    /// carried out, and deleting it would leave an operator unable to answer
    /// "was this ever sent?". Pruning old rows is a separate decision, the same
    /// split as the refresh token store's purge.
    /// </remarks>
    public Task MarkDeliveredAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.Set<NoeliaOutboxMessage>()
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.DeliveredAt, time.GetUtcNow().UtcDateTime)
                    .SetProperty(m => m.ClaimedAt, (DateTime?)null)
                    .SetProperty(m => m.LastError, (string?)null),
                cancellationToken);

    /// <inheritdoc />
    public Task ReleaseAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var trimmed = reason.Length > MaxErrorLength ? reason[..MaxErrorLength] : reason;

        return context.Set<NoeliaOutboxMessage>()
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.ClaimedAt, (DateTime?)null)
                    .SetProperty(m => m.LastError, trimmed),
                cancellationToken);
    }
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

            // The dispatcher's only query: undelivered, oldest first. Without
            // this index it is a table scan that grows with everything ever
            // sent, because delivered rows stay.
            entity.HasIndex(m => new { m.DeliveredAt, m.RecordedAt });
        });

        return builder;
    }

    private const int MaxError = 500;
}
