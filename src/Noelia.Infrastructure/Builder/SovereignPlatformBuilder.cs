using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Audit;
using Noelia.Infrastructure.Audit;
using Noelia.Infrastructure.Sovereignty;
using Microsoft.Extensions.DependencyInjection;

namespace Noelia.Infrastructure.Builder;

/// <summary>
/// Fluent configuration builder for sovereign platform defaults.
/// </summary>
public sealed class SovereignPlatformBuilder
{
    private readonly NoeliaBuilder _noelia;
    private readonly List<Action<EgressPolicyBuilder>> _egressConfigurators = [];
    private readonly List<DeclaredDependency> _declaredDependencies = [];
    private bool _allowLoopback = true;
    private bool _allowPrivateNetworks = true;
    private Action<IServiceCollection>? _sink;

    internal SovereignPlatformBuilder(NoeliaBuilder noelia)
    {
        _noelia = noelia;
    }

    /// <summary>
    /// Allows egress calls to specific hostnames.
    /// </summary>
    public SovereignPlatformBuilder Allow(params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        _egressConfigurators.Add(p => p.Allow(hosts));
        return this;
    }

    /// <summary>
    /// Allows egress calls to subdomains of the given domains.
    /// </summary>
    public SovereignPlatformBuilder AllowSubdomainsOf(params string[] domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        _egressConfigurators.Add(p => p.AllowSubdomainsOf(domains));
        return this;
    }

    /// <summary>
    /// Stops trusting loopback.
    /// </summary>
    /// <remarks>
    /// Loopback is allowed by default because a sidecar, an agent or a local
    /// proxy is the usual reason a sovereign deployment calls out at all. Where
    /// there is none, saying so closes a door nobody needs.
    /// </remarks>
    public SovereignPlatformBuilder WithoutLoopback()
    {
        _allowLoopback = false;
        return this;
    }

    /// <summary>
    /// Stops trusting the private ranges.
    /// </summary>
    /// <remarks>
    /// RFC1918 is allowed by default because the database, the cache and the
    /// broker live there. A service that reaches them only through named hosts
    /// can close the ranges and keep the names.
    /// </remarks>
    public SovereignPlatformBuilder WithoutPrivateNetworks()
    {
        _allowPrivateNetworks = false;
        return this;
    }

    /// <summary>
    /// Customizes the egress policy directly.
    /// </summary>
    public SovereignPlatformBuilder ConfigureEgress(Action<EgressPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _egressConfigurators.Add(configure);
        return this;
    }

    /// <summary>
    /// Declares an external dependency for the sovereignty report.
    /// </summary>
    public SovereignPlatformBuilder DeclareDependency(string name, string? endpoint)
    {
        _declaredDependencies.Add(new DeclaredDependency(name, endpoint));
        return this;
    }

    /// <summary>
    /// Configures a custom destination sink for the sovereign audit trail.
    /// </summary>
    public SovereignPlatformBuilder WithAuditSink<TSink>()
        where TSink : class, ISovereignAuditSink
    {
        _sink = services => services.AddSovereignAuditSink<TSink>();
        return this;
    }

    /// <summary>
    /// Hands the whole bundle to the composition as one module.
    /// </summary>
    /// <remarks>
    /// Registered through <c>Use(module, register, contract)</c> rather than
    /// straight into the container, so it appears in <c>NoeliaComposition</c>
    /// like everything else and a service that deliberately calls outward can
    /// drop it with a reason. Nothing is set up for a module that was dropped —
    /// an egress boundary registered anyway would go on refusing the calls that
    /// reason allowed for.
    /// </remarks>
    internal void Apply()
    {
        // Logging carries the masking enricher. Select it before Build freezes
        // the active set, so the composition and the registrations stay equal.
        _noelia.Use(NoeliaModule.Logging);

        _noelia.Use(
            NoeliaModule.SovereignPlatform,
            noelia =>
            {
            noelia.Services.AddNoeliaEgressPolicy(policy =>
            {
                if (_allowLoopback)
                {
                    policy.AllowLoopback();
                }

                if (_allowPrivateNetworks)
                {
                    policy.AllowPrivateNetworks();
                }

                foreach (var configure in _egressConfigurators)
                {
                    configure(policy);
                }
            });

            noelia.Services.AddNoeliaSovereigntyReport([.. _declaredDependencies]);

            _sink?.Invoke(noelia.Services);
            noelia.Services.AddSovereignAuditTrail();
            },
            contract => contract
                .Provides<Noelia.Abstractions.Sovereignty.ISovereigntyReport>(
                    "Noelia.Infrastructure", "AddSovereignPlatform(...)")
                .Provides<Noelia.Abstractions.Audit.IAuditTrailService>(
                    "Noelia.Infrastructure", "AddSovereignPlatform(...)"));
    }
}

/// <summary>
/// Extension methods for sovereign platform integration on <see cref="NoeliaBuilder"/>.
/// </summary>
public static class SovereignPlatformExtensions
{
    /// <summary>
    /// Bundles NoeliaBuilder with sovereign defaults:
    /// strict egress policy, PII data masking in logs, sovereignty report, and audit trail.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddNoelia(config, env, "identity", noelia => noelia
    ///     .UseDefaults()
    ///     .AddSovereignPlatform(sovereign => sovereign
    ///         .Allow("openbao.internal")
    ///         .DeclareDependency("Secrets", config["OpenBao:Address"])));
    /// </code>
    /// </example>
    public static NoeliaBuilder AddSovereignPlatform(
        this NoeliaBuilder noelia,
        Action<SovereignPlatformBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(noelia);

        var builder = new SovereignPlatformBuilder(noelia);
        configure?.Invoke(builder);
        builder.Apply();

        return noelia;
    }
}
