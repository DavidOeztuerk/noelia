using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>Stub.</summary>
public static class ServiceTokenEndpoint
{
    public static RouteHandlerBuilder MapServiceTokenEndpoint(this IEndpointRouteBuilder endpoints, string pattern = "/m2m/token") => throw new NotImplementedException();
}
