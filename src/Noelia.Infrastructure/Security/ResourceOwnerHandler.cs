using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Noelia.Infrastructure.Security.ServiceTokens;
using System.Security.Claims;

namespace Noelia.Infrastructure.Security;

/// <summary>
/// Handler for resource ownership authorization
/// </summary>
/// <remarks>
/// At least one signed-in person identity has to be there and every one of them has to pass on
/// its own: owner by its own user id, or admin by its own roles (ADR-0007). No person at all
/// fails the requirement, also without <c>RequireAuthenticatedUser</c> in front.
/// </remarks>
public class ResourceOwnerHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<ResourceOwnerHandler> logger)
    : AuthorizationHandler<ResourceOwnerRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<ResourceOwnerHandler> _logger = logger;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourceOwnerRequirement requirement)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            context.Fail();
            return Task.CompletedTask;
        }

        var persons = ServiceCallerIdentity.PersonsOf(context.User).ToList();
        if (persons.Count == 0)
        {
            _logger.LogWarning("Resource owner check failed: no signed-in person");
            context.Fail();
            return Task.CompletedTask;
        }

        foreach (var person in persons)
        {
            if (!IsOwnerOrAdmin(person, httpContext, requirement))
            {
                context.Fail();
                return Task.CompletedTask;
            }
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    private bool IsOwnerOrAdmin(ClaimsIdentity person, HttpContext httpContext, ResourceOwnerRequirement requirement)
    {
        var userId = person.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Resource owner check failed: No user ID found in claims");
            return false;
        }

        // Check if user is admin (admins can access all resources). The roles of this identity
        // alone, read through its own RoleClaimType.
        var alone = new ClaimsPrincipal(person);
        if (alone.IsInRole(Roles.Admin) || alone.IsInRole(Roles.SuperAdmin))
        {
            return true;
        }

        // Get resource ID from route parameters
        var resourceId = httpContext.Request.RouteValues[requirement.ResourceIdParameter]?.ToString();

        if (string.IsNullOrEmpty(resourceId))
        {
            _logger.LogWarning("Resource owner check failed: No resource ID found in route");
            return false;
        }

        // Check if the user owns the resource
        // Note: In a real implementation, you might need to query the database
        // For now, we'll use a simple check if the resource ID matches user ID
        if (resourceId.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _logger.LogWarning("Resource owner check failed: User {UserId} attempted to access resource {ResourceId}",
            userId, resourceId);
        return false;
    }
}
