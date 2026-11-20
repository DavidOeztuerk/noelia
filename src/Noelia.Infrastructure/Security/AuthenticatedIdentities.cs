using System.Security.Claims;

namespace Noelia.Infrastructure.Security;

/// <summary>
/// What of a principal may speak for rights and roles: only the identities that were
/// signed in. One answer for every check Noelia brings itself (ADR-0007, #59).
/// </summary>
/// <remarks>
/// A <see cref="ClaimsPrincipal"/> can hold several identities, and the framework's own
/// <c>Identity.IsAuthenticated</c> asks only the first while <c>FindAll</c> and <c>IsInRole</c>
/// read all of them. Together those two let a claim from an identity nobody signed in
/// (typically one a claims transformation appended without an <c>AuthenticationType</c>)
/// grant a right, or let a signed-in person be refused because such an identity stands in
/// front. Every check that reads rights or roles asks this class instead, so that no check
/// can answer differently from the others.
/// <para>
/// "Authenticated" is <see cref="ClaimsIdentity.IsAuthenticated"/>, a set
/// <c>AuthenticationType</c>. That a scheme really verified the identity behind it cannot be
/// seen from here; a consumer who sets the type on an identity no scheme checked is believed.
/// </para>
/// </remarks>
internal static class AuthenticatedIdentities
{
    /// <summary>Whether any identity of the principal is signed in, wherever it stands.</summary>
    internal static bool IsAuthenticated(ClaimsPrincipal? principal) =>
        principal is not null && principal.Identities.Any(identity => identity.IsAuthenticated);

    /// <summary>
    /// A principal made of only the signed-in identities, in their order. The identities
    /// themselves are shared, not copied, so each keeps its own <c>RoleClaimType</c>.
    /// </summary>
    internal static ClaimsPrincipal Of(ClaimsPrincipal? principal) =>
        new(principal?.Identities.Where(identity => identity.IsAuthenticated) ?? []);
}
