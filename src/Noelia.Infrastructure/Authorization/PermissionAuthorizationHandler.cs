using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Authorization;

namespace Noelia.Infrastructure.Authorization;

/// <summary>
/// Requirement for permission-based authorization
/// </summary>
public class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public string? Resource { get; }

    public PermissionRequirement(string permission, string? resource = null)
    {
        Permission = permission;
        Resource = resource;
    }
}

/// <summary>
/// Handler for permission-based authorization
/// </summary>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly ILogger<PermissionAuthorizationHandler> _logger;
    private readonly IPermissionCatalog _permissions;

    public PermissionAuthorizationHandler(
        ILogger<PermissionAuthorizationHandler> logger,
        IPermissionCatalog? permissions = null)
    {
        _logger = logger;
        _permissions = permissions ?? PermissionCatalog.Empty;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // Same rule as PermissionMiddleware: any signed-in identity, read only from the
        // signed-in ones, so that handler and middleware cannot answer differently
        // (ADR-0007, #59). Not Fail(): a service-caller or other handler may still succeed.
        if (!AuthenticatedIdentities.IsAuthenticated(context.User))
        {
            _logger.LogDebug("User is not authenticated");
            return Task.CompletedTask;
        }

        var caller = AuthenticatedIdentities.Of(context.User);
        var userId = caller.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userPermissions = GetUserPermissions(caller);

        // Check if user has the required permission
        if (HasPermission(userPermissions, requirement.Permission, requirement.Resource))
        {
            _logger.LogDebug(
                "User {UserId} has permission {Permission}",
                userId,
                requirement.Permission);
            
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogWarning(
                "User {UserId} does not have permission {Permission}",
                userId,
                requirement.Permission);
        }

        return Task.CompletedTask;
    }

    private List<string> GetUserPermissions(ClaimsPrincipal user)
    {
        var permissions = new List<string>();

        // Get permissions from claims
        var permissionClaims = user.FindAll("permission");
        permissions.AddRange(permissionClaims.Select(c => c.Value));

        // Get roles and add their permissions
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        if (roles.Any())
        {
            permissions.AddRange(_permissions.PermissionsFor(roles));
        }

        return permissions.Distinct().ToList();
    }

    private bool HasPermission(List<string> userPermissions, string requiredPermission, string? resource)
    {
        // Direct permission check
        if (userPermissions.Contains(requiredPermission))
            return true;

        // Check for resource-specific permission
        if (!string.IsNullOrEmpty(resource))
        {
            var resourcePermission = $"{requiredPermission}:{resource}";
            if (userPermissions.Contains(resourcePermission))
                return true;
        }

        // Check for wildcard permissions (e.g., "users:*" matches "users:create")
        var permissionParts = requiredPermission.Split(':');
        if (permissionParts.Length == 2)
        {
            var wildcardPermission = $"{permissionParts[0]}:*";
            if (userPermissions.Contains(wildcardPermission))
                return true;
        }

        if (userPermissions.Contains(PermissionCatalog.Wildcard))
            return true;

        return false;
    }
}

/// <summary>
/// Policy provider for dynamic permission-based policies
/// </summary>
public class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallbackPolicyProvider;
    private const string PERMISSION_PREFIX = RequirePermissionAttribute.PolicyPrefix;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallbackPolicyProvider = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
        _fallbackPolicyProvider.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
        _fallbackPolicyProvider.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(PERMISSION_PREFIX, StringComparison.OrdinalIgnoreCase))
        {
            var permission = policyName.Substring(PERMISSION_PREFIX.Length);
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();

            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallbackPolicyProvider.GetPolicyAsync(policyName);
    }
}

/// <summary>
/// Extension methods for configuring permission-based authorization
/// </summary>
public static class PermissionAuthorizationExtensions
{
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }

}