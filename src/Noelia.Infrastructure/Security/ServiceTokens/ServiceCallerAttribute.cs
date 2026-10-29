using Microsoft.AspNetCore.Authorization;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>Lets only the named calling services in.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ServiceCallerAttribute : Attribute, IAuthorizeData, IAuthorizationRequirementData
{
    /// <summary>Names the callers.</summary>
    public ServiceCallerAttribute(params string[] callers) => throw new NotImplementedException();

    /// <inheritdoc />
    public string? Policy { get; set; }

    /// <inheritdoc />
    public string? Roles { get; set; }

    /// <inheritdoc />
    public string? AuthenticationSchemes { get; set; }

    /// <inheritdoc />
    public IEnumerable<IAuthorizationRequirement> GetRequirements() => throw new NotImplementedException();
}
