using Microsoft.AspNetCore.Authorization;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// The requirement that the calling service is one of a named list.
/// </summary>
public sealed class ServiceCallerRequirement : IAuthorizationRequirement
{
    /// <summary>Names the callers.</summary>
    public ServiceCallerRequirement(IEnumerable<string> callers) => throw new NotImplementedException();

    internal static ServiceCallerRequirement Nobody => throw new NotImplementedException();

    /// <summary>The service ids that may call.</summary>
    public IReadOnlySet<string> Callers => throw new NotImplementedException();
}
