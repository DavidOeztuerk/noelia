using System.Net;
using Microsoft.Extensions.Configuration;
using Noelia.Infrastructure.Sovereignty;
using StackExchange.Redis;

namespace Demo.Platform;

/// <summary>Declarations derived from this host's selected providers, not observations of traffic.</summary>
public static class DemoDependencies
{
    public static IReadOnlyList<DeclaredDependency> State(DemoEnvironment environment,
        bool readsTokens, bool ownsSessions)
    {
        var dependencies = new List<DeclaredDependency>();
        if (ownsSessions)
            dependencies.Add(new("Sessions (local SQLite)", "http://localhost"));

        if (!environment.UsesRedis)
        {
            dependencies.Add(new("Cache, rate limits, audit chain, data protection (in-process)", "http://localhost"));
            if (readsTokens) dependencies.Add(new("Token revocation (in-process)", "http://localhost"));
            return dependencies;
        }

        // Use the provider parser, not Split(':'): connection strings may have
        // multiple endpoints, IPv6 and credentials in any option order. Only
        // normalized endpoint hosts reach the report registration.
        ConfigurationOptions options;
        try { options = ConfigurationOptions.Parse(environment.RedisConnectionString!); }
        catch (ArgumentException)
        { throw new InvalidOperationException("Demo Redis endpoint configuration is invalid."); }
        foreach (var endpoint in options.EndPoints)
        {
            var (host, port) = endpoint switch
            {
                DnsEndPoint dns => (dns.Host, dns.Port),
                IPEndPoint ip => (ip.Address.ToString(), ip.Port),
                _ => throw new InvalidOperationException("Demo Redis endpoint type is unsupported.")
            };
            var address = new UriBuilder("redis", host, port).Uri.AbsoluteUri;
            dependencies.Add(new("Cache, rate limits, audit chain, encryption keys (Redis)", address));
            if (readsTokens) dependencies.Add(new("Token revocation (Redis)", address));
        }
        return dependencies;
    }

    /// <summary>Configured static Ocelot destinations at startup; not proof that its private transport is guarded.</summary>
    public static IReadOnlyList<DeclaredDependency> GatewayRoutes(IConfiguration configuration)
    {
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in configuration.GetSection("Routes").GetChildren())
        {
            var scheme = route["DownstreamScheme"] ?? "http";
            if (scheme is not ("http" or "https"))
                throw new InvalidOperationException("Demo gateway routes require HTTP or HTTPS destinations.");
            foreach (var endpoint in route.GetSection("DownstreamHostAndPorts").GetChildren())
            {
                var host = endpoint["Host"];
                if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown
                    || !int.TryParse(endpoint["Port"], out var port) || port is < 1 or > 65535)
                    throw new InvalidOperationException("Demo gateway route endpoint is invalid.");
                destinations.Add(new UriBuilder(scheme, host, port).Uri.AbsoluteUri);
            }
        }
        return destinations.Order(StringComparer.Ordinal)
            .Select(address => new DeclaredDependency("Gateway downstream (configured HTTP route)", address)).ToArray();
    }
}
