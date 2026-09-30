using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Security.Keys;
using Noelia.Redis.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Noelia.Redis.Caching;

public static class RedisCacheRegistration
{
    /// <summary>
    /// Serves the cache and the rate limit store from Redis, so every instance
    /// sees the same state.
    /// </summary>
    /// <param name="services">The container to register in.</param>
    /// <param name="keyPrefix">
    /// Separates this application's keys from anything else sharing the server.
    /// </param>
    public static IServiceCollection AddRedisCache(this IServiceCollection services, string keyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        var prefix = keyPrefix.ToLowerInvariant();

        services.AddSingleton<IDistributedCacheService>(sp => new RedisDistributedCacheService(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<ILogger<RedisDistributedCacheService>>(),
            keyPrefix: $"{prefix}:",
            tagPrefix: $"{prefix}:tag:"));
        services.AddSingleton<IDataProtectionKeyStore>(sp => new RedisDataProtectionKeyStore(
            sp.GetRequiredService<IConnectionMultiplexer>(), prefix));

        services.AddSingleton<IDistributedRateLimitStore>(sp => new RedisDistributedRateLimitStore(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<ILogger<RedisDistributedRateLimitStore>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }
}
