using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Authorization;
using Noelia.Infrastructure.Security.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json;

namespace Noelia.Infrastructure.Middleware;

/// <summary>
/// Rejects requests whose caller lacks the permission an endpoint requires.
/// </summary>
/// <remarks>
/// A request's requirement comes from its <see cref="RequirePermissionAttribute"/>
/// or, failing that, from the registered <see cref="IEndpointAccessPolicy"/>.
/// With neither, no permission is required and the request passes through.
/// </remarks>
public class PermissionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PermissionMiddleware> _logger;
    private readonly IPermissionCatalog _catalog;
    private readonly IEndpointAccessPolicy _policy;

    public PermissionMiddleware(
        RequestDelegate next,
        ILogger<PermissionMiddleware> logger,
        IPermissionCatalog? catalog = null,
        IEndpointAccessPolicy? policy = null)
    {
        _next = next;
        _logger = logger;
        _catalog = catalog ?? PermissionCatalog.Empty;
        _policy = policy ?? EndpointAccessPolicy.Empty;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // A WebSocket handshake cannot carry a JSON error body; answering one
        // terminates the connection instead of explaining the problem.
        if (IsRealtimeHandshake(context))
        {
            _logger.LogDebug("Skipping permission check for handshake: {Path}", context.Request.Path);
            await _next(context);
            return;
        }

        if (_policy.IsPublic(context))
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            await _next(context);
            return;
        }

        // Any signed-in identity counts, wherever it stands: asking only the first one
        // (Identity.IsAuthenticated) refused a signed-in person whenever an unsigned
        // identity was attached in front of them (ADR-0007, #59).
        if (!AuthenticatedIdentities.IsAuthenticated(context.User))
        {
            await HandleUnauthorized(context, "User is not authenticated");
            return;
        }

        var requiredPermission = GetRequiredPermission(context);
        if (string.IsNullOrEmpty(requiredPermission))
        {
            await _next(context);
            return;
        }

        // From here on only signed-in identities speak: rights, roles, the wildcards and
        // the user id for log and alert. An identity nobody signed in (a claims
        // transformation appended it without an AuthenticationType) lends none of them.
        var caller = AuthenticatedIdentities.Of(context.User);
        var userPermissions = GetUserPermissions(caller);
        if (HasPermission(userPermissions, requiredPermission))
        {
            await _next(context);
            return;
        }

        var userId = caller.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        _logger.LogWarning(
            "User {UserId} attempted to access {Path} without permission {Permission}",
            userId,
            context.Request.Path,
            requiredPermission);

        await NotifySecurityAsync(context, userId, requiredPermission, userPermissions);
        await HandleForbidden(context, $"Missing required permission: {requiredPermission}");
    }

    /// <summary>
    /// Whether the request is a realtime transport handshake. Covers WebSocket
    /// upgrades and the SignalR negotiate call, both of which are protocol
    /// conventions rather than knowledge about any particular application.
    /// </summary>
    private static bool IsRealtimeHandshake(HttpContext context) =>
        context.WebSockets.IsWebSocketRequest
        || (context.Request.Path.Value?.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) ?? false);

    private string? GetRequiredPermission(HttpContext context)
    {
        var attribute = context.GetEndpoint()?.Metadata.GetMetadata<RequirePermissionAttribute>();

        return attribute?.Permission ?? _policy.RequiredPermission(context);
    }

    private List<string> GetUserPermissions(ClaimsPrincipal user)
    {
        var permissions = user.FindAll("permission").Select(c => c.Value).ToList();

        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        if (roles.Count > 0)
        {
            permissions.AddRange(_catalog.PermissionsFor(roles));
        }

        return permissions.Distinct().ToList();
    }

    private static bool HasPermission(List<string> userPermissions, string requiredPermission)
    {
        if (userPermissions.Contains(requiredPermission))
        {
            return true;
        }

        if (userPermissions.Contains(PermissionCatalog.Wildcard))
        {
            return true;
        }

        // "users:*" satisfies "users:create".
        var parts = requiredPermission.Split(':');

        return parts.Length == 2 && userPermissions.Contains($"{parts[0]}:*");
    }

    private async Task NotifySecurityAsync(
        HttpContext context,
        string? userId,
        string requiredPermission,
        List<string> userPermissions)
    {
        try
        {
            var alerts = context.RequestServices.GetService<ISecurityAlertService>();
            if (alerts is null)
            {
                return;
            }

            await alerts.SendAlertAsync(
                SecurityAlertLevel.High,
                SecurityAlertType.UnauthorizedAccessAttempt,
                "Unauthorized Access Attempt",
                $"User attempted to access {context.Request.Path} without required permission: {requiredPermission}",
                new Dictionary<string, object>
                {
                    ["UserId"] = userId ?? "unknown",
                    ["Endpoint"] = context.Request.Path.Value ?? "",
                    ["Method"] = context.Request.Method,
                    ["RequiredPermission"] = requiredPermission,
                    ["UserPermissions"] = userPermissions,
                    ["IpAddress"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    ["UserAgent"] = context.Request.Headers.UserAgent.ToString()
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send security alert for unauthorized access attempt");
        }
    }

    private static Task HandleUnauthorized(HttpContext context, string message) =>
        WriteProblem(context, StatusCodes.Status401Unauthorized, message);

    private static Task HandleForbidden(HttpContext context, string message) =>
        WriteProblem(context, StatusCodes.Status403Forbidden, message);

    private static Task WriteProblem(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(new
        {
            success = false,
            message,
            data = (object?)null,
            errors = new[] { message }
        });

        return context.Response.WriteAsync(body);
    }
}

public static class PermissionMiddlewareExtensions
{
    public static IApplicationBuilder UsePermissionMiddleware(this IApplicationBuilder builder) =>
        builder.UseMiddleware<PermissionMiddleware>();
}
