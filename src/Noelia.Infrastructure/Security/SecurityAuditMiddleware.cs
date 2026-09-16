using Noelia.Abstractions.Security.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Noelia.Infrastructure.Security;

/// <summary>
/// Security audit middleware
/// </summary>
public class SecurityAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISecurityAuditLogger _auditLogger;
    private readonly ILogger<SecurityAuditMiddleware> _logger;

    public SecurityAuditMiddleware(
        RequestDelegate next,
        ISecurityAuditLogger auditLogger,
        ILogger<SecurityAuditMiddleware> logger)
    {
        _next = next;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var shouldAudit = ShouldAuditRequest(context);

        if (shouldAudit)
        {
            await LogSecurityEventAsync(context, "RequestStarted");
        }

        await _next(context);

        if (shouldAudit)
        {
            await LogSecurityEventAsync(context, "RequestCompleted");
        }
    }

    private static bool ShouldAuditRequest(HttpContext context)
    {
        // Audit authentication-related endpoints and sensitive operations
        var path = context.Request.Path.Value?.ToLowerInvariant();

        return path?.Contains("/auth/") == true ||
               path?.Contains("/admin/") == true ||
               context.Response.StatusCode == 401 ||
               context.Response.StatusCode == 403;
    }

    private async Task LogSecurityEventAsync(HttpContext context, string eventType)
    {
        try
        {
            var auditEvent = new SecurityAuditEvent
            {
                EventType = eventType,
                Description = $"{context.Request.Method} {context.Request.Path}",
                UserId = context.User?.Identity?.Name,
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                UserAgent = context.Request.Headers.UserAgent.ToString(),
                Metadata = new Dictionary<string, object?>
                {
                    ["Method"] = context.Request.Method,
                    ["Path"] = context.Request.Path.Value,
                    ["StatusCode"] = context.Response.StatusCode,
                    ["ContentLength"] = context.Response.ContentLength
                },
                Severity = GetSeverityFromStatusCode(context.Response.StatusCode)
            };

            await _auditLogger.LogSecurityEventAsync(auditEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log security audit event");
        }
    }

    private static SecurityEventSeverity GetSeverityFromStatusCode(int statusCode)
    {
        return statusCode switch
        {
            // On the surviving scale. The scale this replaced had Warning and
            // Error where this has Low through High, and put Critical at a
            // different number — so these two lines are a mapping, not a rename.
            >= 500 => SecurityEventSeverity.High,
            401 or 403 => SecurityEventSeverity.Medium,
            _ => SecurityEventSeverity.Information
        };
    }
}
