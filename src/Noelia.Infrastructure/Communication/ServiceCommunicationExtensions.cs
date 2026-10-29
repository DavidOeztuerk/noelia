using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Noelia.Infrastructure.Resilience;
using Noelia.Infrastructure.Communication.Configuration;

namespace Noelia.Infrastructure.Communication;

public static class ServiceCommunicationExtensions
{
    public static IServiceCollection AddServiceCommunication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind configuration
        var section = configuration.GetSection(ServiceCommunicationOptions.SectionName);
        services.Configure<ServiceCommunicationOptions>(section);
        var options = section.Get<ServiceCommunicationOptions>() ?? new ServiceCommunicationOptions();

        // Configure HttpClient with timeout from options
        services.AddHttpClient<ServiceCommunicationManager>(client =>
        {
            client.Timeout = options.DefaultTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "Noelia-ServiceCommunication/1.0");
        });

        // Configure Circuit Breaker for ServiceCommunication
        services.AddCircuitBreaker("ServiceCommunication", cb =>
        {
            cb.ExceptionsAllowedBeforeBreaking = options.CircuitBreaker.ExceptionsAllowedBeforeBreaking;
            cb.DurationOfBreak = options.CircuitBreaker.DurationOfBreak;
            cb.Timeout = options.CircuitBreaker.Timeout;
            cb.FailureThreshold = options.CircuitBreaker.FailureThreshold;
            cb.MinimumThroughput = options.CircuitBreaker.MinimumThroughput;
        });

        // Configure Retry Policy for ServiceCommunication
        services.AddRetryPolicy("ServiceCommunication", rp =>
        {
            rp.MaxRetryAttempts = options.RetryPolicy.MaxRetries;
            rp.BaseDelay = options.RetryPolicy.InitialDelay;
            rp.MaxDelay = options.RetryPolicy.MaxDelay;
            rp.BackoffStrategy = MapBackoffStrategy(options.RetryPolicy.BackoffStrategy);
            rp.ShouldRetry = ex => ShouldRetryException(ex, options.RetryPolicy);
        });

        // Register cache if enabled
        if (options.EnableResponseCaching)
        {
            services.AddScoped<Caching.IServiceResponseCache, Caching.ServiceResponseCache>();
        }

        // Register M2M token provider if enabled
        if (options.M2M.Enabled)
        {
            // One registration, shared with UseServiceTokenClient: the typed client, the
            // process-wide token cache and the per-audience source. Registering the typed client
            // here a second time would leave two descriptors where the last silently wins.
            Security.M2M.ServiceTokenClientRegistration.Add(services);
        }

        // Register metrics if enabled
        if (options.EnableMetrics)
        {
            services.AddSingleton<Telemetry.IServiceCommunicationMetrics, Telemetry.ServiceCommunicationMetrics>();
        }

        // Register request deduplicator if enabled
        if (options.EnableRequestDeduplication)
        {
            services.AddSingleton<Deduplication.IRequestDeduplicator, Deduplication.RequestDeduplicator>();
        }

        services.AddScoped<IServiceCommunicationManager, ServiceCommunicationManager>();

        return services;
    }

    private static Resilience.BackoffStrategy MapBackoffStrategy(Configuration.BackoffStrategy strategy)
    {
        return strategy switch
        {
            Configuration.BackoffStrategy.Linear => Resilience.BackoffStrategy.Linear,
            Configuration.BackoffStrategy.ExponentialBackoff => Resilience.BackoffStrategy.Exponential,
            Configuration.BackoffStrategy.ExponentialBackoffWithJitter => Resilience.BackoffStrategy.ExponentialWithJitter,
            Configuration.BackoffStrategy.Fibonacci => Resilience.BackoffStrategy.Exponential, // Fallback to Exponential
            _ => Resilience.BackoffStrategy.ExponentialWithJitter
        };
    }

    private static bool ShouldRetryException(Exception ex, RetryConfiguration retryConfig)
    {
        // Check if exception type is retryable
        var exceptionTypeName = ex.GetType().Name;
        if (retryConfig.RetryableExceptions.Contains(exceptionTypeName))
        {
            return true;
        }

        // Check for timeout
        if (retryConfig.RetryOnTimeout && (ex is TimeoutException or TaskCanceledException))
        {
            return true;
        }

        // Check for HTTP status codes in HttpRequestException
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            var statusCode = (int)httpEx.StatusCode.Value;
            return retryConfig.RetryableStatusCodes.Contains(statusCode);
        }

        // Default retry for network exceptions
        return ex is HttpRequestException or TaskCanceledException or TimeoutException;
    }
}
