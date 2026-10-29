using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Infrastructure.Communication.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Noelia.Infrastructure.Security.M2M;

/// <summary>
/// Service token provider implementation using client credentials flow
/// </summary>
public class ServiceTokenProvider : IServiceTokenProvider, IServiceTokenSource
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ServiceTokenProvider> _logger;
    private readonly M2MConfiguration _config;
    private readonly IDistributedCache? _cache;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private const string CacheKeyPrefix = "m2m:token:";

    public ServiceTokenProvider(
        HttpClient httpClient,
        ILogger<ServiceTokenProvider> logger,
        IOptions<ServiceCommunicationOptions> options,
        IDistributedCache? cache = null,
        ServiceTokenCache? tokenCache = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = options.Value.M2M;
        _cache = cache;
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("M2M authentication is disabled");
            return null;
        }

        // Try to get from cache first
        if (_config.EnableTokenCaching && _cache != null)
        {
            var tokenInfo = await GetCachedTokenAsync(cancellationToken);
            if (tokenInfo != null && tokenInfo.IsValid)
            {
                // Check if token should be refreshed proactively
                if (tokenInfo.ShouldRefresh(_config.RefreshBeforeExpiry))
                {
                    _logger.LogDebug("Token will expire soon, refreshing proactively");
                    _ = Task.Run(async () => await RefreshTokenAsync(CancellationToken.None));
                }

                return tokenInfo.Token;
            }
        }

        // Cache miss or expired - get new token
        return await RefreshTokenAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> GetTokenAsync(string audience, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public async Task<string?> RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            return null;
        }

        // Use semaphore to prevent multiple concurrent token refreshes
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Requesting new M2M token from {TokenEndpoint}", _config.TokenEndpoint);

            var tokenResponse = await RequestTokenAsync(cancellationToken);
            if (tokenResponse == null)
            {
                _logger.LogError("Failed to obtain M2M token");
                return null;
            }

            // Cache the token
            if (_config.EnableTokenCaching && _cache != null)
            {
                await CacheTokenAsync(tokenResponse, cancellationToken);
            }

            return tokenResponse.Token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task InvalidateTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_config.EnableTokenCaching && _cache != null)
        {
            var cacheKey = $"{_config.TokenCacheKeyPrefix}{_config.ClientId}";
            await _cache.RemoveAsync(cacheKey, cancellationToken);
            _logger.LogInformation("Invalidated cached M2M token");
        }
    }

    public async Task<TokenInfo?> GetTokenInfoAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            return null;
        }

        if (_config.EnableTokenCaching && _cache != null)
        {
            return await GetCachedTokenAsync(cancellationToken);
        }

        // If caching is disabled, we need to get a new token to return info
        var token = await GetTokenAsync(cancellationToken);
        if (token == null)
        {
            return null;
        }

        return new TokenInfo
        {
            Token = token,
            TokenType = "Bearer",
            ExpiresAt = DateTime.UtcNow.Add(_config.TokenLifetime),
            Scopes = _config.Scopes
        };
    }

    private async Task<TokenInfo?> RequestTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(_config.TokenEndpoint))
            {
                _logger.LogError("M2M TokenEndpoint not configured");
                return null;
            }

            var requestPayload = new
            {
                ServiceName = _config.ClientId,
                ServicePassword = _config.ClientSecret
            };

            var jsonContent = JsonSerializer.Serialize(requestPayload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            using var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_config.TokenEndpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Token request failed with status {StatusCode}: {Error}",
                    response.StatusCode, errorContent);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind == JsonValueKind.Null)
            {
                _logger.LogError("Token response has no data property");
                return null;
            }

            if (!dataElement.TryGetProperty("accessToken", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
            {
                _logger.LogError("Token response has no accessToken");
                return null;
            }

            var accessToken = tokenElement.GetString();
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("AccessToken is empty");
                return null;
            }

            var expiresIn = dataElement.TryGetProperty("expiresIn", out var expiresInElement) && expiresInElement.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromSeconds(expiresInElement.GetInt32())
                : _config.TokenLifetime;

            return new TokenInfo
            {
                Token = accessToken,
                TokenType = "Bearer",
                ExpiresAt = DateTime.UtcNow.Add(expiresIn),
                Scopes = _config.Scopes
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting M2M token");
            return null;
        }
    }

    private async Task<TokenInfo?> GetCachedTokenAsync(CancellationToken cancellationToken)
    {
        if (_cache == null)
        {
            return null;
        }

        try
        {
            var cacheKey = $"{_config.TokenCacheKeyPrefix}{_config.ClientId}";
            var cachedData = await _cache.GetStringAsync(cacheKey, cancellationToken);

            if (string.IsNullOrEmpty(cachedData))
            {
                return null;
            }

            var tokenInfo = JsonSerializer.Deserialize<TokenInfo>(cachedData);
            if (tokenInfo == null || !tokenInfo.IsValid)
            {
                await _cache.RemoveAsync(cacheKey, cancellationToken);
                return null;
            }

            _logger.LogDebug("Retrieved M2M token from cache");
            return tokenInfo;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving M2M token from cache");
            return null;
        }
    }

    private async Task CacheTokenAsync(TokenInfo tokenInfo, CancellationToken cancellationToken)
    {
        if (_cache == null)
        {
            return;
        }

        try
        {
            var cacheKey = $"{_config.TokenCacheKeyPrefix}{_config.ClientId}";
            var serialized = JsonSerializer.Serialize(tokenInfo);

            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = tokenInfo.ExpiresAt
            };

            await _cache.SetStringAsync(cacheKey, serialized, cacheOptions, cancellationToken);
            _logger.LogDebug("Cached M2M token until {ExpiresAt}", tokenInfo.ExpiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error caching M2M token");
        }
    }

}
