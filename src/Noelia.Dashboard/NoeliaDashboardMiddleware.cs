using System.Text.Json;
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

    /// <summary>
    /// How the machine-readable report is written.
    /// </summary>
    /// <remarks>
    /// camelCase because the readers are not all .NET, and unindented because
    /// the reader is a program. A consumer that wants the official types
    /// deserialises with these same settings.
    /// </remarks>
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

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

        var isCss = remaining == "/assets/dashboard.css";
        var isScript = remaining == "/assets/dashboard.js";

        // Each section has an address of its own. The router asks the page which
        // ones exist rather than listing them here, so a section cannot be added
        // with navigation that links to a path nothing serves.
        var section = DashboardSection.Overview;
        var isPage = !isCss
            && !isScript
            && DashboardPage.TryParseSection(
                remaining.HasValue ? remaining.Value! : string.Empty, out section);

        // The same reading the page shows, for a reader that is not a person.
        // It sits inside this middleware rather than beside it so that it
        // inherits the authentication, the visibility rule and the audit entry
        // above: a report path that were easier to reach than the page would be
        // a way around the access rule, not a second view of it.
        var isReport = remaining == "/report.json";

        // Verification is a separate request because it is a separate kind of
        // act. Recomputing a chain of millions of entries on every page load
        // would turn opening the dashboard into an attack on the store it
        // reports about, so this is asked for deliberately.
        var isChain = remaining == "/audit-chain.json";

        if (!isPage && !isCss && !isScript && !isReport && !isChain)
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
        context.Response.ContentType = isReport || isChain
            ? "application/json; charset=utf-8"
            : "text/html; charset=utf-8";

        // Before collecting, not after: inspecting every provider to then throw
        // the answer away would let a HEAD request cost what a GET costs.
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        if (isChain)
        {
            var verifier = context.RequestServices.GetService<IAuditChainVerifier>();
            var verification = verifier is null
                ? AuditChainVerification.Unsupported(
                    "No audit chain verifier is registered. AddSovereignAuditTrail() registers one.")
                : await verifier.VerifyAsync(cancellationToken: context.RequestAborted)
                    .ConfigureAwait(false);

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(verification, ReportJson),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var report = await OperatorReportCollector.CollectAsync(
            context,
            options,
            context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System)
            .ConfigureAwait(false);

        if (isReport)
        {
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(report, ReportJson),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await context.Response.WriteAsync(
            DashboardPage.Render(report, options, section),
            context.RequestAborted).ConfigureAwait(false);
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
