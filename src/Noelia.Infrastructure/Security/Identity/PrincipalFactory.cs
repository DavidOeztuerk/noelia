using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Security.Identity;

/// <inheritdoc />
/// <remarks>
/// The principal is made from <b>one</b> identity: the first signed-in person identity of the
/// user (ADR-0007, #59). <c>sub</c> and <c>tenant</c> are read from that identity and never
/// across two, so "person A acting for the tenant of B" cannot come out of a user that holds
/// both, and an identity nobody signed in, which a claims transformation may have appended,
/// can neither name the person nor make them a company. The factory filters itself, so a
/// consumer who calls it directly gets the same answer as the pipeline.
/// </remarks>
public sealed class PrincipalFactory : IPrincipalFactory
{
    /// <inheritdoc />
    public PrincipalResult Create(ClaimsPrincipal? user)
    {
        var person = ServiceCallerIdentity.PersonsOf(user).FirstOrDefault();
        if (person is null)
        {
            return PrincipalResult.Anonymous();
        }

        var subjectClaim = person.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                           ?? person.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!SubjectId.TryParse(subjectClaim, out var subject))
        {
            return PrincipalResult.Invalid("The token carries no usable subject claim.");
        }

        var tenantClaim = person.FindFirst(NoeliaClaimTypes.Tenant)?.Value;

        if (tenantClaim is null)
        {
            return PrincipalResult.Success(Principal.Person(subject));
        }

        // A malformed tenant claim is rejected rather than downgraded to a
        // person, so that a broken issuer surfaces instead of silently
        // changing what the caller can see.
        if (!TenantId.TryParse(tenantClaim, out var tenant))
        {
            return PrincipalResult.Invalid("The token carries an unusable tenant claim.");
        }

        return PrincipalResult.Success(Principal.Company(subject, tenant));
    }
}
