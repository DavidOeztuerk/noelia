using Noelia.Abstractions.Security.Audit;
// Noelia.Infrastructure.Security also declares Noelia.Abstractions.Security.Audit.SecurityAuditEvent and
// SecurityEventSeverity, for the simpler ISecurityAuditLogger. An enclosing
// namespace beats a using alias in C#, so both are written out in full below.
// Merging the two audit systems is noted in the README.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Noelia.Infrastructure.Http;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Security.Audit;

/// <summary>
/// Middleware for automatic security audit logging
/// </summary>
public class SecurityAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISecurityAuditService _auditService;
    private readonly ILogger<SecurityAuditMiddleware> _logger;

    public SecurityAuditMiddleware(
        RequestDelegate next,
        ISecurityAuditService auditService,
        ILogger<SecurityAuditMiddleware> logger)
    {
        _next = next;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestInfo = await CaptureRequestInfoAsync(context);
        
        Exception? exception = null;
        
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            exception = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            
            try
            {
                await LogRequestAsync(context, requestInfo, stopwatch.ElapsedMilliseconds, exception);
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Failed to log security audit event");
            }
        }
    }

    private async Task<RequestInfo> CaptureRequestInfoAsync(HttpContext context)
    {
        var requestInfo = new RequestInfo
        {
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? "",
            QueryString = context.Request.QueryString.Value ?? "",
            UserAgent = context.Request.Headers.UserAgent.FirstOrDefault() ?? "",
            IpAddress = ClientAddress.Of(context),
            UserId = ServiceCallerIdentity.FirstPersonClaim(context.User, ClaimTypes.NameIdentifier),
            SessionId = context.Request.Headers["X-Session-Id"].FirstOrDefault(),
            RequestId = Activity.Current?.Id ?? Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow
        };

        // Capture request body for sensitive operations (with size limit)
        if (ShouldCaptureRequestBody(context))
        {
            requestInfo.RequestBody = await ReadRequestBodyAsync(context);
        }

        return requestInfo;
    }

    private async Task LogRequestAsync(
        HttpContext context, 
        RequestInfo requestInfo, 
        long durationMs,
        Exception? exception)
    {
        var shouldLog = ShouldLogRequest(context, exception);
        if (!shouldLog)
            return;

        var auditEvent = new Noelia.Abstractions.Security.Audit.SecurityAuditEvent
        {
            EventType = DetermineEventType(context, exception),
            Description = CreateEventDescription(context, requestInfo, exception),
            UserId = requestInfo.UserId,
            SessionId = requestInfo.SessionId,
            IpAddress = requestInfo.IpAddress,
            UserAgent = requestInfo.UserAgent,
            RequestId = requestInfo.RequestId,
            Severity = DetermineSeverity(context, exception),
            Category = DetermineCategory(context, exception),
            ResourceType = ExtractResourceType(context),
            ResourceId = ExtractResourceId(context),
            Action = MapHttpMethodToAction(context.Request.Method),
            Result = exception != null ? "Failure" : "Success",
            RiskScore = CalculateRequestRiskScore(context, requestInfo, exception),
            Tags = GenerateTags(context, exception),
            Metadata = new Dictionary<string, object?>
            {
                ["Method"] = requestInfo.Method,
                ["Path"] = requestInfo.Path,
                ["StatusCode"] = context.Response.StatusCode,
                ["DurationMs"] = durationMs,
                ["UserAgent"] = requestInfo.UserAgent,
                ["ContentLength"] = context.Response.ContentLength,
                ["QueryString"] = requestInfo.QueryString
            }
        };

        // Add exception details if present
        if (exception != null)
        {
            auditEvent.Metadata["ExceptionType"] = exception.GetType().Name;
            auditEvent.Metadata["ExceptionMessage"] = exception.Message;
            auditEvent.Metadata["StackTrace"] = exception.StackTrace;
        }

        // Add request body for sensitive operations
        if (!string.IsNullOrEmpty(requestInfo.RequestBody))
        {
            auditEvent.Metadata["RequestBody"] = requestInfo.RequestBody;
        }

        // Add compliance flags
        auditEvent.ComplianceFlags = DetermineComplianceFlags(context, auditEvent);

        await _auditService.LogSecurityEventAsync(auditEvent);
    }

    private static bool ShouldLogRequest(HttpContext context, Exception? exception)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        
        // Always log exceptions
        if (exception != null)
            return true;

        // Always log authentication-related requests
        if (path.Contains("/auth/") || path.Contains("/login") || path.Contains("/logout"))
            return true;

        // Always log administrative requests
        if (path.Contains("/admin/"))
            return true;

        // Log failed requests
        if (context.Response.StatusCode >= 400)
            return true;

        // Log sensitive operations
        if (IsSensitiveOperation(context))
            return true;

        // Log based on HTTP method
        if (context.Request.Method != "GET")
            return true;

        return false;
    }

    private static bool ShouldCaptureRequestBody(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        
        // Capture for authentication requests
        if (path.Contains("/auth/") || path.Contains("/login"))
            return true;

        // Capture for data modification requests
        if (context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
            return true;

        return false;
    }

    private static async Task<string?> ReadRequestBodyAsync(HttpContext context)
    {
        try
        {
            if (context.Request.ContentLength > 1024 * 1024) // 1MB limit
                return "[Request body too large to capture]";

            context.Request.EnableBuffering();
            context.Request.Body.Position = 0;

            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            // Sanitize sensitive data
            return SanitizeRequestBody(body);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeRequestBody(string body)
    {
        try
        {
            // Parse as JSON and remove sensitive fields
            var jsonDoc = JsonDocument.Parse(body);
            var sanitized = SanitizeJsonElement(jsonDoc.RootElement);
            return JsonSerializer.Serialize(sanitized);
        }
        catch
        {
            // If not JSON, return truncated version
            return body.Length > 500 ? body[..500] + "..." : body;
        }
    }

    private static object? SanitizeJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    var key = prop.Name.ToLowerInvariant();
                    if (IsSensitiveField(key))
                    {
                        obj[prop.Name] = "[REDACTED]";
                    }
                    else
                    {
                        obj[prop.Name] = SanitizeJsonElement(prop.Value);
                    }
                }
                return obj;

            case JsonValueKind.Array:
                return element.EnumerateArray().Select(SanitizeJsonElement).ToArray();

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Number:
                return element.GetDecimal();

            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.GetBoolean();

            default:
                return null;
        }
    }

    private static bool IsSensitiveField(string fieldName)
    {
        var sensitiveFields = new[]
        {
            "password", "secret", "token", "key", "credential",
            "ssn", "social", "credit", "card", "cvv", "pin"
        };

        return sensitiveFields.Any(field => fieldName.Contains(field));
    }

    private static string DetermineEventType(HttpContext context, Exception? exception)
    {
        if (exception != null)
            return "RequestException";

        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        
        return path switch
        {
            var p when p.Contains("/auth/login") => "UserLogin",
            var p when p.Contains("/auth/logout") => "UserLogout",
            var p when p.Contains("/auth/register") => "UserRegistration",
            var p when p.Contains("/admin/") => "AdminAction",
            var p when context.Response.StatusCode == 401 => "UnauthorizedAccess",
            var p when context.Response.StatusCode == 403 => "ForbiddenAccess",
            var p when context.Request.Method == "DELETE" => "DataDeletion",
            var p when context.Request.Method is "POST" or "PUT" or "PATCH" => "DataModification",
            _ => "RequestProcessed"
        };
    }

    private static string CreateEventDescription(HttpContext context, RequestInfo requestInfo, Exception? exception)
    {
        if (exception != null)
            return $"Request failed: {requestInfo.Method} {requestInfo.Path} - {exception.Message}";

        return $"Request processed: {requestInfo.Method} {requestInfo.Path} (Status: {context.Response.StatusCode})";
    }

    private static Noelia.Abstractions.Security.Audit.SecurityEventSeverity DetermineSeverity(HttpContext context, Exception? exception)
    {
        if (exception != null)
            return Noelia.Abstractions.Security.Audit.SecurityEventSeverity.High;

        return context.Response.StatusCode switch
        {
            >= 500 => Noelia.Abstractions.Security.Audit.SecurityEventSeverity.High,
            >= 400 => Noelia.Abstractions.Security.Audit.SecurityEventSeverity.Medium,
            _ => Noelia.Abstractions.Security.Audit.SecurityEventSeverity.Information
        };
    }

    private static Noelia.Abstractions.Security.Audit.SecurityEventCategory DetermineCategory(HttpContext context, Exception? exception)
    {
        if (exception != null)
            return Noelia.Abstractions.Security.Audit.SecurityEventCategory.SystemEvent;

        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        
        return path switch
        {
            var p when p.Contains("/auth/") => Noelia.Abstractions.Security.Audit.SecurityEventCategory.Authentication,
            var p when context.Response.StatusCode is 401 or 403 => Noelia.Abstractions.Security.Audit.SecurityEventCategory.Authorization,
            var p when context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE" => Noelia.Abstractions.Security.Audit.SecurityEventCategory.DataModification,
            _ => Noelia.Abstractions.Security.Audit.SecurityEventCategory.General
        };
    }

    private static bool IsSensitiveOperation(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        
        return path.Contains("/admin/") ||
               path.Contains("/user/") ||
               path.Contains("/password") ||
               path.Contains("/security");
    }

    private static string? ExtractResourceType(HttpContext context)
    {
        var routeData = context.Request.RouteValues;
        
        if (routeData.TryGetValue("controller", out var controller))
        {
            return controller?.ToString();
        }

        return null;
    }

    private static string? ExtractResourceId(HttpContext context)
    {
        var routeData = context.Request.RouteValues;
        
        if (routeData.TryGetValue("id", out var id))
        {
            return id?.ToString();
        }

        return null;
    }

    private static string MapHttpMethodToAction(string httpMethod)
    {
        return httpMethod.ToUpperInvariant() switch
        {
            "GET" => "Read",
            "POST" => "Create",
            "PUT" or "PATCH" => "Update",
            "DELETE" => "Delete",
            _ => "Unknown"
        };
    }

    private static int CalculateRequestRiskScore(HttpContext context, RequestInfo requestInfo, Exception? exception)
    {
        var baseScore = 10;

        // Increase for exceptions
        if (exception != null)
            baseScore += 30;

        // Increase for failed status codes
        baseScore += context.Response.StatusCode switch
        {
            >= 500 => 25,
            >= 400 => 15,
            _ => 0
        };

        // Increase for sensitive operations
        if (IsSensitiveOperation(context))
            baseScore += 20;

        // Increase for admin operations
        if (context.Request.Path.Value?.Contains("/admin/") == true)
            baseScore += 15;

        return Math.Min(100, baseScore);
    }

    private static List<string> GenerateTags(HttpContext context, Exception? exception)
    {
        var tags = new List<string>();

        if (exception != null)
            tags.Add("exception");

        if (context.Response.StatusCode >= 400)
            tags.Add("error");

        if (context.Request.Path.Value?.Contains("/auth/") == true)
            tags.Add("authentication");

        if (context.Request.Path.Value?.Contains("/admin/") == true)
            tags.Add("administration");

        if (IsSensitiveOperation(context))
            tags.Add("sensitive");

        return tags;
    }

    private static List<string> DetermineComplianceFlags(HttpContext context, Noelia.Abstractions.Security.Audit.SecurityAuditEvent auditEvent)
    {
        var flags = new List<string>();

        // GDPR compliance for user data access
        if (auditEvent.ResourceType?.ToLowerInvariant() == "user" ||
            context.Request.Path.Value?.Contains("/user/") == true)
        {
            flags.Add("GDPR");
        }

        // SOX compliance for financial data
        if (context.Request.Path.Value?.Contains("/payment") == true ||
            context.Request.Path.Value?.Contains("/financial") == true)
        {
            flags.Add("SOX");
        }

        // HIPAA compliance for health data (if applicable)
        if (context.Request.Path.Value?.Contains("/health") == true)
        {
            flags.Add("HIPAA");
        }

        return flags;
    }
    private class RequestInfo
    {
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string QueryString { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string? UserId { get; set; }
        public string? SessionId { get; set; }
        public string RequestId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string? RequestBody { get; set; }
    }
}

/// <summary>
/// Extension methods for using security audit middleware
/// </summary>
public static class SecurityAuditMiddlewareExtensions
{
    /// <summary>
    /// Use security audit middleware
    /// </summary>
    public static IApplicationBuilder UseSecurityAudit(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SecurityAuditMiddleware>();
    }
}