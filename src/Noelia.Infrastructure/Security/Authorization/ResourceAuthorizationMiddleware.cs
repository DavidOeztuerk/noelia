using Noelia.Abstractions.Security.Authorization;
using Noelia.Infrastructure.Security.ServiceTokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json;

namespace Noelia.Infrastructure.Security.Authorization;

/// <summary>
/// Middleware for automatic resource authorization
/// </summary>
/// <remarks>
/// A right is per door and not per record (ADR-0002), and the list of a door has
/// already decided by the time this runs, so the identity the service scheme
/// authenticated is never put to the service. A person beside it is (ADR-0003).
/// <para>
/// Each signed-in person identity is put to the provider on its own and every one must pass;
/// an identity nobody signed in is never asked about, and an exception in the provider
/// refuses the request rather than letting it through (ADR-0007). This step reads neither
/// <c>jti</c> nor <c>iat</c>, so a person without a <c>jti</c> (a cookie) is not refused here.
/// </para>
/// </remarks>
public class ResourceAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IResourceAuthorizationService _authorizationService;
    private readonly ILogger<ResourceAuthorizationMiddleware> _logger;
    private readonly IResourceMap _resourceMap;

    public ResourceAuthorizationMiddleware(
        RequestDelegate next,
        IResourceAuthorizationService authorizationService,
        ILogger<ResourceAuthorizationMiddleware> logger,
        IResourceMap? resourceMap = null)
    {
        _next = next;
        _authorizationService = authorizationService;
        _logger = logger;
        _resourceMap = resourceMap ?? ResourceMap.Empty;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Every signed-in person identity is asked about on its own and every one must pass
        // (ADR-0007): a principal can hold two, and the record of one is not the other's.
        // Skip when there is none; a door leaves a principal with no identity at all.
        var persons = ServiceCallerIdentity.PersonsOf(context.User).ToList();
        if (persons.Count == 0)
        {
            await _next(context);
            return;
        }

        // Skip for endpoints that don't require resource authorization
        if (ShouldSkipAuthorization(context))
        {
            await _next(context);
            return;
        }

        foreach (var identity in persons)
        {
            var person = new ClaimsPrincipal(identity);
            AuthorizationResult authResult;
            try
            {
                authResult = await CheckResourceAuthorizationAsync(context, person);
            }
            catch (Exception ex)
            {
                // Fail closed (ADR-0007): a provider that throws is not a provider that said yes.
                // Until 7.2.0 this branch let the request through "so as not to block valid requests".
                _logger.LogError(ex, "Error during resource authorization check; refusing");
                authResult = AuthorizationResult.Fail("The resource authorization check could not be completed");
            }

            if (!authResult.Succeeded)
            {
                _logger.LogWarning(
                    "Resource authorization failed for user {UserId} on {Method} {Path}: {Reasons}",
                    person.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    context.Request.Method,
                    context.Request.Path,
                    string.Join(", ", authResult.FailureReasons));

                await HandleAuthorizationFailure(context, authResult);
                return;
            }
        }

        await _next(context);
    }

    private async Task<AuthorizationResult> CheckResourceAuthorizationAsync(HttpContext context, ClaimsPrincipal person)
    {
        var resourceInfo = ExtractResourceInformation(context);
        
        if (resourceInfo == null)
        {
            return AuthorizationResult.Success(); // No resource information available
        }

        var action = MapHttpMethodToAction(context.Request.Method);
        
        return await _authorizationService.AuthorizeAsync(
            person,
            resourceInfo.ResourceType,
            action,
            resourceInfo.ResourceData);
    }

    private ResourceInformation? ExtractResourceInformation(HttpContext context)
    {
        var routeData = context.Request.RouteValues;
        
        // Extract controller name as resource type
        if (!routeData.TryGetValue("controller", out var controllerObj))
        {
            return null;
        }

        var controller = controllerObj?.ToString();
        if (string.IsNullOrEmpty(controller))
        {
            return null;
        }

        // Map controller names to resource types
        var resourceType = MapControllerToResourceType(controller);
        
        // Extract resource ID if available
        var resourceData = new Dictionary<string, object?>();
        
        if (routeData.TryGetValue("id", out var id))
        {
            resourceData["Id"] = id;
        }

        // Extract additional route parameters
        foreach (var kvp in routeData.Where(kvp => kvp.Key != "controller" && kvp.Key != "action"))
        {
            resourceData[kvp.Key] = kvp.Value;
        }

        return new ResourceInformation
        {
            ResourceType = resourceType,
            ResourceData = resourceData.Any() ? resourceData : null
        };
    }

    /// <summary>
    /// The resource behind a controller name. Falls back to the controller name
    /// itself, which is the convention when no mapping is registered.
    /// </summary>
    private string MapControllerToResourceType(string controller) =>
        _resourceMap.FromPathSegment(controller) ?? controller;

    private static string MapHttpMethodToAction(string httpMethod)
    {
        return httpMethod.ToUpperInvariant() switch
        {
            "GET" => NoeliaActions.READ,
            "POST" => NoeliaActions.CREATE,
            "PUT" or "PATCH" => NoeliaActions.UPDATE,
            "DELETE" => NoeliaActions.DELETE,
            _ => NoeliaActions.READ
        };
    }

    private static bool ShouldSkipAuthorization(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant();
        
        // Skip for authentication endpoints
        if (path?.Contains("/auth/") == true ||
            path?.Contains("/login") == true ||
            path?.Contains("/register") == true ||
            path?.Contains("/logout") == true)
        {
            return true;
        }

        // Skip for health checks and metrics
        if (path?.Contains("/health") == true ||
            path?.Contains("/metrics") == true ||
            path?.Contains("/swagger") == true)
        {
            return true;
        }

        // Skip for public endpoints
        if (path?.Contains("/public/") == true)
        {
            return true;
        }

        return false;
    }

    private async Task HandleAuthorizationFailure(HttpContext context, AuthorizationResult authResult)
    {
        context.Response.StatusCode = 403; // Forbidden
        context.Response.ContentType = "application/json";

        var errorResponse = new
        {
            error = "authorization_failed",
            message = "You do not have permission to access this resource",
            details = authResult.FailureReasons,
            timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }

    private class ResourceInformation
    {
        public string ResourceType { get; set; } = string.Empty;
        public object? ResourceData { get; set; }
    }
}

/// <summary>
/// Extension methods for using resource authorization middleware
/// </summary>
public static class ResourceAuthorizationMiddlewareExtensions
{
    /// <summary>
    /// Use resource authorization middleware
    /// </summary>
    public static IApplicationBuilder UseResourceAuthorization(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ResourceAuthorizationMiddleware>();
    }
}

/// <summary>
/// Attributes for resource authorization
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ResourceAuthorizeAttribute : Attribute
{
    public string? Resource { get; set; }
    public string? Action { get; set; }
    public bool RequireOwnership { get; set; }

    public ResourceAuthorizeAttribute(string? resource = null, string? action = null)
    {
        Resource = resource;
        Action = action;
    }
}

/// <summary>
/// Attribute to skip resource authorization
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class SkipResourceAuthorizationAttribute : Attribute
{
}

/// <summary>
/// Attribute to require resource ownership
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireOwnershipAttribute : Attribute
{
    public string? ResourceType { get; set; }

    public RequireOwnershipAttribute(string? resourceType = null)
    {
        ResourceType = resourceType;
    }
}