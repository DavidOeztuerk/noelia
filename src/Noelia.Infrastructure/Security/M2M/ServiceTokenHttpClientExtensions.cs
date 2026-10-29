using Microsoft.Extensions.DependencyInjection;

namespace Noelia.Infrastructure.Security.M2M;

/// <summary>Puts a service token on the calls of a named or typed <c>HttpClient</c>.</summary>
public static class ServiceTokenHttpClientExtensions
{
    /// <summary>Stub.</summary>
    public static IHttpClientBuilder AddServiceToken(this IHttpClientBuilder builder, string audience) =>
        throw new NotImplementedException();
}
