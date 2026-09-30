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
/// <para>Separate from <see cref="IOutbox"/> because the two have different readers.
/// Application code records; exactly one component delivers, and giving
/// application code the ability to mark a message sent would let a handler
/// silently drop one.</para>
///
/// <para><strong>Delivery is at-least-once.</strong> A publish that succeeded and
/// a mark that did not, or a lease that expired mid-publish, delivers a message
/// again. Consumers must deduplicate by <see cref="OutboxMessage.Id"/>.</para>
///
/// <para><strong>Claims are fenced.</strong> Every claim carries a token; a
/// completion that names a token the store no longer holds changes nothing and
/// reports <c>false</c>. A dispatcher that was slow enough to lose its lease
/// therefore cannot overwrite the outcome of the one that took over.</para>
/// </remarks>
public interface IOutboxReader
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> messages that are due.
    /// </summary>
    /// <remarks>
    /// Claiming, not reading: two dispatchers against one store must not both
    /// take the same message. An implementation that cannot claim atomically
    /// has to say so rather than hand out duplicates and hope. Only messages that
    /// are undelivered, not quarantined, past their <c>notBefore</c> and not
    /// under a live lease are taken; every message of one call shares one
    /// <see cref="OutboxMessage.ClaimToken"/>.
    /// </remarks>
    /// <param name="batchSize">The most to take in one turn.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Records that a claimed message reached the transport.</summary>
    /// <param name="claim">The message exactly as <see cref="ClaimAsync"/> returned it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// <c>true</c> when the claim was still current and the message is now
    /// delivered; <c>false</c> when it had been taken over, in which case nothing
    /// was changed.
    /// </returns>
    Task<bool> MarkDeliveredAsync(OutboxMessage claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a claimed message to the queue after a failed delivery, not to be
    /// claimed again before <paramref name="notBefore"/>.
    /// </summary>
    /// <remarks>
    /// The store does not decide the schedule — retry policy is the dispatcher's —
    /// it only refuses to hand the message out early. The reason is stored for an
    /// operator, so it must be a classification and never a payload, a secret or
    /// raw exception text.
    /// </remarks>
    /// <param name="claim">The message exactly as <see cref="ClaimAsync"/> returned it.</param>
    /// <param name="reason">A short, stable classification of the failure.</param>
    /// <param name="notBefore">The earliest moment the message may be claimed again.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>true</c> when the claim was current; <c>false</c> when it was stale and nothing changed.</returns>
    Task<bool> ReleaseAsync(
        OutboxMessage claim,
        string reason,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a claimed message out of circulation because retrying it is not
    /// expected to help.
    /// </summary>
    /// <remarks>
    /// Not a deletion: the row stays with its reason and attempt count. Until
    /// <see cref="RequeueAsync"/> is called, <see cref="ClaimAsync"/> never
    /// returns it, so a poison message cannot crowd out newer ones.
    /// </remarks>
    /// <param name="claim">The message exactly as <see cref="ClaimAsync"/> returned it.</param>
    /// <param name="reason">A short, stable classification of the failure.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>true</c> when the claim was current; <c>false</c> when it was stale and nothing changed.</returns>
    Task<bool> QuarantineAsync(
        OutboxMessage claim,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a quarantined message back in the queue, due immediately.
    /// </summary>
    /// <remarks>
    /// The manual, deliberate way out of quarantine — call it after the cause has
    /// been fixed. The attempt counter restarts so the message gets a full budget
    /// again; the count and error it had are folded into the stored last error so
    /// the history is not silently lost, and the store logs the requeue.
    /// </remarks>
    /// <param name="id">The message to resume.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>false</c> when there is no quarantined, undelivered message with that id.</returns>
    Task<bool> RequeueAsync(Guid id, CancellationToken cancellationToken = default);
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
/// <param name="ClaimToken">
/// Names the claim that produced this instance. Hand the message back
/// unchanged to <see cref="IOutboxReader.MarkDeliveredAsync"/>,
/// <see cref="IOutboxReader.ReleaseAsync"/> or
/// <see cref="IOutboxReader.QuarantineAsync"/>; a consumer has no use for it.
/// </param>
public sealed record OutboxMessage(
    Guid Id,
    string Type,
    string Payload,
    string? CorrelationId,
    DateTimeOffset RecordedAt,
    int Attempts,
    Guid ClaimToken);
