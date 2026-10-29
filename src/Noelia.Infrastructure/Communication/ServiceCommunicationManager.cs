using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Noelia.Abstractions.Messaging;
using Noelia.Infrastructure.Resilience;
using Noelia.Infrastructure.Communication.Configuration;
using Noelia.Infrastructure.Communication.Caching;
using Noelia.Infrastructure.Communication.Telemetry;
using Noelia.Infrastructure.Communication.Deduplication;
using Noelia.Infrastructure.Security.M2M;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using Noelia.Abstractions.Observability;

namespace Noelia.Infrastructure.Communication;

public class ServiceCommunicationManager : IServiceCommunicationManager
{
    private readonly HttpClient _httpClient;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ServiceCommunicationManager> _logger;
    private readonly IConfiguration _configuration;
    private readonly ICircuitBreaker? _circuitBreaker;
    private readonly Resilience.IRetryPolicy? _retryPolicy;
    private readonly IServiceResponseCache? _cache;
    private readonly IServiceTokenProvider? _tokenProvider;
    private readonly IServiceTokenSource? _tokenSource;
    private readonly IServiceCommunicationMetrics? _metrics;
    private readonly IRequestDeduplicator? _deduplicator;
    private readonly Dictionary<string, string> _serviceUrls;
    private readonly bool _useGateway;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ServiceCommunicationOptions _options;

    /// <summary>
    /// The 7.0 constructor, kept with its exact signature so an assembly compiled
    /// against 7.0 still binds to it.
    /// </summary>
    /// <remarks>
    /// No default values here on purpose: with them, a call naming only the first
    /// four arguments would match both constructors and stop compiling.
    /// </remarks>
    public ServiceCommunicationManager(
        HttpClient httpClient,
        IEventBus eventBus,
        ILogger<ServiceCommunicationManager> logger,
        IConfiguration configuration,
        ICircuitBreakerFactory? circuitBreakerFactory,
        IRetryPolicyFactory? retryPolicyFactory,
        IHttpContextAccessor? httpContextAccessor,
        IOptions<ServiceCommunicationOptions>? options,
        IServiceResponseCache? cache,
        IServiceTokenProvider? tokenProvider,
        IServiceCommunicationMetrics? metrics,
        IRequestDeduplicator? deduplicator)
        : this(httpClient, eventBus, logger, configuration, circuitBreakerFactory, retryPolicyFactory,
            httpContextAccessor, options, cache, tokenProvider, metrics, deduplicator, tokenSource: null)
    {
    }

    /// <summary>
    /// Creates the manager. The container picks this constructor, so a registered
    /// per-audience token source is the one used.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public ServiceCommunicationManager(
        HttpClient httpClient,
        IEventBus eventBus,
        ILogger<ServiceCommunicationManager> logger,
        IConfiguration configuration,
        ICircuitBreakerFactory? circuitBreakerFactory = null,
        IRetryPolicyFactory? retryPolicyFactory = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IOptions<ServiceCommunicationOptions>? options = null,
        IServiceResponseCache? cache = null,
        IServiceTokenProvider? tokenProvider = null,
        IServiceCommunicationMetrics? metrics = null,
        IRequestDeduplicator? deduplicator = null,
        IServiceTokenSource? tokenSource = null)
    {
        _httpClient = httpClient;
        _eventBus = eventBus;
        _logger = logger;
        _configuration = configuration;
        _circuitBreaker = circuitBreakerFactory?.GetCircuitBreaker("ServiceCommunication");
        _retryPolicy = retryPolicyFactory?.GetRetryPolicy("ServiceCommunication");
        _httpContextAccessor = httpContextAccessor;
        _options = options?.Value ?? new ServiceCommunicationOptions();
        _cache = cache;
        _tokenProvider = tokenProvider;
        _tokenSource = tokenSource ?? tokenProvider as IServiceTokenSource;
        _metrics = metrics;
        _deduplicator = deduplicator;

        _serviceUrls = InitializeServiceUrls();
        _useGateway = _options.UseGateway;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <inheritdoc />
    public async Task<ServiceResponse<TResponse>> SendRequestAsync<TRequest, TResponse>(
        string serviceName,
        string endpoint,
        TRequest request,
        CancellationToken cancellationToken = default,
        Dictionary<string, string>? headers = null)
        where TRequest : class
        where TResponse : class
    {
        var resolvedService = _useGateway ? "gateway" : serviceName.ToLowerInvariant();
        if (!_serviceUrls.TryGetValue(resolvedService, out var baseUrl))
        {
            _logger.LogError("Service {ServiceName} not found in configuration", serviceName);
            throw new InvalidOperationException($"Service {serviceName} not configured");
        }

        var requestUri = $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";

        // Apply resilience patterns: Circuit Breaker -> Retry Policy -> Actual Request
        return await ExecuteWithResilienceAsync(
            async () => await SendHttpRequestAsync<TRequest, TResponse>(serviceName, requestUri, request, headers, cancellationToken),
            serviceName);
    }

    /// <inheritdoc />
    public async Task<ServiceResponse<TResponse>> GetAsync<TResponse>(
        string serviceName,
        string endpoint,
        CancellationToken cancellationToken = default,
        Dictionary<string, string>? headers = null)
        where TResponse : class
    {
        var resolvedService = _useGateway ? "gateway" : serviceName.ToLowerInvariant();
        if (!_serviceUrls.TryGetValue(resolvedService, out var baseUrl))
        {
            _logger.LogError("Service {ServiceName} not found in configuration", serviceName);
            throw new InvalidOperationException($"Service {serviceName} not configured");
        }

        var requestUri = $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";

        // Apply request deduplication if enabled
        if (_options.EnableRequestDeduplication && _deduplicator != null)
        {
            var deduplicationKey = $"GET:{serviceName}:{endpoint}";
            var shared = await _deduplicator.ExecuteAsync(
                deduplicationKey,
                async () => await ExecuteGetRequestWithCachingAsync<TResponse>(serviceName, endpoint, requestUri, headers, cancellationToken),
                cancellationToken);

            // The operation above never yields null, so neither can a caller
            // that joined it. Nothing left to report if it did.
            return shared ?? throw new InvalidOperationException(
                $"Deduplicated GET to {serviceName} produced no answer.");
        }

        return await ExecuteGetRequestWithCachingAsync<TResponse>(serviceName, endpoint, requestUri, headers, cancellationToken);
    }

    private async Task<ServiceResponse<TResponse>> ExecuteGetRequestWithCachingAsync<TResponse>(
        string serviceName,
        string endpoint,
        string requestUri,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        // Check if caching is enabled for this service and method
        if (_options.EnableResponseCaching && _cache != null && IsCacheable(serviceName, endpoint, "GET"))
        {
            var cacheKey = CacheKeyGenerator.GenerateKey(serviceName, endpoint, headers);

            // Try to get from cache first
            var cachedResponse = await _cache.GetAsync<TResponse>(cacheKey, cancellationToken);
            if (cachedResponse != null && cachedResponse.IsValid)
            {
                _logger.LogDebug("Returning cached response for {ServiceName} GET {Endpoint}", serviceName, endpoint);

                // Record cache hit metric
                _metrics?.RecordServiceCall(serviceName, endpoint, "GET", 200, TimeSpan.Zero, fromCache: true);
                _metrics?.RecordCacheOperation(serviceName, "hit", true);

                return new ServiceResponse<TResponse>(HttpStatusCode.OK, cachedResponse.Data, null);
            }

            // Record cache miss
            _metrics?.RecordCacheOperation(serviceName, "miss", true);

            // Cache miss - fetch from service
            var response = await ExecuteWithResilienceAsync(
                async () => await SendHttpGetAsync<TResponse>(serviceName, requestUri, headers, cancellationToken),
                serviceName);

            // Only an answer that succeeded is worth remembering: caching a 404
            // would keep answering it after the resource appears.
            if (response is { IsSuccess: true, Value: not null })
            {
                var ttl = GetCacheTtl(serviceName);
                await _cache.SetAsync(cacheKey, response.Value, ttl, etag: null, cancellationToken);
            }

            return response;
        }

        // No caching - direct request
        return await ExecuteWithResilienceAsync(
            async () => await SendHttpGetAsync<TResponse>(serviceName, requestUri, headers, cancellationToken),
            serviceName);
    }

    private async Task<ServiceResponse<TResponse>> SendHttpGetAsync<TResponse>(
        string serviceName,
        string requestUri,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
            await AttachHeadersAsync(httpRequest, serviceName, headers, cancellationToken);

            _logger.LogDebug("Sending GET to {ServiceName} at {RequestUri}", serviceName, requestUri);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;

            // Record metrics
            _metrics?.RecordServiceCall(serviceName, requestUri, "GET", statusCode, stopwatch.Elapsed, fromCache: false);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Successfully received GET response from {ServiceName}", serviceName);
                return new ServiceResponse<TResponse>(
                    response.StatusCode, UnwrapResponse<TResponse>(content, serviceName), content);
            }

            // The status and who gave it; not the body. That body is the other
            // service's data, it arrives here verbatim, and the caller already
            // has it in ServiceResponse.Body — where whoever knows whether it may
            // be kept can decide.
            _logger.LogWarning("GET to {ServiceName} answered {StatusCode} ({Bytes} bytes)",
                serviceName, response.StatusCode, content.Length);

            return new ServiceResponse<TResponse>(response.StatusCode, null, content);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics?.RecordServiceCallFailure(serviceName, requestUri, "GET", ex.GetType().Name, stopwatch.Elapsed);
            _logger.LogError(ex, "Error performing GET with {ServiceName} at {RequestUri}", serviceName, requestUri);
            throw;
        }
    }

    private async Task<ServiceResponse<TResponse>> SendHttpRequestAsync<TRequest, TResponse>(
        string serviceName,
        string requestUri,
        TRequest request,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(request, options: _jsonOptions)
            };
            await AttachHeadersAsync(httpRequest, serviceName, headers, cancellationToken);

            _logger.LogDebug("Sending request to {ServiceName} at {RequestUri}", serviceName, requestUri);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;

            // Record metrics
            _metrics?.RecordServiceCall(serviceName, requestUri, "POST", statusCode, stopwatch.Elapsed, fromCache: false);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Successfully received response from {ServiceName}", serviceName);
                return new ServiceResponse<TResponse>(
                    response.StatusCode, UnwrapResponse<TResponse>(content, serviceName), content);
            }

            // The status and who gave it; not the body. That body is the other
            // service's data, it arrives here verbatim, and the caller already
            // has it in ServiceResponse.Body — where whoever knows whether it may
            // be kept can decide.
            _logger.LogWarning("Request to {ServiceName} answered {StatusCode} ({Bytes} bytes)",
                serviceName, response.StatusCode, content.Length);

            return new ServiceResponse<TResponse>(response.StatusCode, null, content);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics?.RecordServiceCallFailure(serviceName, requestUri, "POST", ex.GetType().Name, stopwatch.Elapsed);
            _logger.LogError(ex, "Error communicating with {ServiceName} at {RequestUri}", serviceName, requestUri);
            throw;
        }
    }

    private TResponse? UnwrapResponse<TResponse>(string responseContent, string serviceName)
        where TResponse : class
    {
        try
        {
            // Try to parse as JSON document first to check structure
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            // Check if it looks like an ApiResponse wrapper (has "success" and "data" properties)
            if (root.TryGetProperty("success", out var successElement) &&
                root.TryGetProperty("data", out var dataElement))
            {
                var success = successElement.GetBoolean();

                if (success && dataElement.ValueKind != JsonValueKind.Null)
                {
                    // Deserialize just the "data" property as TResponse
                    var data = JsonSerializer.Deserialize<TResponse>(dataElement.GetRawText(), _jsonOptions);
                    if (data != null)
                    {
                        _logger.LogDebug("Unwrapped ApiResponse wrapper from {ServiceName}, extracted data as {Type}",
                            serviceName, typeof(TResponse).Name);
                        return data;
                    }
                }

                if (!success)
                {
                    // How many, not which: the messages are the other service's
                    // text about the other service's data, and they reach the
                    // caller with the rest of the body.
                    var errorCount = root.TryGetProperty("errors", out var errorsElement) &&
                                     errorsElement.ValueKind == JsonValueKind.Array
                        ? errorsElement.GetArrayLength()
                        : 0;

                    _logger.LogWarning("{ServiceName} answered with an unsuccessful envelope, {Count} error(s)",
                        serviceName, errorCount);
                    return default;
                }
            }
        }
        catch (JsonException)
        {
            // Not an ApiResponse wrapper, try direct deserialization
        }

        // Try to deserialize directly as TResponse
        try
        {
            var directResult = JsonSerializer.Deserialize<TResponse>(responseContent, _jsonOptions);
            if (directResult != null)
            {
                _logger.LogDebug("Deserialized response directly as {Type} from {ServiceName}",
                    typeof(TResponse).Name, serviceName);
                return directResult;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize response from {ServiceName} as {Type}",
                serviceName, typeof(TResponse).Name);
        }

        return default;
    }

    private async Task AttachHeadersAsync(HttpRequestMessage httpRequest, string serviceName, Dictionary<string, string>? headers, CancellationToken cancellationToken = default)
    {
        if (headers != null)
        {
            foreach (var header in headers)
            {
                if (!httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    httpRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        if (_options.M2M.Enabled && _options.M2M.Protocol == ServiceTokenProtocol.ClientCredentials)
        {
            await AttachServiceTokenAsync(httpRequest, serviceName, cancellationToken);
        }
        else
        {
            await AttachLegacyAuthorizationAsync(httpRequest, cancellationToken);
        }

        AttachCorrelationHeaders(httpRequest);
    }

    /// <summary>
    /// Client-credentials mode (ADR-0002, F3): the target's token or no call. A token that
    /// cannot be had throws, and the configured bearer fallback is not consulted, because
    /// a call that quietly went out with a shared credential is the failure this mode
    /// exists to remove.
    /// </summary>
    private async Task AttachServiceTokenAsync(
        HttpRequestMessage httpRequest, string serviceName, CancellationToken cancellationToken)
    {
        if (httpRequest.Headers.Contains("Authorization"))
        {
            throw new InvalidOperationException(
                "The request already carries an Authorization header. Service calls carry the service "
                + "token and nothing else; a person's token is not forwarded.");
        }

        if (_tokenSource is null)
        {
            _logger.LogError("M2M uses client credentials but no token source is registered");
            throw new InvalidOperationException(
                "M2M is set to client credentials, but no IServiceTokenSource is registered. "
                + "Call UseServiceTokenClient() on the Noelia composition.");
        }

        // The audience is the target service, also when the call goes through the gateway:
        // the gateway forwards the request, it is not the service the token is for.
        string? token;
        try
        {
            token = await _tokenSource.GetTokenAsync(serviceName.ToLowerInvariant(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("No service token for {ServiceName}: {Exception}", serviceName, ex.GetType().Name);
            throw;
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new ServiceTokenUnavailableException("The token source returned no token.");
        }

        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        _logger.LogDebug("Using service token for service-to-service communication");
    }

    private async Task AttachLegacyAuthorizationAsync(HttpRequestMessage httpRequest, CancellationToken cancellationToken)
    {
        // ServiceCommunicationManager is ONLY for Service-to-Service calls
        // ALWAYS use M2M token, NEVER forward user tokens
        if (_options.M2M.Enabled && _tokenProvider != null)
        {
            try
            {
                var m2mToken = await _tokenProvider.GetTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(m2mToken) && !httpRequest.Headers.Contains("Authorization"))
                {
                    httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {m2mToken}");
                    _logger.LogDebug("Using M2M token for service-to-service communication");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get M2M token for service-to-service communication");
                throw; // Service calls should fail if M2M authentication fails
            }
        }
        else if (_options.M2M.Enabled)
        {
            _logger.LogWarning("M2M is enabled but token provider is not configured");
        }

        // Fallback to configured service token if M2M is not available
        if (!httpRequest.Headers.Contains("Authorization"))
        {
            var token = _configuration["ServiceCommunication:BearerToken"]
                        ?? Environment.GetEnvironmentVariable("SERVICE_COMMUNICATION_BEARER");
            if (!string.IsNullOrEmpty(token))
            {
                httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                _logger.LogDebug("Using fallback bearer token for service communication");
            }
        }

    }

    private void AttachCorrelationHeaders(HttpRequestMessage httpRequest)
    {
        // Propagate correlation ID for cross-service tracing
        var correlationId = _httpContextAccessor?.HttpContext?.Items[CorrelationId.BaggageKey]?.ToString();
        if (!string.IsNullOrEmpty(correlationId))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        }

        // Also propagate trace identifier as request ID
        var reqId = _httpContextAccessor?.HttpContext?.TraceIdentifier;
        if (!string.IsNullOrEmpty(reqId))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-Request-ID", reqId);
        }
    }

    public async Task PublishEventAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        try
        {
            _logger.LogDebug("Publishing event of type {EventType}", typeof(TEvent).Name);
            await _eventBus.PublishAsync(eventData, cancellationToken);
            _logger.LogDebug("Successfully published event of type {EventType}", typeof(TEvent).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish event of type {EventType}", typeof(TEvent).Name);
            throw;
        }
    }

    public async Task<bool> CheckServiceHealthAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (!_serviceUrls.TryGetValue(serviceName.ToLowerInvariant(), out var baseUrl))
        {
            return false;
        }

        var healthEndpoint = $"{baseUrl.TrimEnd('/')}/health/ready";
        
        try
        {
            using var response = await _httpClient.GetAsync(healthEndpoint, cancellationToken);
            var isHealthy = response.IsSuccessStatusCode;
            
            _logger.LogDebug("Health check for {ServiceName}: {Status}", 
                serviceName, isHealthy ? "Healthy" : "Unhealthy");
            
            return isHealthy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for {ServiceName}", serviceName);
            return false;
        }
    }

    public async Task<Dictionary<string, ServiceEndpointInfo>> DiscoverServiceEndpointsAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        if (!_serviceUrls.TryGetValue(serviceName.ToLowerInvariant(), out var baseUrl))
        {
            return new Dictionary<string, ServiceEndpointInfo>();
        }

        try
        {
            var discoveryEndpoint = $"{baseUrl.TrimEnd('/')}/swagger/v1/swagger.json";
            using var response = await _httpClient.GetAsync(discoveryEndpoint, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Service discovery failed for {ServiceName}", serviceName);
                return new Dictionary<string, ServiceEndpointInfo>();
            }

            var swaggerDoc = await response.Content.ReadFromJsonAsync<JsonDocument>(_jsonOptions, cancellationToken);
            var endpoints = ParseSwaggerDocument(swaggerDoc);
            
            _logger.LogDebug("Discovered {EndpointCount} endpoints for {ServiceName}", 
                endpoints.Count, serviceName);
            
            return endpoints;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover endpoints for {ServiceName}", serviceName);
            return new Dictionary<string, ServiceEndpointInfo>();
        }
    }

    /// <summary>
    /// Reads whatever stands under <c>ServiceEndpoints</c>. Nothing is defaulted:
    /// an unconfigured service has to fail at the call, and a default pointing at
    /// localhost would turn that failure into a silent call to the wrong host.
    /// </summary>
    private Dictionary<string, string> InitializeServiceUrls()
    {
        var services = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in _configuration.GetSection("ServiceEndpoints").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(endpoint.Value))
            {
                services[endpoint.Key] = endpoint.Value;
            }
        }

        return services;
    }

    /// <summary>
    /// Execute operation with resilience patterns (Circuit Breaker + Retry Policy)
    /// </summary>
    private async Task<T> ExecuteWithResilienceAsync<T>(Func<Task<T>> operation, string serviceName)
    {
        try
        {
            // Layer 1: Circuit Breaker (outer layer)
            if (_circuitBreaker != null)
            {
                return await _circuitBreaker.ExecuteAsync(async () =>
                {
                    // Layer 2: Retry Policy (inner layer)
                    if (_retryPolicy != null)
                    {
                        return await _retryPolicy.ExecuteAsync(
                            operation,
                            (attempt, exception, delay) =>
                            {
                                _logger.LogWarning(
                                    "Retry attempt {Attempt} for service {ServiceName} after {Delay}ms due to: {Exception}",
                                    attempt, serviceName, delay.TotalMilliseconds, exception.Message);

                                // Record retry metric
                                _metrics?.RecordRetryAttempt(serviceName, attempt);
                            });
                    }
                    else
                    {
                        return await operation();
                    }
                });
            }
            else if (_retryPolicy != null)
            {
                // Only Retry Policy available
                return await _retryPolicy.ExecuteAsync(
                    operation,
                    (attempt, exception, delay) =>
                    {
                        _logger.LogWarning(
                            "Retry attempt {Attempt} for service {ServiceName} after {Delay}ms due to: {Exception}",
                            attempt, serviceName, delay.TotalMilliseconds, exception.Message);

                        // Record retry metric
                        _metrics?.RecordRetryAttempt(serviceName, attempt);
                    });
            }
            else
            {
                // No resilience patterns configured
                return await operation();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "All resilience attempts exhausted for service {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Check if a service endpoint is cacheable
    /// </summary>
    private bool IsCacheable(string serviceName, string endpoint, string method)
    {
        // Check if caching is enabled globally
        if (!_options.EnableResponseCaching)
        {
            return false;
        }

        // Get service-specific cache policy
        if (_options.Caching.PerServicePolicies.TryGetValue(serviceName.ToLowerInvariant(), out var policy))
        {
            if (!policy.Enabled)
            {
                return false;
            }

            // Check if method is cacheable
            if (!policy.CacheableMethods.Contains(method.ToUpperInvariant()))
            {
                return false;
            }

            // Check exclude patterns
            foreach (var pattern in policy.ExcludePatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(endpoint, pattern))
                {
                    return false;
                }
            }

            // Check include patterns (if any are defined, endpoint must match at least one)
            if (policy.IncludePatterns.Any())
            {
                return policy.IncludePatterns.Any(pattern =>
                    System.Text.RegularExpressions.Regex.IsMatch(endpoint, pattern));
            }

            return true;
        }

        // Default: cache GET requests only
        return method.Equals("GET", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get cache TTL for a service
    /// </summary>
    private TimeSpan GetCacheTtl(string serviceName)
    {
        if (_options.Caching.PerServicePolicies.TryGetValue(serviceName.ToLowerInvariant(), out var policy))
        {
            return policy.TTL;
        }

        return _options.Caching.DefaultTTL;
    }

    /// <summary>
    /// Determine if an exception should trigger a retry for service calls
    /// </summary>
    private bool ShouldRetryForServiceCall(Exception exception)
    {
        // Retry on transient failures
        if (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return true;
        }

        // Retry on specific HTTP status codes if wrapped in an exception
        if (exception.Message.Contains("408") ||  // Request Timeout
            exception.Message.Contains("429") ||  // Too Many Requests
            exception.Message.Contains("500") ||  // Internal Server Error
            exception.Message.Contains("502") ||  // Bad Gateway
            exception.Message.Contains("503") ||  // Service Unavailable
            exception.Message.Contains("504"))    // Gateway Timeout
        {
            return true;
        }

        return false;
    }

    private static Dictionary<string, ServiceEndpointInfo> ParseSwaggerDocument(JsonDocument? swaggerDoc)
    {
        var endpoints = new Dictionary<string, ServiceEndpointInfo>();
        
        if (swaggerDoc?.RootElement.TryGetProperty("paths", out var pathsElement) == true)
        {
            foreach (var path in pathsElement.EnumerateObject())
            {
                foreach (var method in path.Value.EnumerateObject())
                {
                    var endpointKey = $"{method.Name.ToUpperInvariant()} {path.Name}";
                    var requiresAuth = CheckIfRequiresAuth(method.Value);
                    var metadata = ExtractMetadata(method.Value);
                    
                    endpoints[endpointKey] = new ServiceEndpointInfo(
                        endpointKey,
                        path.Name,
                        method.Name.ToUpperInvariant(),
                        requiresAuth,
                        metadata
                    );
                }
            }
        }
        
        return endpoints;
    }

    private static bool CheckIfRequiresAuth(JsonElement methodElement)
    {
        return methodElement.TryGetProperty("security", out _);
    }

    private static Dictionary<string, string> ExtractMetadata(JsonElement methodElement)
    {
        var metadata = new Dictionary<string, string>();
        
        if (methodElement.TryGetProperty("summary", out var summary))
        {
            metadata["summary"] = summary.GetString() ?? "";
        }
        
        if (methodElement.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var tagList = tags.EnumerateArray().Select(t => t.GetString() ?? "").ToList();
            metadata["tags"] = string.Join(", ", tagList);
        }
        
        return metadata;
    }
}
