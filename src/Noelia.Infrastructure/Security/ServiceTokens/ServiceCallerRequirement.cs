using Microsoft.AspNetCore.Authorization;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// The requirement that the calling service is one of a named list.
/// </summary>
/// <remarks>
/// Satisfied only by the identity the <c>NoeliaService</c> scheme authenticated
/// and only by its <c>client_id</c>; see <c>ServiceCallerHandler</c>. There is no
/// way to say "any service" and no role standing in for one: a door that lets
/// every service in is a door every compromised service can use.
/// </remarks>
public sealed class ServiceCallerRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Names the services that may call.
    /// </summary>
    /// <param name="callers">
    /// Client ids as the issuer knows them: lower case, starting with a letter,
    /// up to 48 letters, digits and hyphens.
    /// </param>
    /// <exception cref="ArgumentException">The list is empty or an id is malformed.</exception>
    public ServiceCallerRequirement(IEnumerable<string> callers)
    {
        ArgumentNullException.ThrowIfNull(callers);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caller in callers)
        {
            if (!ServiceTokenFormat.IsIdentifier(caller))
            {
                throw new ArgumentException(
                    "A caller is a client id: lower case, starting with a letter, up to 48 letters, "
                    + "digits and hyphens. There is no wildcard.",
                    nameof(callers));
            }

            set.Add(caller);
        }

        if (set.Count == 0)
        {
            throw new ArgumentException(
                "A door that names no caller opens for nobody. Name the services that may call it.",
                nameof(callers));
        }

        Callers = set;
    }

    private ServiceCallerRequirement() => Callers = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The requirement nobody satisfies. The fallback of a group carries it, so
    /// that a request to a path with no door takes the same route through the
    /// same scheme and the same result handler as a refused request to a door.
    /// </summary>
    internal static ServiceCallerRequirement Nobody { get; } = new();

    /// <summary>The service ids that may call.</summary>
    public IReadOnlySet<string> Callers { get; }
}
