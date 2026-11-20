using Noelia.Abstractions.Security.Authorization;
using AuthorizationResult = Noelia.Abstractions.Security.Authorization.AuthorizationResult;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Noelia.Infrastructure.Security.ServiceTokens;
using System.Security.Claims;

namespace Noelia.Infrastructure.Security.Authorization;

/// <summary>
/// Extension methods for authorization services
/// </summary>
public static class AuthorizationExtensions
{
    /// <summary>
    /// Add resource-based authorization services
    /// </summary>
    public static IServiceCollection AddResourceAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The IResourceAuthorizationService implementation comes from a
        // provider package — AddRedisResourceAuthorization() or
        // AddInMemoryResourceAuthorization().

        // Register permission resolver
        services.AddSignedInPolicyHandlers();
        services.AddSingleton<IPermissionResolver, PermissionResolver>();

        // Register authorization handlers
        services.AddScoped<IAuthorizationHandler, ResourceAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, OwnershipAuthorizationHandler>();

        // Add authorization policies
        services.AddAuthorization(options =>
        {
            // Resource-based policies. RequireAuthenticatedUser is the second line beside the
            // handlers' own "at least one signed-in person" (ADR-0007): a policy without it would
            // rest on its handler alone.
            options.AddPolicy("ResourceRead", policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new ResourceRequirement(NoeliaActions.READ)));

            options.AddPolicy("ResourceWrite", policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new ResourceRequirement(NoeliaActions.UPDATE)));

            options.AddPolicy("ResourceDelete", policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new ResourceRequirement(NoeliaActions.DELETE)));

            options.AddPolicy("ResourceOwner", policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new OwnershipRequirement()));

            // Admin policies
            options.AddPolicy("AdminOnly", policy =>
                policy.RequireSignedInRole("Admin"));

            options.AddPolicy("SuperAdminOnly", policy =>
                policy.RequireSignedInRole("SuperAdmin"));

        });

        return services;
    }

    /// <summary>
    /// Add authorization middleware
    /// </summary>
    public static IServiceCollection AddAuthorizationMiddleware(this IServiceCollection services)
    {
        services.AddTransient<ResourceAuthorizationMiddleware>();
        return services;
    }
}

/// <summary>
/// Resource authorization requirement
/// </summary>
public class ResourceRequirement : IAuthorizationRequirement
{
    public string Action { get; }
    public string? ResourceType { get; }

    public ResourceRequirement(string action, string? resourceType = null)
    {
        Action = action;
        ResourceType = resourceType;
    }
}

/// <summary>
/// Ownership authorization requirement
/// </summary>
public class OwnershipRequirement : IAuthorizationRequirement
{
    public string? ResourceType { get; }

    public OwnershipRequirement(string? resourceType = null)
    {
        ResourceType = resourceType;
    }
}

/// <summary>
/// Resource authorization handler.
/// Supports both MVC (AuthorizationFilterContext) and Minimal API (HttpContext) resources.
/// NOTE: GetResourceDataFromContext returns null, because route data alone
/// (e.g. { Id = "..." }) is not the resource a condition reads. Conditional
/// permissions (IsConditional=true) therefore fail closed at the handler level
/// until callers pass the real domain object as the authorization resource.
/// Non-conditional permissions and ownership checks work as expected.
/// </summary>
public class ResourceAuthorizationHandler : AuthorizationHandler<ResourceRequirement>
{
    private readonly IResourceAuthorizationService _authorizationService;
    private readonly IResourceMap _resourceMap;

    public ResourceAuthorizationHandler(
        IResourceAuthorizationService authorizationService,
        IResourceMap? resourceMap = null)
    {
        _authorizationService = authorizationService;
        _resourceMap = resourceMap ?? ResourceMap.Empty;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourceRequirement requirement)
    {
        // At least one signed-in person, and the provider is asked about each on its own: a
        // person identity is never judged by the claims of another, and an identity nobody signed
        // in is not asked about at all (ADR-0007). No person is Fail(), not Succeed().
        var persons = ServiceCallerIdentity.PersonsOf(context.User).ToList();
        if (persons.Count == 0)
        {
            context.Fail();
            return;
        }

        var resourceType = requirement.ResourceType ?? GetResourceTypeFromContext(context, _resourceMap);
        var resourceData = GetResourceDataFromContext(context);

        if (string.IsNullOrEmpty(resourceType))
        {
            context.Fail();
            return;
        }

        foreach (var person in persons)
        {
            var result = await _authorizationService.AuthorizeAsync(
                new ClaimsPrincipal(person),
                resourceType,
                requirement.Action,
                resourceData);

            if (!result.Succeeded)
            {
                context.Fail();
                return;
            }
        }

        context.Succeed(requirement);
    }

    internal static string? GetResourceTypeFromContext(
        AuthorizationHandlerContext context,
        IResourceMap resourceMap)
    {
        // MVC path: extract controller name from AuthorizationFilterContext
        if (context.Resource is AuthorizationFilterContext filterContext)
        {
            if (filterContext.RouteData.Values.TryGetValue("controller", out var controller))
            {
                return controller?.ToString();
            }
        }

        // Minimal API path: extract from HttpContext endpoint metadata or route values
        var httpContext = context.Resource as HttpContext;
        if (httpContext != null)
        {
            return ResolveResourceTypeFromHttpContext(httpContext, resourceMap);
        }

        return null;
    }

    internal static string? ResolveResourceTypeFromHttpContext(
        HttpContext httpContext,
        IResourceMap resourceMap)
    {
        // 1. Check for explicit ResourceAuthorizeAttribute metadata first
        var endpoint = httpContext.GetEndpoint();
        var resourceAttr = endpoint?.Metadata.GetMetadata<ResourceAuthorizeAttribute>();
        if (!string.IsNullOrEmpty(resourceAttr?.Resource))
        {
            return resourceAttr.Resource;
        }

        // 2. Infer from typed route-value names (e.g. bookingId → Booking).
        //    This is more specific than path segments and handles multi-resource routes
        //    like /users/calendar/{userId}/sync/{bookingId} correctly.
        var routeValueType = InferResourceTypeFromRouteValues(httpContext.Request.RouteValues, resourceMap);

        // 3. Infer from path segments (e.g. /bookings/... → Booking)
        string? pathType = null;
        var path = httpContext.Request.Path.Value;
        if (!string.IsNullOrEmpty(path))
        {
            pathType = InferResourceTypeFromPath(path, resourceMap);
        }

        // 4. Reconcile: route-value inference wins; conflict → fail closed
        if (routeValueType != null && pathType != null
            && !string.Equals(routeValueType, pathType, StringComparison.Ordinal))
        {
            // Conflict between route-value-derived type and path-derived type → fail closed
            return null;
        }

        // Route-value type takes priority (more specific), then path type
        return routeValueType ?? pathType;
    }

    /// <summary>
    /// Infers a resource type from typed route-value parameter names.
    /// Maps specific params like "bookingId" → Booking, "referralId" → Match.
    /// Generic "id" and "userId" are excluded (too ambiguous).
    /// Returns null if no specific typed param found, or if multiple distinct types found.
    /// </summary>
    internal static string? InferResourceTypeFromRouteValues(
        IReadOnlyDictionary<string, object?> routeValues,
        IResourceMap resourceMap)
    {
        string? inferredType = null;

        foreach (var kvp in routeValues)
        {
            if (kvp.Value == null) continue;
            var stringValue = kvp.Value.ToString();
            if (string.IsNullOrEmpty(stringValue)) continue;

            var mappedType = resourceMap.FromRouteParameter(kvp.Key);
            if (mappedType == null) continue;

            if (inferredType == null)
            {
                inferredType = mappedType;
            }
            else if (!string.Equals(inferredType, mappedType, StringComparison.Ordinal))
            {
                // Multiple distinct resource types from route params → ambiguous → fail closed
                return null;
            }
        }

        return inferredType;
    }

    internal static string? InferResourceTypeFromPath(string path, IResourceMap resourceMap)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Scan all segments for known resource segments.
        // If multiple DISTINCT resource types are found, the path is ambiguous
        // (e.g. /users/calendar/{userId}/sync/{bookingId} has User + Booking).
        // Ambiguous paths return null → fail closed. Callers must use explicit
        // ResourceAuthorizeAttribute or typed OwnershipRequirement.
        string? firstType = null;

        for (int i = 0; i < segments.Length; i++)
        {
            var mapped = resourceMap.FromPathSegment(segments[i]);
            if (mapped != null)
            {
                if (firstType == null)
                {
                    firstType = mapped;
                }
                else if (!string.Equals(firstType, mapped, StringComparison.Ordinal))
                {
                    // Multiple distinct resource types → ambiguous → fail closed
                    return null;
                }
            }
        }

        return firstType;
    }


    private static object? GetResourceDataFromContext(AuthorizationHandlerContext context)
    {
        // Route data only provides an ID — not the domain fields (RequesterId, TargetUserId,
        // OrganizerUserId, ParticipantUserId, HostUserId) that conditional permission
        // evaluation requires. Return null so conditional permissions fail-closed.
        // Callers needing conditional authorization should pass the real domain object
        // as context.Resource directly.
        return null;
    }
}

/// <summary>
/// Ownership authorization handler.
/// Supports both MVC (AuthorizationFilterContext) and Minimal API (HttpContext) resources.
/// Uses resource-aware ID resolution: when resource type is known, prefers the matching
/// typed route param (e.g. bookingId for Booking) before falling back to generic "id".
/// </summary>
public class OwnershipAuthorizationHandler : AuthorizationHandler<OwnershipRequirement>
{
    private readonly IResourceAuthorizationService _authorizationService;
    private readonly IResourceMap _resourceMap;

    public OwnershipAuthorizationHandler(
        IResourceAuthorizationService authorizationService,
        IResourceMap? resourceMap = null)
    {
        _authorizationService = authorizationService;
        _resourceMap = resourceMap ?? ResourceMap.Empty;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OwnershipRequirement requirement)
    {
        // At least one signed-in person, each with a user id of their own, and each must own the
        // record. No person is Fail() (ADR-0007), not Succeed() over the empty set.
        var persons = ServiceCallerIdentity.PersonsOf(context.User).ToList();
        if (persons.Count == 0)
        {
            context.Fail();
            return;
        }

        var userIds = new List<string>();
        foreach (var person in persons)
        {
            var userId = person.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                context.Fail();
                return;
            }

            userIds.Add(userId);
        }

        var resourceType = requirement.ResourceType
            ?? ResourceAuthorizationHandler.GetResourceTypeFromContext(context, _resourceMap);
        var resourceId = GetResourceIdFromContext(context, _resourceMap, resourceType);

        if (string.IsNullOrEmpty(resourceType) || string.IsNullOrEmpty(resourceId))
        {
            context.Fail();
            return;
        }

        foreach (var userId in userIds)
        {
            if (!await _authorizationService.IsResourceOwnerAsync(userId, resourceType, resourceId))
            {
                context.Fail();
                return;
            }
        }

        context.Succeed(requirement);
    }

    internal static string? GetResourceIdFromContext(
        AuthorizationHandlerContext context,
        IResourceMap resourceMap,
        string? resourceType)
    {
        // MVC path
        if (context.Resource is AuthorizationFilterContext filterContext)
        {
            return ResolveResourceId(filterContext.RouteData.Values, resourceMap, resourceType);
        }

        // Minimal API path
        if (context.Resource is HttpContext httpContext)
        {
            return ResolveResourceId(httpContext.Request.RouteValues, resourceMap, resourceType);
        }

        return null;
    }

    /// <summary>
    /// Resolves a resource ID from route values using resource-aware resolution.
    /// When resourceType is known, uses the preferred param list for that type.
    /// When unknown, falls back to generic resolution with ambiguity fail-closed.
    /// </summary>
    internal static string? ResolveResourceId(
        IReadOnlyDictionary<string, object?> routeValues,
        IResourceMap resourceMap,
        string? resourceType = null)
    {
        var preferredParams = string.IsNullOrEmpty(resourceType)
            ? []
            : resourceMap.IdParametersFor(resourceType);

        // Resource-aware path: use the preferred param list for this resource type
        if (preferredParams.Count > 0)
        {
            foreach (var paramName in preferredParams)
            {
                if (routeValues.TryGetValue(paramName, out var value) && value != null)
                {
                    var stringValue = value.ToString();
                    if (!string.IsNullOrEmpty(stringValue))
                    {
                        return stringValue;
                    }
                }
            }
            // No preferred param found — fail-closed
            return null;
        }

        // Generic fallback: scan all known params, fail-closed on ambiguity
        return ResolveResourceIdGeneric(routeValues, resourceMap);
    }

    /// <summary>
    /// Generic resolution: returns a single unambiguous ID, or null if zero or multiple found.
    /// </summary>
    internal static string? ResolveResourceIdGeneric(
        IReadOnlyDictionary<string, object?> routeValues,
        IResourceMap resourceMap)
    {
        string? foundId = null;

        foreach (var paramName in resourceMap.AllIdParameters)
        {
            if (routeValues.TryGetValue(paramName, out var value) && value != null)
            {
                var stringValue = value.ToString();
                if (!string.IsNullOrEmpty(stringValue))
                {
                    if (foundId != null)
                    {
                        // Ambiguous: multiple known ID route params present — fail-closed
                        return null;
                    }
                    foundId = stringValue;
                }
            }
        }

        return foundId;
    }
}

