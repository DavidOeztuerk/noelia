using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// Decides <see cref="ServiceCallerRequirement"/> from the identity the service
/// scheme authenticated, and from nothing else.
/// </summary>
/// <remarks>
/// Never <c>User.FindFirst</c>: a principal that merges a person's identity with a
/// service identity (an endpoint asking for both) would answer from whichever came
/// first, and a person's token may carry a <c>client_id</c> the issuer was told to
/// put there. The identity is picked by the authentication type the service scheme
/// stamps on it.
/// </remarks>
internal sealed class ServiceCallerHandler : AuthorizationHandler<ServiceCallerRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ServiceCallerRequirement requirement)
    {
        var caller = ServiceCallerIdentity.CallerOf(context.User);
        if (caller is not null && requirement.Callers.Contains(caller))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal static class ServiceCallerIdentity
{
    /// <summary>The authenticated service identity of the principal, if it has one.</summary>
    internal static ClaimsIdentity? Of(ClaimsPrincipal? principal) =>
        principal?.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType, ServiceTokenDefaults.AuthenticationScheme, StringComparison.Ordinal));

    /// <summary>The verified <c>client_id</c> of the service identity, or null.</summary>
    internal static string? CallerOf(ClaimsPrincipal? principal) =>
        Of(principal)?.FindFirst(ServiceTokenDefaults.CallerClaim)?.Value;
}
