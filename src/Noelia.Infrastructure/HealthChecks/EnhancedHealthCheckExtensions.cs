using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Noelia.Infrastructure.HealthChecks;

/// <summary>
/// Extension methods for enhanced health checks
/// </summary>
public static class EnhancedHealthCheckExtensions
{
    /// <summary>
    /// Add comprehensive health checks for the Noelia platform
    /// </summary>
    public static IServiceCollection AddNoeliaHealthChecks(
        this IServiceCollection services,
        Action<HealthCheckBuilder>? configure = null)
    {
        var builder = new HealthCheckBuilder(services);

        // The checks this package can register without naming a provider. A
        // database, a broker or a cache is reached by its driver, and the
        // driver lives in a provider package — so the readiness checks that
        // matter arrive with AddDatabase<TContext>(...), AddRedisConnection(...)
        // or UseMassTransitMessaging(...), not from here.
        builder.AddCustomHealthChecks();

        // Allow custom configuration
        configure?.Invoke(builder);

        return services;
    }

    /// <summary>
    /// Use enhanced health check endpoints
    /// </summary>
    public static IApplicationBuilder UseEnhancedHealthChecks(this IApplicationBuilder app)
    {
        // Liveness probe (basic health check)
        app.UseHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
            ResponseWriter = WriteHealthCheckResponse,
            AllowCachingResponses = false
        });

        // Readiness probe (comprehensive health check)
        app.UseHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthCheckResponse,
            AllowCachingResponses = false
        });

        // Detailed health report
        app.UseHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteDetailedHealthCheckResponse,
            AllowCachingResponses = false
        });

        // Health check UI (for development)
        app.UseHealthChecks("/health/ui", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthCheckUI,
            AllowCachingResponses = false
        });

        return app;
    }

    private static async Task WriteHealthCheckResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString(),
            timestamp = DateTime.UtcNow,
            duration = report.TotalDuration,
            info = report.Entries.Where(e => e.Value.Status == HealthStatus.Healthy)
                .ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString() }),
            error = report.Entries.Where(e => e.Value.Status == HealthStatus.Unhealthy)
                .ToDictionary(e => e.Key, e => new
                {
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description
                }),
            details = report.Entries.Where(e => e.Value.Status == HealthStatus.Degraded)
                .ToDictionary(e => e.Key, e => new
                {
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description
                })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }

    private static async Task WriteDetailedHealthCheckResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString(),
            timestamp = DateTime.UtcNow,
            duration = report.TotalDuration.TotalMilliseconds,
            results = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    duration = e.Value.Duration.TotalMilliseconds,
                    description = e.Value.Description,
                    data = e.Value.Data?.ToDictionary(d => d.Key, d => d.Value)
                })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }

    private static async Task WriteHealthCheckUI(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/html";

        var html = GenerateHealthCheckHtml(report);
        await context.Response.WriteAsync(html);
    }

    private static string GenerateHealthCheckHtml(HealthReport report)
    {
        var statusColor = report.Status switch
        {
            HealthStatus.Healthy => "green",
            HealthStatus.Degraded => "orange",
            HealthStatus.Unhealthy => "red",
            _ => "gray"
        };

        var rows = string.Join("", report.Entries.Select(entry =>
        {
            var color = entry.Value.Status switch
            {
                HealthStatus.Healthy => "green",
                HealthStatus.Degraded => "orange",
                HealthStatus.Unhealthy => "red",
                _ => "gray"
            };

            return $@"
                <tr>
                    <td>{entry.Key}</td>
                    <td style='color: {color}; font-weight: bold;'>{entry.Value.Status}</td>
                    <td>{entry.Value.Duration.TotalMilliseconds:F1}ms</td>
                    <td>{System.Net.WebUtility.HtmlEncode(entry.Value.Description) ?? "-"}</td>
                </tr>";
        }));

        return $@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Noelia Health Check</title>
                <style>
                    body {{ font-family: Arial, sans-serif; margin: 40px; }}
                    .status {{ font-size: 24px; font-weight: bold; color: {statusColor}; }}
                    table {{ border-collapse: collapse; width: 100%; margin-top: 20px; }}
                    th, td {{ border: 1px solid #ddd; padding: 8px; text-align: left; }}
                    th {{ background-color: #f2f2f2; }}
                    .refresh {{ margin-top: 20px; }}
                </style>
                <script>
                    function autoRefresh() {{
                        setTimeout(function(){{ location.reload(); }}, 30000);
                    }}
                </script>
            </head>
            <body onload='autoRefresh()'>
                <h1>Noelia Health Check</h1>
                <div class='status'>Status: {report.Status}</div>
                <p>Last Updated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
                <p>Total Duration: {report.TotalDuration.TotalMilliseconds:F1}ms</p>
                
                <table>
                    <thead>
                        <tr>
                            <th>Check Name</th>
                            <th>Status</th>
                            <th>Duration</th>
                            <th>Error</th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows}
                    </tbody>
                </table>
                
                <div class='refresh'>
                    <p><em>This page auto-refreshes every 30 seconds</em></p>
                    <button onclick='location.reload()'>Refresh Now</button>
                </div>
            </body>
            </html>";
    }
}

/// <summary>
/// Builder for configuring health checks
/// </summary>
public class HealthCheckBuilder
{
    private readonly IServiceCollection _services;
    private readonly IHealthChecksBuilder _healthChecksBuilder;

    public HealthCheckBuilder(IServiceCollection services)
    {
        _services = services;
        _healthChecksBuilder = services.AddHealthChecks();
    }

    /// <summary>
    /// The underlying builder, for checks that name a provider.
    /// </summary>
    /// <remarks>
    /// There are deliberately no <c>AddDatabaseHealthCheck</c> or
    /// <c>AddRabbitMqHealthCheck</c> methods here. A check that reaches a
    /// database or a broker has to name one, and this package may not — the
    /// same rule that keeps <c>Noelia.Infrastructure</c> free of drivers
    /// (ADR-0001). Those checks arrive with their provider:
    /// <c>AddDatabase&lt;TContext&gt;(...)</c> from
    /// <c>Noelia.Data.EntityFrameworkCore</c>, <c>AddRedisConnection(...)</c>
    /// from <c>Noelia.Redis</c>, <c>UseMassTransitMessaging(...)</c> from
    /// <c>Noelia.Messaging.MassTransit</c>.
    /// <para>
    /// The three methods that used to sit here returned <c>this</c> over a
    /// commented-out body, so a service could call all three and register
    /// nothing. They were removed in 5.1.0 rather than filled in, because in
    /// this package they can never be filled in.
    /// </para>
    /// </remarks>
    public IHealthChecksBuilder Checks => _healthChecksBuilder;

    /// <summary>
    /// Add custom application health checks
    /// </summary>
    public HealthCheckBuilder AddCustomHealthChecks()
    {
        _healthChecksBuilder
            .AddCheck<ApplicationHealthCheck>("application",
                HealthStatus.Unhealthy,
                tags: new[] { "live", "ready" })
            .AddCheck<CircuitBreakerHealthCheck>("circuit_breakers",
                HealthStatus.Degraded,
                tags: new[] { "resilience" })
            .AddCheck<MemoryHealthCheck>("memory",
                HealthStatus.Degraded,
                tags: new[] { "live", "performance" })
            .AddCheck<DiskSpaceHealthCheck>("disk_space",
                HealthStatus.Degraded,
                tags: new[] { "live", "performance" });

        return this;
    }

    /// <summary>
    /// Add a custom health check
    /// </summary>
    public HealthCheckBuilder AddCustomCheck<T>(
        string name,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        params string[] tags)
        where T : class, IHealthCheck
    {
        _healthChecksBuilder.AddCheck<T>(name, failureStatus, tags);
        return this;
    }

    /// <summary>
    /// Add health check publishing (optional)
    /// </summary>
    public HealthCheckBuilder AddPublisher<T>()
        where T : class, IHealthCheckPublisher
    {
        _services.AddSingleton<IHealthCheckPublisher, T>();
        return this;
    }
}