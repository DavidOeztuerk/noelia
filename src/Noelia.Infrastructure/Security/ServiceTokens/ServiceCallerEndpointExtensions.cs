using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>Endpoint conventions for doors that only named services may open.</summary>
public static class ServiceCallerEndpointExtensions
{
    /// <summary>Lets only the named services call.</summary>
    public static TBuilder RequireServiceCaller<TBuilder>(this TBuilder builder, params string[] callers)
        where TBuilder : IEndpointConventionBuilder => throw new NotImplementedException();

    /// <summary>Maps the catch-all.</summary>
    public static IEndpointConventionBuilder MapServiceFallback(
        this IEndpointRouteBuilder endpoints, string? prefix = null) => throw new NotImplementedException();
}
