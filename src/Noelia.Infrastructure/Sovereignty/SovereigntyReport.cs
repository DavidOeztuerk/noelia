using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Sovereignty;

namespace Noelia.Infrastructure.Sovereignty;

/// <summary>
/// Builds the report from the connection strings in configuration plus anything
/// the application declares.
/// </summary>
public sealed class SovereigntyReport : ISovereigntyReport
{
    private readonly IConfiguration _configuration;
    private readonly IEgressPolicy _egressPolicy;
    private readonly IReadOnlyList<(string Name, string? Value)> _declared;

    public SovereigntyReport(
        IConfiguration configuration,
        IEgressPolicy egressPolicy,
        IEnumerable<DeclaredDependency> declared)
    {
        _configuration = configuration;
        _egressPolicy = egressPolicy;
        _declared = [.. declared.Select(d => (d.Name, d.EndpointOrConnectionString))];
    }

    /// <inheritdoc />
    public SovereigntyAssessment Assess()
    {
        var findings = new List<DependencyFinding>();

        foreach (var entry in _configuration.GetSection("ConnectionStrings").GetChildren())
        {
            findings.Add(Examine(entry.Key, entry.Value));
        }

        foreach (var (name, value) in _declared)
        {
            findings.Add(Examine(name, value));
        }

        return new SovereigntyAssessment(
            findings.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray(),
            _egressPolicy.DeclaredHosts,
            _egressPolicy.IsEnforcing);
    }

    private static DependencyFinding Examine(string name, string? value)
    {
        var host = ExtractHost(value);
        var (jurisdiction, note) = HostJurisdiction.Classify(host);

        return new DependencyFinding(name, host, jurisdiction, note)
        {
            Kind = HostJurisdiction.IsArtificialIntelligence(host)
                ? DependencyKind.ArtificialIntelligence
                : DependencyKind.Ordinary
        };
    }

    /// <summary>
    /// Pulls the host out of a URL or a key-value connection string, and never
    /// returns anything else — a connection string carries credentials, and a
    /// report is something people paste into tickets.
    /// </summary>
    internal static string? ExtractHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host;
        }

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            if (!key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("Server", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // "host,port" and "host:port" both occur.
            return part[(separator + 1)..].Split(',', ':')[0].Trim();
        }

        // A bare "host:port" list, as Redis and Valkey connection strings use.
        return value.Split(',')[0].Split(':')[0].Trim();
    }
}

/// <summary>
/// A dependency the application declares because it does not appear under
/// <c>ConnectionStrings</c>.
/// </summary>
public sealed record DeclaredDependency(string Name, string? EndpointOrConnectionString);

public static class SovereigntyReportExtensions
{
    /// <summary>
    /// Registers the sovereignty report. Connection strings are picked up
    /// automatically; anything else is declared here.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddNoeliaSovereigntyReport(
    ///     new DeclaredDependency("Secrets", configuration["OpenBao:Address"]),
    ///     new DeclaredDependency("Telemetry", configuration["Otlp:Endpoint"]));
    /// </code>
    /// </example>
    public static IServiceCollection AddNoeliaSovereigntyReport(
        this IServiceCollection services,
        params DeclaredDependency[] declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        foreach (var dependency in declared)
        {
            services.AddSingleton(dependency);
        }

        services.TryAddEgressPolicyFallback();
        services.AddSingleton<ISovereigntyReport, SovereigntyReport>();

        return services;
    }

    /// <summary>
    /// The report reads the egress policy; without one it uses the unrestricted
    /// policy so the report still works.
    /// </summary>
    private static void TryAddEgressPolicyFallback(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(IEgressPolicy)))
        {
            services.AddSingleton(EgressPolicy.Unrestricted);
        }
    }
}
