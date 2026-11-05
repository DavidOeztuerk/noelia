using System.Diagnostics.CodeAnalysis;

namespace Noelia.Core.Identity;

/// <summary>
/// Access to the principal of the current request.
/// </summary>
public interface ICurrentPrincipal
{
    /// <summary>
    /// The person behind the request, or <c>null</c> when no person stands behind it.
    /// </summary>
    /// <remarks>
    /// <c>null</c> on an anonymous request, and on a service-to-service door: a
    /// service is authenticated but it is not a person, and nothing here may take it
    /// for one. Should a request carry a person beside the service, this is that
    /// person. The caller of a door is in <c>HttpContext.User</c>.
    /// </remarks>
    Principal? Current { get; }
}

public static class CurrentPrincipalExtensions
{
    /// <summary>Whether a person stands behind the request.</summary>
    /// <remarks>
    /// False on a service-to-service door, although that request is authenticated.
    /// <c>HttpContext.User</c> answers the other question.
    /// </remarks>
    public static bool IsAuthenticated(this ICurrentPrincipal accessor) =>
        accessor.Current is not null;

    /// <summary>
    /// The principal of the current request.
    /// </summary>
    /// <remarks>
    /// For code behind <c>[Authorize]</c>, where an anonymous request is a wiring
    /// mistake rather than a runtime case. It throws on a service-to-service door as
    /// well, which is what keeps code written for people from running for a service.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No person stands behind the request.</exception>
    public static Principal Require(this ICurrentPrincipal accessor) =>
        accessor.Current ?? throw new InvalidOperationException(
            "No principal on this request. Is [Authorize] or the principal "
            + "middleware missing, or is this a door a service calls? "
            + "A service has no principal.");

    /// <summary>
    /// Gets the tenant when the caller acts for a company. Anonymous callers and
    /// callers acting as a person both return false.
    /// </summary>
    public static bool TryGetTenant(this ICurrentPrincipal accessor, out TenantId tenant)
    {
        if (accessor.Current is { } principal)
        {
            return principal.TryGetTenant(out tenant);
        }

        tenant = TenantId.None;
        return false;
    }

    public static bool TryGetSubject(this ICurrentPrincipal accessor, [NotNullWhen(true)] out SubjectId subject)
    {
        if (accessor.Current is { } principal)
        {
            subject = principal.Subject;
            return true;
        }

        subject = default;
        return false;
    }
}
