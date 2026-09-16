using Noelia.Abstractions.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Noelia.Infrastructure.Communication.Configuration;

namespace Noelia.Infrastructure.Communication.Caching;

/// <summary>
/// Service response cache implementation using IDistributedCache
/// </summary>
public class ServiceResponseCache : IServiceResponseCache
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<ServiceResponseCache> _logger;
    private readonly CacheConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;

    // Statistics tracking
    private long _totalRequests = 0;
    private long _cacheHits = 0;
    private long _cacheMisses = 0;
    private long _cacheEvictions = 0;
    private readonly object _statsLock = new object();

    public ServiceResponseCache(
        IDistributedCache cache,
        ILogger<ServiceResponseCache> logger,
        IOptions<ServiceCommunicationOptions> options)
    {
        _cache = cache;
        _logger = logger;
        _config = options.Value.Caching;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<CachedResponse<T>?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        IncrementTotalRequests();

        try
        {
            var cachedData = await _cache.GetStringAsync(key, cancellationToken);

            if (string.IsNullOrEmpty(cachedData))
            {
                IncrementCacheMisses();
                _logger.LogDebug("Cache miss for key: {Key}", key);
                return null;
            }

            var cachedResponse = JsonSerializer.Deserialize<CachedResponse<T>>(cachedData, _jsonOptions);

            if (cachedResponse == null || !cachedResponse.IsValid)
            {
                IncrementCacheMisses();
                _logger.LogDebug("Cache expired for key: {Key}", key);
                await RemoveAsync(key, cancellationToken);
                return null;
            }

            IncrementCacheHits();
            _logger.LogDebug("Cache hit for key: {Key}", key);
            return cachedResponse;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving from cache for key: {Key}", key);
            IncrementCacheMisses();
            return null;
        }
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? ttl = null,
        string? etag = null,
        CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var effectiveTtl = ttl ?? _config.DefaultTTL;
            var cachedResponse = new CachedResponse<T>
            {
                Data = value,
                ETag = etag,
                CachedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(effectiveTtl)
            };

            var serialized = JsonSerializer.Serialize(cachedResponse, _jsonOptions);

            // Compress if enabled and data is large enough
            if (_config.EnableCompression && serialized.Length > _config.CompressionThreshold)
            {
                serialized = await CompressAsync(serialized);
            }

            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = effectiveTtl
            };

            await _cache.SetStringAsync(key, serialized, cacheOptions, cancellationToken);

            _logger.LogDebug("Cached response for key: {Key} with TTL: {TTL}", key, effectiveTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error caching response for key: {Key}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            IncrementCacheEvictions();
            _logger.LogDebug("Removed cache entry for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error removing cache entry for key: {Key}", key);
        }
    }

    public async Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        // Note: IDistributedCache doesn't support pattern-based removal
        // This would require a custom implementation with Redis or a cache key registry
        _logger.LogWarning("RemoveByPatternAsync not fully implemented for pattern: {Pattern}", pattern);
        await Task.CompletedTask;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var cachedData = await _cache.GetStringAsync(key, cancellationToken);
            return !string.IsNullOrEmpty(cachedData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking cache existence for key: {Key}", key);
            return false;
        }
    }

    public CacheStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            // Hits and misses rather than hits and a total: the total is
            // their sum, and carrying it separately invites the two to
            // disagree after a counter is missed somewhere.
            return new CacheStatistics
            {
                Hits = _cacheHits,
                Misses = _cacheMisses,
                Evictions = _cacheEvictions
            };
        }
    }

    private void IncrementTotalRequests()
    {
        lock (_statsLock)
        {
            _totalRequests++;
        }
    }

    private void IncrementCacheHits()
    {
        lock (_statsLock)
        {
            _cacheHits++;
        }
    }

    private void IncrementCacheMisses()
    {
        lock (_statsLock)
        {
            _cacheMisses++;
        }
    }

    private void IncrementCacheEvictions()
    {
        lock (_statsLock)
        {
            _cacheEvictions++;
        }
    }

    private async Task<string> CompressAsync(string data)
    {
        // Simple compression implementation placeholder
        // Could use GZip or other compression algorithms
        await Task.CompletedTask;
        return data;
    }
}
