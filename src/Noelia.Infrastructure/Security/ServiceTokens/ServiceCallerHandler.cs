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
        principal?.Identities.FirstOrDefault(IsService);

    /// <summary>The verified <c>client_id</c> of the service identity, or null.</summary>
    internal static string? CallerOf(ClaimsPrincipal? principal) =>
        Of(principal)?.FindFirst(ServiceTokenDefaults.CallerClaim)?.Value;

    /// <summary>
    /// The signed-in person identities of the principal, in their order: every
    /// authenticated identity except the service's. The identities are shared, not copied.
    /// </summary>
    internal static IEnumerable<ClaimsIdentity> PersonsOf(ClaimsPrincipal? principal) =>
        principal?.Identities.Where(identity => identity.IsAuthenticated && !IsService(identity)) ?? [];

    /// <summary>
    /// The claim of that type on the first signed-in person identity, so that a user id in a
    /// log line or an alert comes from a signed-in person and never from an identity nobody
    /// signed in (ADR-0007). Read from that one identity, never across two.
    /// </summary>
    internal static string? FirstPersonClaim(ClaimsPrincipal? principal, string claimType) =>
        PersonsOf(principal).FirstOrDefault()?.FindFirst(claimType)?.Value;

    /// <summary>
    /// What is left of the principal for the steps that are about a person: every
    /// signed-in identity except the service's, or nothing at all on a plain door.
    /// </summary>
    /// <remarks>
    /// Revocation, the principal and the check per record all look for a person, and
    /// behind a door the policy has put a service in <c>context.User</c>. Taking the
    /// service out, rather than skipping the step when one is present, is the point:
    /// a policy naming two schemes merges a person into the same principal, and that
    /// person is still revoked, refused and asked about as ever. It also keeps
    /// <c>FindFirst</c> from reading across the two, which until 7.1.1 took <c>jti</c>
    /// and <c>iat</c> from the service token and <c>sub</c> from the person, so a
    /// revoked person went unnoticed.
    /// <para>
    /// The filter applies to every principal, with or without a service identity in it
    /// (ADR-0007, which replaces the part of ADR-0003 that handed a principal without a
    /// service identity back as it went in): an identity nobody signed in is never part
    /// of it either, so a claim it carries cannot speak for the person. A factory the
    /// consumer wrote itself is handed this principal and therefore sees only signed-in
    /// identities, never the service's.
    /// </para>
    /// </remarks>
    internal static ClaimsPrincipal PersonPartOf(ClaimsPrincipal? principal) =>
        new(PersonsOf(principal));

    private static bool IsService(ClaimsIdentity identity) =>
        identity.IsAuthenticated
        && string.Equals(
            identity.AuthenticationType, ServiceTokenDefaults.AuthenticationScheme, StringComparison.Ordinal);
}
