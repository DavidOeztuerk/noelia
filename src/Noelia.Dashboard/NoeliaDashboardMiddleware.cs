using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Audit;
using Noelia.Abstractions.Observability;

namespace Noelia.Dashboard;

internal sealed class NoeliaDashboardStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<NoeliaDashboardMiddleware>();
        next(app);
    };
}

internal sealed class NoeliaDashboardMiddleware(
    RequestDelegate next,
    NoeliaDashboardOptions options)
{
    private const string CssResource = "Noelia.Dashboard.Assets.dashboard.css";
    private const string JsResource = "Noelia.Dashboard.Assets.dashboard.js";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(options.Path, out var remaining))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method))
        {
            await NotFound(context).ConfigureAwait(false);
            return;
        }

        if (!await AuthenticateIfAvailable(context).ConfigureAwait(false)
            || !IsVisible(context))
        {
            await NotFound(context).ConfigureAwait(false);
            return;
        }

        ApplySecurityHeaders(context.Response);

        var isPage = !remaining.HasValue || remaining == "/";
        var isCss = remaining == "/assets/dashboard.css";
        var isScript = remaining == "/assets/dashboard.js";
        if (!isPage && !isCss && !isScript)
        {
            await NotFound(context).ConfigureAwait(false);
            return;
        }

        if (!await RecordAccess(context).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (isCss)
        {
            await Embedded(context, CssResource, "text/css; charset=utf-8").ConfigureAwait(false);
            return;
        }

        if (isScript)
        {
            await Embedded(context, JsResource, "text/javascript; charset=utf-8").ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(
                await DashboardPage.RenderAsync(context, options).ConfigureAwait(false),
                context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task<bool> AuthenticateIfAvailable(HttpContext context)
    {
        var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
        if (schemes is null)
        {
            return true;
        }

        try
        {
            var scheme = await schemes.GetDefaultAuthenticateSchemeAsync().ConfigureAwait(false);
            if (scheme is null)
            {
                return true;
            }

            var result = await context.AuthenticateAsync(scheme.Name).ConfigureAwait(false);
            if (result.Principal is not null)
            {
                context.User = result.Principal;
            }

            return true;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Authentication failures must not reveal that the dashboard exists.
            return false;
        }
    }

    private bool IsVisible(HttpContext context)
    {
        if (options.Visibility is null)
        {
            return false;
        }

        try
        {
            return options.Visibility(context);
        }
        catch
        {
            // Authorization code is application code. A broken policy must not
            // turn invisibility into a 500 that confirms the surface exists.
            return false;
        }
    }

    private static async Task<bool> RecordAccess(HttpContext context)
    {
        var audit = context.RequestServices.GetService<IAuditTrailService>();
        if (audit is null)
        {
            return true;
        }

        var actor = context.User.FindFirst("sub")?.Value
                    ?? context.User.Identity?.Name
                    ?? "anonymous-operator";

        try
        {
            await audit.RecordAsync<object>(
                actor,
                "operator",
                "Viewed",
                "Noelia.Dashboard",
                correlationId: CorrelationOf(context),
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // If an installed audit trail cannot record the access, do not
            // reveal the operational inventory it was meant to account for.
            return false;
        }
    }

    /// <summary>
    /// The id that ties this view to the rest of its request, across services.
    /// </summary>
    /// <remarks>
    /// Until 5.2.0 this recorded <see cref="HttpContext.TraceIdentifier"/> — a
    /// per-connection request id that is local to this process. Under the name
    /// <c>correlationId</c> it correlated nothing: an investigator holding an
    /// audit entry could not find the request that produced it in any other
    /// service's log.
    /// <para>
    /// The header is read directly because this middleware runs ahead of the
    /// application's own pipeline — the dashboard is installed by a startup
    /// filter so that it is reachable whatever the application configures, and
    /// that puts it in front of <c>UseCorrelationId()</c>. Where the ambient id
    /// has already been established it wins; the trace identifier stays as the
    /// last resort, because an entry with some id beats an entry with none.
    /// </para>
    /// </remarks>
    private static string CorrelationOf(HttpContext context)
    {
        if (CorrelationId.Current is { Length: > 0 } ambient)
        {
            return ambient;
        }

        var header = context.Request.Headers[CorrelationId.HeaderName].ToString();

        return string.IsNullOrWhiteSpace(header) ? context.TraceIdentifier : header;
    }

    private static void ApplySecurityHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        response.Headers.Pragma = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'self'; script-src 'self'; "
            + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }

    private static async Task Embedded(HttpContext context, string resource, string contentType)
    {
        var stream = typeof(NoeliaDashboardMiddleware).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using (stream)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = contentType;

            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
