using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Dashboard;
using Noelia.InMemory.Hosting;
using Noelia.InMemory.Security;
using Noelia.Infrastructure.Audit;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Security.Encryption;
using Noelia.Infrastructure.Security.Keys;
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
        // cannot.
        noelia.Services.AddSovereignAuditTrail();

        // The argument this library makes, made visible. Without this the
        // dashboard said "No sovereignty report is registered" — the headline
        // feature missing from the one place a customer would look for it.
        //
        // The declarations are the point: an outbound call to a host nobody
        // named fails, and the report says what this service reaches. Loopback
        // and private networks are allowed because everything here is a
        // container talking to its neighbour; a deployment that reaches the
        // internet names those hosts one by one.
        noelia.AddSovereignPlatform(sovereign =>
        {
            sovereign.DeclareDependency("Sessions", "sqlite:///data");

            if (environment.UsesRedis)
            {
                sovereign
                    .Allow(environment.RedisConnectionString!.Split(':')[0])
                    .DeclareDependency("Cache, rate limits, revocation, audit chain",
                        environment.RedisConnectionString);
            }
            else
            {
                sovereign.DeclareDependency("Cache, rate limits, revocation", "in-process");
            }
        });

        // The key ring needs a master key and somewhere to put the result, and
        // every stage has both. Requiring a whole encryption provider — which
        // in practice meant Redis — left Development unable to protect it at
        // all, and a stage that cannot is a stage that reports Fail forever.
        noelia.Services.AddConfiguredMasterKey();

        if (!environment.UsesRedis)
        {
            noelia.UseInMemoryCache(serviceName);
            noelia.UseDataProtection(serviceName);

            // Development got no revocation store at all, and the dashboard
            // said so: "Token revocation is not registered." A signed-out
            // access token stayed valid until it expired — in the one stage
            // where a developer is most likely to test signing out. In process
            // and per replica, which is what Development is, but registered.
            if (readsTokens)
            {
                noelia.Services.AddInMemoryTokenRevocation();
            }

            return noelia;
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

        // One chain for every replica writing to this server, instead of one
        // per process. Without it two replicas both start from their own head
        // and a verifier reading the store back finds a break on a system where
        // nothing was tampered with.
        noelia.UseRedisSovereignAudit();

        if (readsTokens)
        {
            noelia.UseRedisTokenRevocation(MaxTokenLifetime);
        }

        // The chain that ends the two data-protection warnings ASP.NET writes at
        // every start: a master key opens the encryption provider, the
        // encryption provider protects the key ring, and the cache provider
        // keeps it where the next container can read it. Without all three the
        // ring lives in one container's filesystem in the clear, and every
        // cookie or antiforgery token protected with it stops verifying when
        // that container is replaced.
        noelia.UseRedisEncryption();
        noelia.UseDataProtection(serviceName);

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
