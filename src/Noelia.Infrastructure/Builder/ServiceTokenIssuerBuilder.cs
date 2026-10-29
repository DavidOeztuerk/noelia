using Microsoft.Extensions.Configuration;
using Noelia.Abstractions.Hosting;

namespace Noelia.Infrastructure.Builder;

/// <summary>Stub.</summary>
public sealed class ServiceTokenIssuerBuilder
{
    public ServiceTokenIssuerBuilder Clients(IConfiguration configuration, params string[] clientIds) => throw new NotImplementedException();
    public ServiceTokenIssuerBuilder Audiences(params string[] audiences) => throw new NotImplementedException();
    public ServiceTokenIssuerBuilder Lifetime(TimeSpan lifetime) => throw new NotImplementedException();
}

/// <summary>Stub.</summary>
public static class ServiceTokenIssuerModuleExtensions
{
    public static NoeliaBuilder UseServiceTokenIssuer(this NoeliaBuilder noelia, Action<ServiceTokenIssuerBuilder> configure) => throw new NotImplementedException();
}
