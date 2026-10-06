using System.Security.Claims;

namespace Noelia.Infrastructure.Security;

/// <summary>
/// The names under which a session travels in an access token.
/// </summary>
/// <remarks>
/// The issuer writes <see cref="Issued"/> and every reader goes through
/// <see cref="Read"/>, so the two cannot drift apart again: until 7.0.1 the
/// issuer wrote <c>session_id</c> while the revocation check asked for
/// <c>sid</c>, and <c>RevokeSessionAsync</c> never reached a token.
/// </remarks>
public static class SessionClaims
{
    /// <summary>The claim Noelia's own issuer writes. Consumers depend on it.</summary>
    public const string Issued = "session_id";

    /// <summary>The OIDC standard name, accepted from other issuers.</summary>
    public const string Standard = "sid";

    /// <summary>The session of a principal, from <see cref="Issued"/> or else <see cref="Standard"/>.</summary>
    public static string? Read(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirst(Issued)?.Value ?? principal.FindFirst(Standard)?.Value;
    }
}
