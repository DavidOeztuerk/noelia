using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Dashboard;
using Noelia.InMemory.Hosting;
using Noelia.Infrastructure.Audit;
using Noelia.Redis;
using Noelia.Redis.Caching;
using Noelia.Redis.Security;

namespace Demo.Platform;

/// <summary>
/// Where this stage keeps the state Noelia needs.
/// </summary>
/// <remarks>
/// This is the demo's whole point in one method. The application code above it
/// is identical in every stage; the cache, the rate counters, the revocation
/// list and the refresh tokens move from the process to a shared server by
/// changing <c>Demo:Providers:Redis</c> in a configuration file. Nothing above
/// the composition root knows which of the two it got, which is the property
/// the port cut exists to produce.
/// <para>
/// Until 5.1.0 the demo registered <c>UseInMemoryCache</c> in every stage, so
/// the running stack exercised <c>Noelia.Redis</c> — the package carrying most
/// of the security-relevant implementations — exactly never.
/// </para>
/// </remarks>
public static class DemoProviders
{
    /// <summary>
    /// How long a revoked access token has to stay on the list: no longer than
    /// the longest token that could still be presented.
    /// </summary>
    private static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(24);

    /// <summary>Registers the state providers this stage asked for.</summary>
    /// <param name="noelia">The composition being built.</param>
    /// <param name="environment">The stage's declared providers.</param>
    /// <param name="serviceName">Prefixes keys so services can share one server.</param>
    /// <param name="readsTokens">
    /// True for every host that verifies a token, not only for the one that
    /// issues them. A verifier without the shared revocation list checks an
    /// empty list of its own and accepts a token the issuer revoked an hour
    /// ago — the sign-out worked everywhere except where it mattered. Only the
    /// gateway is false here, and only because it reads no token at all.
    /// </param>
    public static NoeliaBuilder UseDemoProviders(
        this NoeliaBuilder noelia,
        DemoEnvironment environment,
        string serviceName,
        bool readsTokens = false)
    {
        ArgumentNullException.ThrowIfNull(noelia);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // The dashboard records who looked at it, and refuses to answer when it
        // cannot. The sink is in-process in every stage because Noelia ships no
        // other ISovereignAuditSink yet — a single-replica demo keeps one chain,
        // which is exactly the limitation AuditTrailService documents.
        noelia.Services.AddSovereignAuditTrail();

        if (!environment.UsesRedis)
        {
            return noelia.UseInMemoryCache(serviceName);
        }

        // The connection itself is not a module: it is the thing the modules
        // below require, and the contract on each of them names this call when
        // it is missing.
        noelia.Services.AddRedisConnection(environment.RedisConnectionString!, serviceName);

        // Through Use(...), not through the Add… methods. The Add… form
        // registers immediately, the built-in modules register when the
        // composition is built, and the last registration wins — so calling
        // AddRedisCache beside UseDefaults() leaves the service on the
        // in-process rate counter that RateLimiting brings, silently.
        noelia.UseRedisCache(serviceName);
        noelia.UseRedisSecurityAudit();

        if (readsTokens)
        {
            noelia.UseRedisTokenRevocation(MaxTokenLifetime);
        }

        return noelia;
    }
}

/// <summary>Composes the operator dashboard only where the stage asks for it.</summary>
public static class DemoDashboard
{
    /// <summary>
    /// Adds the dashboard when this stage runs one, and leaves it out entirely
    /// when it does not.
    /// </summary>
    /// <param name="noelia">The composition being built.</param>
    /// <param name="environment">The stage's declared visibility.</param>
    /// <param name="host">Decides what Production means here.</param>
    /// <param name="configurationSections">
    /// Sections whose key names and value shapes may be shown. Values never are.
    /// </param>
    public static NoeliaBuilder UseDemoDashboard(
        this NoeliaBuilder noelia,
        DemoEnvironment environment,
        IHostEnvironment host,
        params string[] configurationSections)
    {
        ArgumentNullException.ThrowIfNull(noelia);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(host);

        if (!environment.DashboardEnabled)
        {
            return noelia.Without(
                NoeliaModule.Dashboard,
                $"{host.EnvironmentName} runs no operator dashboard on this service");
        }

        return noelia.UseDashboard(dashboard =>
        {
            environment.Apply(dashboard, host);

            if (configurationSections.Length > 0)
            {
                dashboard.InspectConfiguration(configurationSections);
            }
        });
    }
}
