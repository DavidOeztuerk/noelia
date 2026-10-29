using Microsoft.AspNetCore.Authorization;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// Marks an endpoint as the fallback of a group of service doors, for the self-check.
/// </summary>
/// <remarks>
/// Which doors a fallback covers is not stored here: it is read off the fallback's
/// own route pattern, the final one after any group prefixes were applied. That is the
/// one value that cannot disagree with what the router does, and it spares
/// <c>RequireServiceCaller</c> from having to know which fallback it belongs to.
/// </remarks>
internal sealed class ServiceFallbackMarker
{
    internal static ServiceFallbackMarker Instance { get; } = new();
}

/// <summary>Marks the token endpoint of the issuer, so the self-check can find it.</summary>
internal sealed class ServiceTokenEndpointMarker
{
    internal static ServiceTokenEndpointMarker Instance { get; } = new();
}

/// <summary>The authorization data of a fallback: the service scheme and a requirement nobody meets.</summary>
internal sealed class ServiceFallbackAuthorizeData : IAuthorizeData, IAuthorizationRequirementData
{
    internal static ServiceFallbackAuthorizeData Instance { get; } = new();

    public string? Policy { get => null; set => throw new NotSupportedException(); }

    public string? Roles { get => null; set => throw new NotSupportedException(); }

    public string? AuthenticationSchemes
    {
        get => ServiceTokenDefaults.AuthenticationScheme;
        set => throw new NotSupportedException();
    }

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [ServiceCallerRequirement.Nobody];
}
