using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Noelia.Infrastructure.Security.Authorization;

// ASP.NET's RequireClaim and RequireRole evaluate every identity of the principal, signed in or
// not. The catalogue and role policies used them, so a claim on an identity nobody signed in
// satisfied them (ADR-0007, #59). The two requirements below mean exactly what RequireClaim and
// RequireRole meant, applied to the signed-in identities only. They are internal: policy names
// and the public API stay as they were.

/// <summary>
/// <c>RequireClaim(type, value)</c> as the framework defines it (claim type case-insensitive,
/// value exact, no wildcard, no catalogue role), over signed-in identities only.
/// </summary>
internal sealed class SignedInClaimRequirement(string claimType, string value) : IAuthorizationRequirement
{
    public string ClaimType { get; } = claimType;

    public string Value { get; } = value;
}

/// <summary>
/// <c>RequireRole(roles)</c> as the framework defines it, over signed-in identities only. The
/// role is looked up per identity by that identity's own <c>RoleClaimType</c>, so schemes with
/// their own role claim (OIDC) keep working.
/// </summary>
internal sealed class SignedInRoleRequirement(IReadOnlyList<string> roles) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Roles { get; } = roles;
}

internal sealed class SignedInClaimHandler : AuthorizationHandler<SignedInClaimRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SignedInClaimRequirement requirement)
    {
        var signedIn = AuthenticatedIdentities.Of(context.User);
        if (signedIn.Claims.Any(claim =>
                string.Equals(claim.Type, requirement.ClaimType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(claim.Value, requirement.Value, StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class SignedInRoleHandler : AuthorizationHandler<SignedInRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SignedInRoleRequirement requirement)
    {
        var signedIn = AuthenticatedIdentities.Of(context.User);
        if (requirement.Roles.Any(signedIn.IsInRole))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal static class SignedInPolicyExtensions
{
    /// <summary>Drop-in for <c>RequireClaim</c>: the same meaning, signed-in identities only.</summary>
    internal static AuthorizationPolicyBuilder RequireSignedInClaim(
        this AuthorizationPolicyBuilder policy, string claimType, string value) =>
        policy.RequireAuthenticatedUser().AddRequirements(new SignedInClaimRequirement(claimType, value));

    /// <summary>Drop-in for <c>RequireRole</c>: the same meaning, signed-in identities only.</summary>
    internal static AuthorizationPolicyBuilder RequireSignedInRole(
        this AuthorizationPolicyBuilder policy, params string[] roles) =>
        policy.RequireAuthenticatedUser().AddRequirements(new SignedInRoleRequirement(roles));

    /// <summary>
    /// Registers the two handlers. Both policy registrations call this, because either may be
    /// the only one an application uses; a requirement without its handler could never succeed.
    /// </summary>
    internal static IServiceCollection AddSignedInPolicyHandlers(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, SignedInClaimHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, SignedInRoleHandler>());
        return services;
    }
}
