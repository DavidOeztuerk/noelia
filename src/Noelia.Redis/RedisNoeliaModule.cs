using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Security;
using Noelia.Abstractions.Security.Encryption;
using Noelia.Redis.Caching;
using Noelia.Redis.Security;
using StackExchange.Redis;

namespace Noelia.Redis;

/// <summary>Exposes the Redis-backed providers in the Noelia composition.</summary>
/// <remarks>
/// These exist because <c>AddRedisCache(prefix)</c> and its siblings register
/// the moment they are called, while the built-in modules register when the
/// composition is built — which is later. A service that called
/// <c>AddRedisCache</c> beside <c>UseDefaults()</c> therefore got the
/// in-process rate counter that <c>RateLimiting</c> brings, silently, because
/// the last registration of a service is the one that wins. Nothing failed and
/// nothing was logged; the counters simply stayed in the process, which is a
/// per-replica limit wearing the name of a shared one.
/// <para>
/// Registering through <see cref="NoeliaBuilder.Use(NoeliaModule, Action{NoeliaBuilder}, Action{NoeliaModuleContractBuilder})"/>
/// puts the provider where every other provider sits: after the built-ins,
/// in the composition report, and with a contract that says what it supplies.
/// The <c>Add…</c> methods remain for hosts that compose by hand.
/// </para>
/// </remarks>
public static class RedisNoeliaModule
{
    /// <summary>The module id for Redis-backed encryption.</summary>
    public static NoeliaModule Encryption => new("Redis.Encryption");

    /// <summary>The module id for the Redis cache and rate counter.</summary>
    public static NoeliaModule Cache => new("Redis.Cache");

    /// <summary>The module id for the Redis token revocation list.</summary>
    public static NoeliaModule TokenRevocation => new("Redis.TokenRevocation");

    /// <summary>The module id for the Redis security audit sink.</summary>
    public static NoeliaModule SecurityAudit => new("Redis.SecurityAudit");

    /// <summary>
    /// Puts the cache and the rate counter on the RESP server.
    /// </summary>
    /// <param name="noelia">The composition.</param>
    /// <param name="keyPrefix">
    /// Namespaces every key, so two services sharing a server cannot read each
    /// other's entries.
    /// </param>
    public static NoeliaBuilder UseRedisCache(this NoeliaBuilder noelia, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(noelia);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        return noelia.Use(
            Cache,
            builder => builder.Services.AddRedisCache(keyPrefix),
            contract => contract
                .Requires<IConnectionMultiplexer>(new NoeliaProviderHint(
                    "Noelia.Redis", "AddRedisConnection(connectionString, instanceName)"))
                .Provides<IDistributedCacheService>("Noelia.Redis", "UseRedisCache(prefix)")
                .Provides<IDistributedRateLimitStore>("Noelia.Redis", "UseRedisCache(prefix)"));
    }

    /// <summary>
    /// Puts the revocation list on the RESP server, where every replica reads
    /// the same one.
    /// </summary>
    /// <param name="noelia">The composition.</param>
    /// <param name="maxTokenLifetime">
    /// How long an entry has to outlive the token it revokes. Shorter, and a
    /// token outlives its own revocation.
    /// </param>
    public static NoeliaBuilder UseRedisTokenRevocation(
        this NoeliaBuilder noelia,
        TimeSpan maxTokenLifetime)
    {
        ArgumentNullException.ThrowIfNull(noelia);

        return noelia.Use(
            TokenRevocation,
            builder => builder.Services.AddRedisTokenRevocation(maxTokenLifetime),
            contract => contract
                .Requires<IConnectionMultiplexer>(new NoeliaProviderHint(
                    "Noelia.Redis", "AddRedisConnection(connectionString, instanceName)"))
                .Provides<ITokenRevocationEvaluator>(
                    "Noelia.Redis", "UseRedisTokenRevocation(maxTokenLifetime)")
                .Provides<ITokenRevocationWriter>(
                    "Noelia.Redis", "UseRedisTokenRevocation(maxTokenLifetime)"));
    }

    /// <summary>The module id for the shared sovereign audit chain.</summary>
    public static NoeliaModule SovereignAudit => new("Redis.SovereignAudit");

    /// <summary>
    /// Puts the sovereign audit chain on the RESP server, so every replica
    /// extends one sequence instead of starting its own.
    /// </summary>
    /// <param name="noelia">The composition.</param>
    /// <param name="keyPrefix">Separates one system's chain from another's.</param>
    public static NoeliaBuilder UseRedisSovereignAudit(
        this NoeliaBuilder noelia,
        string keyPrefix = "noelia")
    {
        ArgumentNullException.ThrowIfNull(noelia);

        return noelia.Use(
            SovereignAudit,
            builder => builder.Services.AddRedisSovereignAudit(keyPrefix),
            contract => contract
                .Requires<IConnectionMultiplexer>(new NoeliaProviderHint(
                    "Noelia.Redis", "AddRedisConnection(connectionString, instanceName)"))
                .Provides<Noelia.Abstractions.Audit.ISovereignAuditSink>(
                    "Noelia.Redis", "UseRedisSovereignAudit()")
                .Provides<Noelia.Abstractions.Audit.IChainedSovereignAuditSink>(
                    "Noelia.Redis", "UseRedisSovereignAudit()"));
    }

    /// <summary>Writes the security audit trail to the RESP server.</summary>
    /// <param name="noelia">The composition.</param>
    public static NoeliaBuilder UseRedisSecurityAudit(this NoeliaBuilder noelia)
    {
        ArgumentNullException.ThrowIfNull(noelia);

        return noelia.Use(
            SecurityAudit,
            builder => builder.Services.AddRedisSecurityAudit(),
            contract => contract
                .Requires<IConnectionMultiplexer>(new NoeliaProviderHint(
                    "Noelia.Redis", "AddRedisConnection(connectionString, instanceName)")));
    }

    public static NoeliaBuilder UseRedisEncryption(this NoeliaBuilder noelia)
    {
        ArgumentNullException.ThrowIfNull(noelia);

        return noelia.Use(
            Encryption,
            builder => builder.Services.AddRedisEncryption(),
            contract => contract
                .Requires<IConnectionMultiplexer>(new NoeliaProviderHint(
                    "Noelia.Redis", "AddRedisConnection(connectionString, instanceName)"))
                .Requires<IMasterKeyProvider>(new NoeliaProviderHint(
                    "Noelia.Infrastructure", "AddConfiguredMasterKey() or AddSecretStoreMasterKey()"))
                .Provides<IDataEncryptionService>(
                    "Noelia.Redis", "UseRedisEncryption()"));
    }
}
