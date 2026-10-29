using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Infrastructure.Communication.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Security.M2M;

/// <summary>
/// Service token provider implementation using client credentials flow
/// </summary>
/// <remarks>
/// Two protocols, chosen by <see cref="M2MConfiguration.Protocol"/>. The legacy one is
/// unchanged. In <see cref="ServiceTokenProtocol.ClientCredentials"/> mode the provider
/// also is an <see cref="IServiceTokenSource"/>: one token per audience, in the process
/// only, and an exception rather than a null when there is none. The parameterless
/// <c>GetTokenAsync</c>, <c>RefreshTokenAsync</c>, <c>InvalidateTokenAsync</c> and
/// <c>GetTokenInfoAsync</c> then act on <see cref="M2MConfiguration.Audience"/>, and
/// <c>GetTokenAsync</c> and <c>RefreshTokenAsync</c> throw
/// <see cref="ServiceTokenUnavailableException"/> instead of returning null.
/// </remarks>
public class ServiceTokenProvider : IServiceTokenProvider, IServiceTokenSource
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ServiceTokenProvider> _logger;
    private readonly M2MConfiguration _config;
    private readonly IDistributedCache? _cache;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ServiceTokenCache _tokens;
    private readonly TimeProvider _time;
    private const string CacheKeyPrefix = "m2m:token:";

    /// <summary>
    /// The 7.0 constructor, kept with its exact signature so an assembly compiled
    /// against 7.0 still binds to it.
    /// </summary>
    /// <remarks>
    /// No default values here on purpose: with them, a call naming only the first
    /// three arguments would match both constructors and stop compiling.
    /// </remarks>
    public ServiceTokenProvider(
        HttpClient httpClient,
        ILogger<ServiceTokenProvider> logger,
        IOptions<ServiceCommunicationOptions> options,
        IDistributedCache? cache)
        : this(httpClient, logger, options, cache, tokenCache: null, timeProvider: null)
    {
    }

    /// <summary>
    /// Creates the provider. The container picks this constructor, so the shared
    /// token cache and the clock are the registered ones.
    /// </summary>
    [ActivatorUtilitiesConstructor]
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
        // Without a shared cache each instance would fetch for itself; the module and
        // AddServiceCommunication both register the singleton, this only keeps a hand-built
        // provider (a test, a console tool) working.
        _tokens = tokenCache ?? new ServiceTokenCache();
        _time = timeProvider ?? TimeProvider.System;
    }

    private bool ClientCredentials => _config.Protocol == ServiceTokenProtocol.ClientCredentials;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("M2M authentication is disabled");
            return null;
        }

        if (ClientCredentials)
        {
            return await GetDefaultAudienceTokenAsync(cancellationToken);
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
    /// <exception cref="InvalidOperationException">
    /// The provider is disabled or speaks the legacy protocol, which has no audiences.
    /// </exception>
    public async Task<string> GetTokenAsync(string audience, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || !ClientCredentials)
        {
            throw new InvalidOperationException(
                "Tokens per audience need M2M enabled with Protocol = ClientCredentials; "
                + "UseServiceTokenClient() sets both.");
        }

        if (!ServiceTokenFormat.IsIdentifier(audience))
        {
            throw new ArgumentException(
                "The audience starts with a lower-case letter and has up to 48 lower-case letters, "
                + "digits and hyphens.",
                nameof(audience));
        }

        var entry = await _tokens.GetAsync(
            CacheKey(audience), _time,
            token => FetchAsync(audience, token),
            exception => _logger.LogWarning(
                "Renewing the service token for audience {Audience} failed ({Cause}); the current token is still valid",
                audience, exception.InnerException?.GetType().Name ?? exception.GetType().Name),
            cancellationToken);
        return entry.Token;
    }

    public async Task<string?> RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            return null;
        }

        if (ClientCredentials)
        {
            var audience = DefaultAudience();
            await InvalidateTokenAsync(cancellationToken);
            return await GetTokenAsync(audience, cancellationToken);
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
        if (ClientCredentials)
        {
            if (_config.Enabled)
            {
                _tokens.Remove(CacheKey(DefaultAudience()));
            }

            return;
        }

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

        if (ClientCredentials)
        {
            var audience = DefaultAudience();
            await GetTokenAsync(audience, cancellationToken);
            var entry = _tokens.Peek(CacheKey(audience));
            return entry is null
                ? null
                : new TokenInfo
                {
                    Token = entry.Token,
                    TokenType = "Bearer",
                    ExpiresAt = entry.ExpiresAt.UtcDateTime,
                    Scopes = _config.Scopes
                };
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

    private async Task<string> GetDefaultAudienceTokenAsync(CancellationToken cancellationToken) =>
        await GetTokenAsync(DefaultAudience(), cancellationToken);

    private string DefaultAudience() =>
        string.IsNullOrWhiteSpace(_config.Audience)
            ? throw new InvalidOperationException(
                "No audience given and M2MConfiguration.Audience is empty. Name the audience of the "
                + "service you call, for example AddServiceToken(\"identity\").")
            : _config.Audience;

    // The client id is part of the key so two providers configured for different clients
    // in one process (it happens in tests and in gateways) never hand each other's token out.
    private string CacheKey(string audience) => $"{_config.ClientId}\n{audience}";

    /// <summary>
    /// Asks the token endpoint for one token. Every failure is a
    /// <see cref="ServiceTokenUnavailableException"/> whose message names a kind of cause
    /// and no value; the cause is kept as the inner exception for the log.
    /// </summary>
    private async Task<ServiceTokenCache.Entry> FetchAsync(string audience, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_config.TokenEndpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ServiceTokenUnavailableException("The service token endpoint is not configured as an absolute http(s) URL.");
        }

        if (string.IsNullOrEmpty(_config.ClientId) || string.IsNullOrEmpty(_config.ClientSecret))
        {
            throw new ServiceTokenUnavailableException("The service client id or secret is not configured.");
        }

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _config.ClientId,
                ["client_secret"] = _config.ClientSecret,
                ["audience"] = audience
            });
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Status only: the body is the endpoint's, and a refusal has nothing in it worth keeping.
                _logger.LogWarning(
                    "The service token endpoint refused the request for audience {Audience} with status {StatusCode}",
                    audience, (int)response.StatusCode);
                throw new ServiceTokenUnavailableException(
                    $"The service token endpoint answered with status {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(json, audience);
        }
        catch (ServiceTokenUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The type only: the text of an HttpRequestException can quote the URL, and a
            // JsonException can quote the body.
            _logger.LogWarning(
                "Fetching the service token for audience {Audience} failed: {Exception}",
                audience, exception.GetType().Name);
            throw new ServiceTokenUnavailableException(
                "The service token endpoint could not be reached or did not answer with a token.", exception);
        }
    }

    private ServiceTokenCache.Entry Parse(string json, string audience)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("access_token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("token_type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("expires_in", out var expiresElement) || !expiresElement.TryGetInt32(out var expiresIn))
        {
            throw Malformed(audience);
        }

        var token = tokenElement.GetString();
        if (string.IsNullOrEmpty(token)
            || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !string.Equals(typeElement.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
            || expiresIn <= 0)
        {
            throw Malformed(audience);
        }

        var now = _time.GetUtcNow();
        var lifetime = TimeSpan.FromSeconds(expiresIn);
        // A fifth at most, so a short-lived token is not renewed from the moment it arrives.
        var window = TimeSpan.FromTicks(Math.Min(
            Math.Max(_config.RefreshBeforeExpiry.Ticks, 0), lifetime.Ticks / 5));
        return new ServiceTokenCache.Entry(token, now + lifetime, now + lifetime - window);
    }

    private ServiceTokenUnavailableException Malformed(string audience)
    {
        _logger.LogWarning(
            "The service token endpoint answered for audience {Audience} with something that is not a token",
            audience);
        return new ServiceTokenUnavailableException("The service token endpoint did not answer with a valid token.");
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
