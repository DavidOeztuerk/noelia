using Noelia.Abstractions.Security;
using Noelia.Abstractions.Security.Audit;
using Noelia.Abstractions.Security.Authorization;
using Noelia.Abstractions.Security.Encryption;
using Noelia.Redis.Security.Audit;
using Noelia.Redis.Security.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noelia.Abstractions.Audit;
using StackExchange.Redis;
using System.Security.Cryptography;
using System.Text;

namespace Noelia.Redis.Security;

public static class RedisSecurityRegistration
{
    /// <summary>
    /// Writes the security audit trail to Redis, hash-chained so a missing or
    /// altered entry is detectable.
    /// </summary>
    /// <remarks>
    /// An audit trail is only evidence if it outlives the incident it records.
    /// Redis keeps it only as long as its persistence settings allow, so ship
    /// entries onward if they must be retained.
    /// </remarks>
    public static IServiceCollection AddRedisSecurityAudit(this IServiceCollection services)
    {
        services.AddSingleton<ISecurityAuditService>(provider =>
        {
            var masterKey = provider.GetRequiredService<IMasterKeyProvider>().GetMasterKey();

            if (masterKey.Length != 32)
            {
                throw new InvalidOperationException(
                    $"The master key used for audit signing is {masterKey.Length} bytes; 32 are required.");
            }

            // Derive a purpose-specific key instead of reusing the encryption
            // root directly as an HMAC key.
            var signingKey = HMACSHA256.HashData(
                masterKey,
                Encoding.UTF8.GetBytes("Noelia.Redis.SecurityAudit.Signing.v1"));

            return new SecurityAuditService(
                provider.GetRequiredService<IConnectionMultiplexer>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SecurityAuditService>>(),
                signingKey);
        });

        return services;
    }

    /// <summary>
    /// Writes the security audit trail with an explicit, stable 256-bit HMAC
    /// key. Prefer the parameterless overload when an
    /// <see cref="IMasterKeyProvider"/> is already part of the composition.
    /// </summary>
    public static IServiceCollection AddRedisSecurityAudit(
        this IServiceCollection services,
        byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        if (signingKey.Length != 32)
        {
            throw new ArgumentException(
                $"The audit signing key is {signingKey.Length} bytes; 32 are required.",
                nameof(signingKey));
        }

        var stableKey = signingKey.ToArray();
        services.AddSingleton<ISecurityAuditService>(provider => new SecurityAuditService(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SecurityAuditService>>(),
            stableKey));

        return services;
    }

    /// <summary>
    /// Keeps resource permissions and ownership in Redis, so every instance
    /// reaches the same decision.
    /// </summary>
    public static IServiceCollection AddRedisResourceAuthorization(this IServiceCollection services) =>
        services.AddSingleton<IResourceAuthorizationService, ResourceAuthorizationService>();


    /// <summary>
    /// Keeps token revocations in Redis, serving both the reading and the
    /// writing side from one instance.
    /// </summary>
    /// <remarks>
    /// Requires a registered <c>IConnectionMultiplexer</c> — call
    /// <c>AddRedisConnection(...)</c> first.
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="maxTokenLifetime">
    /// How long a cutoff is kept. Must be at least the longest lifetime an
    /// access token can have; checked here rather than on first use, so a wrong
    /// value fails at composition instead of on the request that needed it.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxTokenLifetime"/> is not positive.
    /// </exception>
    public static IServiceCollection AddRedisTokenRevocation(
        this IServiceCollection services,
        TimeSpan maxTokenLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTokenLifetime, TimeSpan.Zero);

        services.AddSingleton(provider => new RedisTokenRevocationStore(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            maxTokenLifetime,
            provider.GetService<TimeProvider>()));
        services.AddSingleton<ITokenRevocationEvaluator>(
            provider => provider.GetRequiredService<RedisTokenRevocationStore>());
        services.AddSingleton<ITokenRevocationWriter>(
            provider => provider.GetRequiredService<RedisTokenRevocationStore>());

        return services;
    }
}

/// <summary>Registers the shared sovereign audit chain.</summary>
public static class RedisSovereignAuditRegistration
{
    /// <summary>
    /// Puts the sovereign audit chain on the RESP server, where every replica
    /// extends one sequence instead of starting its own.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="keyPrefix">
    /// Separates one system's chain from another's on a shared server. Two
    /// deployments that should share a chain share this; two that should not,
    /// must not.
    /// </param>
    public static IServiceCollection AddRedisSovereignAudit(
        this IServiceCollection services,
        string keyPrefix = "noelia")
    {
        ArgumentNullException.ThrowIfNull(services);

        var sink = new Func<IServiceProvider, RedisSovereignAuditSink>(provider =>
            new RedisSovereignAuditSink(
                provider.GetRequiredService<IConnectionMultiplexer>(),
                provider.GetRequiredService<ILogger<RedisSovereignAuditSink>>(),
                keyPrefix));

        // Registered under both: AuditTrailService resolves the sink by the base
        // port and asks whether it also owns the chain, and the security check
        // asks the same question. One instance answers both.
        services.AddSingleton(sink);
        services.AddSingleton<ISovereignAuditSink>(p => p.GetRequiredService<RedisSovereignAuditSink>());
        services.AddSingleton<IChainedSovereignAuditSink>(p => p.GetRequiredService<RedisSovereignAuditSink>());

        return services;
    }
}
