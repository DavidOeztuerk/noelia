using Microsoft.AspNetCore.Authorization;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// Lets only the named calling services in, and answers everyone else with the
/// 404 of a door that is not there.
/// </summary>
/// <remarks>
/// The controller form of <c>RequireServiceCaller</c>. It needs
/// <c>UseServiceCallers</c> in the composition and a fallback over the door
/// (<c>MapServiceFallback("/prefix")</c>): without one the refusal is still a 404,
/// but the answer for a path that does not exist differs from it.
/// <para>
/// Policy, roles and scheme cannot be set: the scheme is fixed and the list of
/// callers is the whole policy. Combine it with <c>[Authorize]</c> to require more.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ServiceCallerAttribute : Attribute, IAuthorizeData, IAuthorizationRequirementData
{
    private readonly ServiceCallerRequirement _requirement;

    /// <summary>Names the services that may call.</summary>
    /// <param name="callers">Client ids; at least one, no wildcard.</param>
    /// <exception cref="ArgumentException">The list is empty or an id is malformed.</exception>
    public ServiceCallerAttribute(params string[] callers) =>
        _requirement = new ServiceCallerRequirement(callers);

    /// <inheritdoc />
    string? IAuthorizeData.Policy
    {
        get => null;
        set => throw Fixed();
    }

    /// <inheritdoc />
    string? IAuthorizeData.Roles
    {
        get => null;
        set => throw Fixed();
    }

    /// <inheritdoc />
    string? IAuthorizeData.AuthenticationSchemes
    {
        get => ServiceTokenDefaults.AuthenticationScheme;
        set => throw Fixed();
    }

    /// <summary>The scheme that authenticates service tokens.</summary>
    public string AuthenticationSchemes => ServiceTokenDefaults.AuthenticationScheme;

    /// <summary>Always null: the list of callers is the whole policy.</summary>
    public string? Policy => null;

    /// <summary>Always null: there is no role for a service.</summary>
    public string? Roles => null;

    /// <inheritdoc />
    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [_requirement];

    private static NotSupportedException Fixed() =>
        new("A service door is defined by its list of callers; policy, roles and scheme are not settable.");
}
