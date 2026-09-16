namespace Noelia.Abstractions.Messaging;

/// <summary>
/// Records an intent to publish, in the transaction that caused it.
/// </summary>
/// <remarks>
/// <para><see cref="IEventBus"/> promises that a transport accepted an event and
/// nothing more. Between committing a change and publishing the event about it
/// there is a gap, and a process that dies in that gap leaves a system where
/// the change happened and nobody was told. Retrying the publish instead moves
/// the gap rather than closing it: now the publish can succeed and the commit
/// fail, and listeners hear about something that never happened.</para>
///
/// <para>An outbox closes it by making the intent part of the same commit. The
/// row and the change succeed together or not at all, and a separate reader
/// delivers what was committed. Delivery becomes at-least-once — which is why
/// <see cref="OutboxMessage.Id"/> travels with the message, so a consumer can
/// recognise one it has already handled.</para>
///
/// <para><strong>This port records; it does not deliver.</strong> Delivery is a
/// loop with its own schedule, its own failure handling and its own operational
/// questions, and it belongs to whoever runs the service.
/// <see cref="IOutboxReader"/> is what a dispatcher reads.</para>
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Writes the intent to publish <paramref name="event"/>.
    /// </summary>
    /// <remarks>
    /// Enlists in the caller's transaction and does not commit. An
    /// implementation that committed here would defeat the point: the row would
    /// survive a rolled-back change and announce something that never happened.
    /// </remarks>
    /// <typeparam name="TEvent">The event type, used as the message's name.</typeparam>
    /// <param name="event">What to publish once the transaction commits.</param>
    /// <param name="correlationId">Ties the delivery back to the request that caused it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Guid> RecordAsync<TEvent>(
        TEvent @event,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        where TEvent : class;
}

/// <summary>What a dispatcher needs to deliver what was recorded.</summary>
/// <remarks>
/// Separate from <see cref="IOutbox"/> because the two have different readers.
/// Application code records; exactly one component delivers, and giving
/// application code the ability to mark a message sent would let a handler
/// silently drop one.
/// </remarks>
public interface IOutboxReader
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> undelivered messages.
    /// </summary>
    /// <remarks>
    /// Claiming, not reading: two dispatchers against one store must not both
    /// take the same message. An implementation that cannot claim atomically
    /// has to say so rather than hand out duplicates and hope.
    /// </remarks>
    /// <param name="batchSize">The most to take in one turn.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Records that a message reached the transport.</summary>
    /// <param name="id">The message that was delivered.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task MarkDeliveredAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a claimed message to the queue after a failed delivery.
    /// </summary>
    /// <remarks>
    /// With the reason, because an operator looking at a message that has
    /// failed nine times needs to know whether the broker was down or the
    /// payload is unroutable — those call for different actions.
    /// </remarks>
    /// <param name="id">The message that did not reach the transport.</param>
    /// <param name="reason">What the transport said, in one line.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task ReleaseAsync(Guid id, string reason, CancellationToken cancellationToken = default);
}

/// <summary>One recorded intent, as a dispatcher sees it.</summary>
/// <param name="Id">
/// Travels with the message. Delivery is at-least-once, so a consumer needs
/// something stable to recognise a repeat by.
/// </param>
/// <param name="Type">
/// The event's type name, as the recorder saw it. A dispatcher uses it to
/// deserialise; nothing else should read it, because it is a name in someone
/// else's assembly.
/// </param>
/// <param name="Payload">The event, serialised.</param>
/// <param name="CorrelationId">The request that caused it, where there was one.</param>
/// <param name="RecordedAt">When the transaction that wrote it committed.</param>
/// <param name="Attempts">
/// How many times delivery has been tried. An operator reads this to tell a
/// slow broker from a message nothing will ever accept.
/// </param>
public sealed record OutboxMessage(
    Guid Id,
    string Type,
    string Payload,
    string? CorrelationId,
    DateTimeOffset RecordedAt,
    int Attempts);
