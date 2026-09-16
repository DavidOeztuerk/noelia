using Noelia.Infrastructure.Http;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Caching.Http;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Middleware;
using Noelia.Infrastructure.Observability;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Headers;
using Noelia.Infrastructure.Security.InputSanitization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Noelia.Infrastructure.Builder.Modules;

/// <summary>
/// One pipeline step per method, on <see cref="InfrastructureMiddlewareBuilder"/>.
/// </summary>
/// <remarks>
/// Each step names the module it belongs to and adds nothing when that module was
/// left out — so <c>Without(module, reason)</c> is decided once, on the service
/// side, and holds here too.
/// </remarks>
public static class MiddlewarePipelineModule
{
    /// <summary>The response headers a browser is told to enforce.</summary>
    public static InfrastructureMiddlewareBuilder UseSecurityHeaders(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.SecurityHeaders, step =>
        {
            step.Requires<ISecurityHeadersService>("UseSecurityHeaders()", "AddSecurityHeaders()");
            step.App.UseMiddleware<SecurityHeadersMiddleware>();
        });

    public static InfrastructureMiddlewareBuilder UseCorrelationId(this InfrastructureMiddlewareBuilder builder)
    {
        builder.App.UseMiddleware<CorrelationIdMiddleware>();
        return builder;
    }

    public static InfrastructureMiddlewareBuilder UseRequestLogging(this InfrastructureMiddlewareBuilder builder)
    {
        builder.App.UseMiddleware<RequestLoggingMiddleware>();
        return builder;
    }

    /// <summary>Traces and the performance counters that hang off them.</summary>
    public static InfrastructureMiddlewareBuilder UseTelemetry(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.Observability, step =>
        {
            step.Requires<IPerformanceMetrics>("UseTelemetry()", "AddObservability()");
            step.App.UseTelemetry();
            step.App.UsePerformanceMonitoring();
        });

    public static InfrastructureMiddlewareBuilder UseExceptionHandling(this InfrastructureMiddlewareBuilder builder)
    {
        builder.App.UseMiddleware<GlobalExceptionHandlingMiddleware>();
        return builder;
    }

    /// <summary>Refuses requests carrying injection syntax.</summary>
    public static InfrastructureMiddlewareBuilder UseInputSanitization(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.InputSanitization, step =>
        {
            step.Requires<IInputSanitizer>("UseInputSanitization()", "AddInputSanitization()");
            step.App.UseMiddleware<InputSanitizationMiddleware>();
        });

    /// <summary>Serilog's own request log line.</summary>
    public static InfrastructureMiddlewareBuilder UseSerilogLogging(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.Logging, step =>
        step.App.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value ?? "");
                diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.FirstOrDefault() ?? "");
                diagnosticContext.Set("RemoteIP", httpContext.Connection.RemoteIpAddress?.ToString() ?? "");

                if (httpContext.User?.Identity?.IsAuthenticated == true)
                {
                    diagnosticContext.Set("UserId", httpContext.User.FindFirst("sub")?.Value ?? "");
                }
            };
        }));

    /// <summary>The cross-origin rules the service was configured with.</summary>
    public static InfrastructureMiddlewareBuilder UseCors(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.Cors, step => step.App.UseCors());

    /// <summary>Swagger, in development only.</summary>
    public static InfrastructureMiddlewareBuilder UseSwagger(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.ApiDocumentation, step =>
        {
            if (step.Environment.IsDevelopment())
            {
                step.App.UseSwaggerDocumentation(step.ServiceName);
            }
        });

    /// <summary>
    /// Adds distributed rate limiting to the pipeline. Requires an
    /// <c>IDistributedRateLimitStore</c>, registered by <c>AddCaching</c>.
    /// </summary>
    public static InfrastructureMiddlewareBuilder UseRateLimiting(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.RateLimiting, step =>
        {
            step.Requires<IDistributedRateLimitStore>(
                "UseRateLimiting()", "AddInMemoryCache(prefix) or AddRedisCache(prefix)");
            step.App.UseMiddleware<DistributedRateLimitingMiddleware>();
        });

    /// <summary>
    /// Rewrites the connection's address and scheme from the headers a named
    /// proxy set.
    /// </summary>
    /// <remarks>
    /// Added only when <c>TrustForwardedHeadersFrom(...)</c> named at least one
    /// proxy. Until 5.1.0 that call configured
    /// <see cref="Microsoft.AspNetCore.Builder.ForwardedHeadersOptions"/> and
    /// nothing applied them: Noelia's pipeline never ran the middleware, so
    /// <c>ClientAddress.Of</c> — and with it the rate limiter's buckets, the
    /// audit trail's addresses and every security alert — kept naming the proxy.
    /// A service behind TLS termination also kept seeing <c>http</c>, which is
    /// how a cookie asking for <c>Secure</c> ends up without it.
    /// <para>
    /// It must run before anything that reads either value, so it belongs at
    /// the front of the chain. An application that already called
    /// <c>UseForwardedHeaders()</c> itself should drop that call: two passes
    /// consume two entries of <c>X-Forwarded-For</c>.
    /// </para>
    /// </remarks>
    public static InfrastructureMiddlewareBuilder UseForwardedHeaders(
        this InfrastructureMiddlewareBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.App.ApplicationServices.GetService<ForwardedHeaderTrust>() is null)
        {
            return builder;
        }

        builder.App.UseForwardedHeaders();
        return builder;
    }

    /// <summary>Liveness and readiness endpoints.</summary>
    public static InfrastructureMiddlewareBuilder UseHealthCheckEndpoints(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.HealthChecks, step =>
        {
        var healthCheckOptions = new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var response = new
                {
                    status = report.Status.ToString(),
                    timestamp = DateTime.UtcNow,
                    durationMs = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        durationMs = e.Value.Duration.TotalMilliseconds,
                        tags = e.Value.Tags,

                        // The check's own words, never the exception's. Until
                        // 5.1.0 this served e.Value.Exception?.Message to
                        // whoever asked — and a driver's connection failure
                        // names the host, the port and sometimes the
                        // credentials it was using. /health answers anonymously
                        // wherever it is exposed; a probe that discloses the
                        // infrastructure behind it on failure is a probe that
                        // rewards knocking.
                        //
                        // A check that wants to say more says it in its own
                        // description, where its author decided what is safe.
                        // The exception itself is logged.
                        description = e.Value.Description
                    })
                };
                await context.Response.WriteAsync(
                    System.Text.Json.JsonSerializer.Serialize(response,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        };

        step.App.UseHealthChecks("/health", healthCheckOptions);

        step.App.UseHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
            ResponseWriter = healthCheckOptions.ResponseWriter,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status200OK
            }
        });

        step.App.UseHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = healthCheckOptions.ResponseWriter,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        });
        });

    /// <summary>Authentication, then authorization.</summary>
    /// <remarks>
    /// Not gated on a module: which scheme establishes a caller is not something
    /// the module set decides — <c>UseJwt(...)</c> answers it, and so does any
    /// <c>AddAuthentication(...)</c> the service wrote itself. What it does do is
    /// say which call is missing, rather than letting the framework report an
    /// unresolvable <c>IAuthenticationSchemeProvider</c> naming a type the reader
    /// never wrote.
    /// </remarks>
    public static InfrastructureMiddlewareBuilder UseAuth(this InfrastructureMiddlewareBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Requires<IAuthenticationSchemeProvider>(
            "UseAuth()", "UseJwt(...) while configuring Noelia, or AddAuthentication(...)");
        builder.App.UseAuthentication();
        builder.App.UseAuthorization();
        return builder;
    }

    /// <summary>The security audit trail.</summary>
    public static InfrastructureMiddlewareBuilder UseSecurityAudit(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.Audit, step =>
        {
            step.Requires<ISecurityAuditLogger>("UseSecurityAudit()", "AddAuditLogging()");
            step.App.UseMiddleware<SecurityAuditMiddleware>();
        });

    /// <summary>
    /// Refuses every request no permission covers.
    /// </summary>
    /// <remarks>
    /// Fail-closed, and right as a default for a service whose whole surface is
    /// behind a token. A service with a public surface leaves it out with
    /// <c>Without(NoeliaModule.PermissionEnforcement, reason)</c> and keeps
    /// <see cref="NoeliaModule.Authorization"/>, which is what answers
    /// <c>[RequirePermission]</c> on the endpoints that carry it.
    /// </remarks>
    public static InfrastructureMiddlewareBuilder UsePermissions(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.PermissionEnforcement, step => step.App.UsePermissionMiddleware());

    /// <summary>
    /// Refuses tokens that were withdrawn. Place after <see cref="UseAuth"/>,
    /// which establishes the claims it reads.
    /// </summary>
    /// <remarks>
    /// Fails while composing when no evaluator is registered — a revocation
    /// check that silently answers "not revoked" is indistinguishable from one
    /// that works.
    /// </remarks>
    public static InfrastructureMiddlewareBuilder UseTokenRevocation(
        this InfrastructureMiddlewareBuilder builder)
    {
        builder.App.UseTokenRevocation();
        return builder;
    }

    /// <summary>
    /// ETags and conditional responses.
    /// </summary>
    /// <remarks>
    /// <see cref="NoeliaModule.HttpResponseCaching"/> is not in the default set,
    /// so this step is in the default chain but does nothing until a service asks
    /// for the module. It used to run unconditionally, which made the default
    /// chain contradict the default modules.
    /// </remarks>
    public static InfrastructureMiddlewareBuilder UseHttpCaching(this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.HttpResponseCaching, step =>
        {
            step.Requires<ICachePolicyProvider>("UseHttpCaching()", "AddCaching()");
            step.App.UseHttpResponseCachingHeaders();
        });
}
