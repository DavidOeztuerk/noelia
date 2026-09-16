using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noelia.Abstractions.Security.Audit;

namespace Noelia.Infrastructure.Security;

/// <summary>
/// The always-available front door for recording a security event.
/// </summary>
/// <remarks>
/// <para>This exists because the <c>Audit</c> module is in
/// <c>UseDefaults()</c> and must compose with nothing else registered, while
/// <see cref="ISecurityAuditService"/> — the store, with querying, integrity
/// verification and export — is provider-backed. Middleware writes here and
/// does not care which of the two situations it is in.</para>
///
/// <para>Until 6.0.0 there were two parallel systems: this interface with its
/// own <c>SecurityAuditEvent</c> and <c>SecurityEventSeverity</c> in
/// <c>Noelia.Infrastructure</c>, and <see cref="ISecurityAuditService"/> with
/// types of the same names in <c>Noelia.Abstractions</c>. The severities did
/// not even agree: <c>Critical</c> was 3 on one scale and 4 on the other, so a
/// value written by one and read by the other quietly changed meaning.</para>
/// </remarks>
public interface ISecurityAuditLogger
{
    /// <summary>Records a security event.</summary>
    /// <param name="auditEvent">The event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task LogSecurityEventAsync(
        SecurityAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads recorded events back.
    /// </summary>
    /// <remarks>
    /// Requires a registered <see cref="ISecurityAuditService"/>, because
    /// reading needs somewhere the events were kept. Without one this throws
    /// rather than answering — see the implementation for why an empty list
    /// would be worse.
    /// </remarks>
    /// <param name="fromDate">Earliest timestamp, or <c>null</c>.</param>
    /// <param name="toDate">Latest timestamp, or <c>null</c>.</param>
    /// <param name="eventType">Restrict to one event type, or <c>null</c>.</param>
    /// <param name="userId">Restrict to one subject, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IEnumerable<SecurityAuditEvent>> GetSecurityEventsAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? eventType = null,
        string? userId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes to the log stream always, and to the audit store when one exists.
/// </summary>
/// <remarks>
/// <para><strong>What this did until 6.0.0.</strong> It wrote the event to the
/// logger and stopped, carrying a comment saying that a real implementation
/// would also store it. Since <c>Audit</c> is in <c>UseDefaults()</c>, that was
/// every Noelia service: security events went to the log stream and nowhere
/// that could be queried, and the composition reported a security audit trail
/// the whole time.</para>
///
/// <para>The reading half was worse. <c>GetSecurityEventsAsync</c> returned an
/// empty sequence unconditionally, so a caller asking what security events had
/// occurred was told "none" — which reads like an answer rather than like the
/// absence of a store. It now throws and names the packages that would give it
/// one, because a caller who has to be told cannot be told by silence.</para>
/// </remarks>
public class SecurityAuditLogger : ISecurityAuditLogger
{
    private readonly ILogger<SecurityAuditLogger> _logger;
    private readonly IServiceProvider _services;

    /// <summary>Takes the logger and the container the store may be in.</summary>
    /// <param name="logger">Where every event is written regardless.</param>
    /// <param name="services">
    /// Resolved lazily rather than injected, because the store is registered by
    /// a provider package during composition and this logger is built whether
    /// or not one arrived.
    /// </param>
    public SecurityAuditLogger(ILogger<SecurityAuditLogger> logger, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(services);

        _logger = logger;
        _services = services;
    }

    /// <inheritdoc />
    public async Task LogSecurityEventAsync(
        SecurityAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Always, and first. The log stream is the one destination that exists
        // in every deployment, and an event that reached neither would be a
        // security event nobody can account for.
        _logger.LogInformation(
            "Security Event: {EventType} - {Description} (User: {UserId}, Severity: {Severity})",
            auditEvent.EventType,
            auditEvent.Description,
            auditEvent.UserId,
            auditEvent.Severity);

        if (_services.GetService<ISecurityAuditService>() is { } store)
        {
            await store.LogSecurityEventAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SecurityAuditEvent>> GetSecurityEventsAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? eventType = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var store = _services.GetService<ISecurityAuditService>()
            ?? throw new InvalidOperationException(
                "Security events cannot be read back because no ISecurityAuditService is "
                + "registered. Register one — AddRedisSecurity(...) from Noelia.Redis or "
                + "AddInMemorySecurity() from Noelia.InMemory — or read them from wherever "
                + "your logs are collected. Answering with an empty sequence would report "
                + "that nothing happened.");

        return await store.GetSecurityEventsAsync(
            new SecurityAuditQuery
            {
                FromDate = fromDate,
                ToDate = toDate,
                EventType = eventType,
                UserId = userId
            },
            cancellationToken).ConfigureAwait(false);
    }
}
