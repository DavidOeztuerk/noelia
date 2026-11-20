using Noelia.Infrastructure.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Noelia.Infrastructure.Security;

public static class AuthorizationExtensions
{
    /// <summary>
    /// Registers the role policies, the account-state policies, and one policy
    /// per permission in the application's catalogue.
    /// </summary>
    /// <remarks>
    /// Call <c>AddPermissionCatalog</c> first; the permission policies are
    /// derived from the catalogue registered at that point. Without a catalogue
    /// only the role and account-state policies are registered.
    /// </remarks>
    public static IServiceCollection AddNoeliaAuthorization(this IServiceCollection services)
    {
        var catalog = services
            .FirstOrDefault(d => d.ServiceType == typeof(IPermissionCatalog))?
            .ImplementationInstance as IPermissionCatalog
            ?? PermissionCatalog.Empty;

        services.AddAuthorization(options =>
        {
            options.AddPolicy(Policies.RequireAdminRole, policy =>
                policy.RequireSignedInRole(Roles.Admin, Roles.SuperAdmin));

            options.AddPolicy(Policies.RequireModeratorRole, policy =>
                policy.RequireSignedInRole(Roles.Moderator, Roles.Admin, Roles.SuperAdmin));

            options.AddPolicy(Policies.RequireUserRole, policy =>
                policy.RequireSignedInRole(Roles.User, Roles.Moderator, Roles.Admin, Roles.SuperAdmin));

            // The handlers insist on a signed-in person themselves; RequireAuthenticatedUser is the
            // second, independent line (ADR-0007).
            options.AddPolicy(Policies.RequireVerifiedEmail, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new EmailVerifiedRequirement()));

            options.AddPolicy(Policies.RequireActiveAccount, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new ActiveAccountRequirement()));

            foreach (var permission in catalog.AllPermissions)
            {
                options.AddPolicy(permission, policy => policy.RequireSignedInClaim("permission", permission));
            }
        });

        services.AddSignedInPolicyHandlers();
        services.AddScoped<IAuthorizationHandler, ResourceOwnerHandler>();
        services.AddScoped<IAuthorizationHandler, EmailVerifiedHandler>();
        services.AddScoped<IAuthorizationHandler, ActiveAccountHandler>();

        return services;
    }
}
