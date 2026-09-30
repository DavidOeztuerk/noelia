namespace Noelia.Data.EntityFrameworkCore.Messaging;

/// <summary>
/// One recorded intent, as a row.
/// </summary>
/// <remarks>
/// Infrastructure, like the refresh token table: it holds what the mechanism
/// needs, not what the application means. Nothing in the application's own
/// model has to know it exists.
/// </remarks>
public sealed class NoeliaOutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>The event's type name, as the recorder saw it.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The event, serialised.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>The request that caused it, where there was one.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Stored as UTC <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <remarks>
    /// SQLite cannot order by a <c>DateTimeOffset</c>, and a dispatcher that
    /// cannot order by this column delivers in an order nobody chose.
    /// </remarks>
    public DateTime RecordedAt { get; set; }

    /// <summary>When a dispatcher took it, or null while it is free.</summary>
    /// <remarks>
    /// A claim, not a lock. A dispatcher that dies holding one leaves it set
    /// forever, so the claim expires: <see cref="EntityFrameworkOutbox{TContext}"/>
    /// treats anything claimed longer than its lease as free again. Losing a
    /// dispatcher must not mean losing a message.
    /// </remarks>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>
    /// Identifies the claim that currently owns the row, or null while it is free.
    /// </summary>
    /// <remarks>
    /// Fencing. A dispatcher whose lease ran out and whose message was claimed by
    /// another one still holds a reference to it; without a token its late
    /// <c>Release</c> or <c>MarkDelivered</c> would overwrite the newer claim's
    /// outcome. Every completion names the token it was given, and a row that
    /// carries a different one is left alone.
    /// </remarks>
    public Guid? ClaimToken { get; set; }

    /// <summary>
    /// The earliest moment the message may be claimed again, or null when it is due.
    /// </summary>
    /// <remarks>
    /// This is what stops a message nothing will accept from being retried in a
    /// tight loop and from crowding newer messages out of every batch. The
    /// schedule itself is the dispatcher's; the store only honours it.
    /// </remarks>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// When the dispatcher gave up on the message, or null while it is still in play.
    /// </summary>
    /// <remarks>
    /// A quarantined message is never claimed. Only an explicit
    /// <see cref="EntityFrameworkOutbox{TContext}.RequeueAsync"/> puts it back,
    /// so the decision to try again is always a person's and is on record.
    /// </remarks>
    public DateTime? QuarantinedAt { get; set; }

    /// <summary>When it reached the transport, or null while it has not.</summary>
    public DateTime? DeliveredAt { get; set; }

    /// <summary>How many times delivery has been tried.</summary>
    public int Attempts { get; set; }

    /// <summary>What the transport said last time it refused, in one line.</summary>
    /// <remarks>
    /// Truncated on write. A transport that answers with a page of text would
    /// otherwise put a page of text in every row, and the useful part is
    /// always at the front.
    /// </remarks>
    public string? LastError { get; set; }
}
