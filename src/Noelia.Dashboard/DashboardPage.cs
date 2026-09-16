using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noelia.Abstractions.Audit;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Abstractions.Sovereignty;

namespace Noelia.Dashboard;

internal static class DashboardPage
{
    internal static async Task<string> RenderAsync(
        HttpContext context,
        NoeliaDashboardOptions options)
    {
        var services = context.RequestServices;
        var composition = services.GetRequiredService<NoeliaComposition>();
        var output = new StringBuilder(16_384);

        output.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>Noelia · ").Append(H(options.ServiceName)).Append("</title>")
            .Append("<link rel=\"stylesheet\" href=\"").Append(H(options.Path))
            .Append("/assets/dashboard.css\"></head><body><main>")
            .Append("<header><h1>Noelia</h1><div class=\"identity\">")
            .Append(H(options.ServiceName)).Append(" · instance ")
            .Append(H(options.InstanceName)).Append(" · ")
            .Append(H(options.EnvironmentName)).Append("</div>")
            .Append("<p class=\"muted\">Read-only operator view. Configuration values, tokens, keys and state snapshots are never rendered.</p></header>");

        Composition(output, composition, services.GetService<IServiceProviderIsService>());
        Configuration(output, composition, options);
        Security(output, composition, services.GetService<ISecurityCheckReport>());
        Sovereignty(output, services.GetService<ISovereigntyReport>());
        await Audit(output, services.GetService<IAuditTrailService>(), context.RequestAborted)
            .ConfigureAwait(false);
        await Sessions(output, services.GetService<ITokenSessionService>(), context.RequestAborted)
            .ConfigureAwait(false);
        await RateLimits(output, services.GetService<IDistributedRateLimitStore>(), context.RequestAborted)
            .ConfigureAwait(false);
        await Health(output, services.GetService<HealthCheckService>(), context.RequestAborted)
            .ConfigureAwait(false);

        output.Append("<script src=\"").Append(H(options.Path))
            .Append("/assets/dashboard.js\"></script></main></body></html>");

        return output.ToString();
    }

    private static void Composition(
        StringBuilder output,
        NoeliaComposition composition,
        IServiceProviderIsService? services)
    {
        output.Append("<section><h2>Composition</h2>")
            .Append("<input type=\"search\" aria-label=\"Filter modules\" placeholder=\"Filter modules\" data-filter=\"#modules tbody tr\">")
            .Append("<table id=\"modules\"><thead><tr><th>Module</th><th>State</th><th>Decision</th></tr></thead><tbody>");

        foreach (var module in composition.Included)
        {
            output.Append("<tr><td>").Append(H(module.Name))
                .Append("</td><td class=\"active\">running</td><td>selected</td></tr>");
        }

        foreach (var (module, reason) in composition.Excluded.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            output.Append("<tr><td>").Append(H(module.Name))
                .Append("</td><td class=\"excluded\">not running</td><td>")
                .Append(H(reason)).Append("</td></tr>");
        }

        output.Append("</tbody></table>");

        foreach (var module in composition.Included)
        {
            if (!composition.Contracts.TryGetValue(module, out var contract))
            {
                continue;
            }

            output.Append("<h3>").Append(H(module.Name)).Append(" contract</h3>");

            if (contract.Requirements.Count == 0 && contract.Provisions.Count == 0)
            {
                output.Append("<p class=\"warning\">Running, but its contract declares no requirement or provided effect.</p>");
                continue;
            }

            output.Append("<table><thead><tr><th>Direction</th><th>Service</th><th>State</th><th>Details</th></tr></thead><tbody>");

            foreach (var requirement in contract.Requirements)
            {
                var present = services?.IsService(requirement.ServiceType) == true;
                output.Append("<tr><td>requires</td><td><code>")
                    .Append(H(requirement.ServiceType.Name)).Append("</code></td><td class=\"")
                    .Append(present ? "pass\">present" : "missing\">missing")
                    .Append("</td><td>")
                    .Append(H(string.Join(" or ", requirement.Providers)))
                    .Append("</td></tr>");
            }

            foreach (var provision in contract.Provisions)
            {
                var present = services?.IsService(provision.ServiceType) == true;
                var readers = composition.Contracts.Values
                    .Where(other => other.Module != module)
                    .Where(other => other.Requirements.Any(
                        requirement => requirement.ServiceType == provision.ServiceType))
                    .Select(other => other.Module.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                output.Append("<tr><td>provides</td><td><code>")
                    .Append(H(provision.ServiceType.Name)).Append("</code></td><td class=\"")
                    .Append(present ? "pass\">registered" : "missing\">not registered")
                    .Append("</td><td>")
                    .Append(readers.Length == 0
                        ? "No active module declares that it reads this service."
                        : $"Read by {H(string.Join(", ", readers))}.")
                    .Append("</td></tr>");
            }

            output.Append("</tbody></table>");
        }

        output.Append("</section>");
    }

    private static void Configuration(
        StringBuilder output,
        NoeliaComposition composition,
        NoeliaDashboardOptions options)
    {
        var requested = composition.Included.Select(module => module.Name)
            .Concat(options.ConfigurationSections)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        output.Append("<section><h2>Configuration shapes</h2>")
            .Append("<p class=\"muted\">Only keys and shapes are shown. A shape is derived in memory and the value is discarded.</p>")
            .Append("<table><thead><tr><th>Section</th><th>Key</th><th>Shape</th></tr></thead><tbody>");

        foreach (var sectionName in requested)
        {
            var section = options.Configuration.GetSection(sectionName);
            var leaves = Leaves(section).Take(201).ToArray();

            if (leaves.Length == 0)
            {
                output.Append("<tr><td>").Append(H(sectionName))
                    .Append("</td><td>—</td><td>no explicit value</td></tr>");
                continue;
            }

            foreach (var leaf in leaves.Take(200))
            {
                output.Append("<tr><td>").Append(H(sectionName)).Append("</td><td>")
                    .Append(H(RelativeKey(section, leaf))).Append("</td><td>")
                    .Append(H(ValueShape(leaf.Value))).Append("</td></tr>");
            }

            if (leaves.Length > 200)
            {
                output.Append("<tr><td>").Append(H(sectionName))
                    .Append("</td><td>…</td><td>more than 200 keys; remaining shapes omitted</td></tr>");
            }
        }

        output.Append("</tbody></table>");

        if (options.ProductionReason is not null)
        {
            // The wording, not a character count. InProduction(reason) already
            // gates composition, so the reason is written down in every case —
            // but until 5.1.0 it was rendered as "recorded (N characters)" and
            // could therefore say anything at all without anyone reading it.
            // An operator looking at this page is exactly the person who has to
            // judge whether the stated reason still holds.
            output.Append("<p>Production exposure reason: ")
                .Append(H(options.ProductionReason))
                .Append("</p>");
        }

        output.Append("</section>");
    }

    private static void Security(
        StringBuilder output,
        NoeliaComposition composition,
        ISecurityCheckReport? report)
    {
        output.Append("<section><h2>Security checks</h2>");

        if (report is null)
        {
            output.Append("<p class=\"not-applicable\">No security-check report is registered.</p></section>");
            return;
        }

        var active = composition.Included.ToHashSet();
        active.Add(NoeliaModule.Composition);
        var results = report.Latest.Where(result => active.Contains(result.Module)).ToArray();

        if (results.Length == 0)
        {
            output.Append("<p class=\"not-applicable\">No completed check exists for an active module.</p></section>");
            return;
        }

        output.Append("<table><thead><tr><th>Check</th><th>Module</th><th>Status</th><th>Finding</th><th>Remediation</th></tr></thead><tbody>");
        foreach (var result in results)
        {
            var status = result.Status.ToString().ToLowerInvariant();
            output.Append("<tr><td><code>").Append(H(result.Id)).Append("</code></td><td>")
                .Append(H(result.Module.Name)).Append("</td><td class=\"")
                .Append(H(status)).Append("\">").Append(H(result.Status.ToString()))
                .Append(" · ").Append(H(result.Severity.ToString())).Append("</td><td>")
                .Append(H(result.Summary)).Append("</td><td>")
                .Append(H(result.Remediation)).Append("</td></tr>");
        }

        output.Append("</tbody></table></section>");
    }

    private static void Sovereignty(StringBuilder output, ISovereigntyReport? report)
    {
        output.Append("<section><h2>Sovereignty and egress</h2>");
        if (report is null)
        {
            output.Append("<p class=\"not-applicable\">No sovereignty report is registered.</p></section>");
            return;
        }

        SovereigntyAssessment assessment;
        try
        {
            assessment = report.Assess();
        }
        catch
        {
            output.Append("<p class=\"fail\">The sovereignty report could not be read.</p></section>");
            return;
        }

        output.Append("<p>Egress enforcement: <span class=\"")
            .Append(assessment.EgressIsEnforced ? "pass\">active" : "warning\">not active")
            .Append("</span></p><p>Declared hosts: ")
            .Append(assessment.DeclaredEgressHosts.Count == 0
                ? "none"
                : H(string.Join(", ", assessment.DeclaredEgressHosts.Order(StringComparer.Ordinal))))
            .Append("</p><table><thead><tr><th>Dependency</th><th>Host</th><th>Assessment</th><th>Reason</th></tr></thead><tbody>");

        foreach (var dependency in assessment.Dependencies)
        {
            var css = dependency.Jurisdiction == Jurisdiction.SelfHosted ? "pass" : "warning";
            output.Append("<tr><td>").Append(H(dependency.Name)).Append("</td><td>")
                .Append(H(dependency.Host ?? "not configured")).Append("</td><td class=\"")
                .Append(css).Append("\">").Append(H(dependency.Jurisdiction.ToString()))
                .Append("</td><td>").Append(H(dependency.Note)).Append("</td></tr>");
        }

        output.Append("</tbody></table><p class=\"muted\">Undetermined is not a pass; a hostname cannot prove jurisdiction.</p></section>");
    }

    private static async Task Audit(
        StringBuilder output,
        IAuditTrailService? audit,
        CancellationToken cancellationToken)
    {
        output.Append("<section><h2>Audit trail</h2>");
        if (audit is null)
        {
            output.Append("<p class=\"warning\">No audit trail is registered; the dashboard access check reports this.</p></section>");
            return;
        }

        AuditTrailInspection inspection;
        try
        {
            inspection = await audit.InspectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            output.Append("<p class=\"fail\">The audit trail could not be inspected.</p></section>");
            return;
        }

        if (!inspection.IsAvailable)
        {
            output.Append("<p class=\"not-applicable\">Access was recorded, but this audit provider exposes no read model.</p></section>");
            return;
        }

        output.Append("<p>Instance chain at write time: <span class=\"")
            .Append(inspection.IsChainValidAtWriteTime ? "pass\">valid" : "fail\">invalid")
            .Append("</span> · persisted sink: ")
            .Append(inspection.VerifiesPersistedSink ? "verified" : "not verified by this provider")
            .Append(" · ")
            .Append(inspection.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" entries</p><table><thead><tr><th>Time</th><th>Actor</th><th>Capacity</th><th>Action</th><th>Resource</th></tr></thead><tbody>");

        foreach (var entry in inspection.Latest)
        {
            output.Append("<tr><td>").Append(H(entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</td><td>").Append(H(entry.ActorId))
                .Append("</td><td>").Append(H(entry.Capacity))
                .Append("</td><td>").Append(H(entry.Action))
                .Append("</td><td>").Append(H(entry.Resource)).Append("</td></tr>");
        }

        output.Append("</tbody></table><p class=\"muted\">This is the chain of the instance named above, not a cluster-wide claim.</p></section>");
    }

    private static async Task Health(
        StringBuilder output,
        HealthCheckService? health,
        CancellationToken cancellationToken)
    {
        output.Append("<section><h2>Health</h2>");
        if (health is null)
        {
            output.Append("<p class=\"not-applicable\">No health-check service is registered.</p></section>");
            return;
        }

        HealthReport report;
        try
        {
            report = await health.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            output.Append("<p class=\"fail\">Health checks could not complete.</p></section>");
            return;
        }

        output.Append("<p>Overall: <span class=\"")
            .Append(report.Status == HealthStatus.Healthy ? "pass" : "warning")
            .Append("\">").Append(H(report.Status.ToString())).Append("</span></p>")
            .Append("<table><thead><tr><th>Check</th><th>Status</th><th>Duration</th><th>Tags</th></tr></thead><tbody>");

        foreach (var (name, entry) in report.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            output.Append("<tr><td>").Append(H(name)).Append("</td><td>")
                .Append(H(entry.Status.ToString())).Append("</td><td>")
                .Append(H(entry.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)))
                .Append(" ms</td><td>").Append(H(string.Join(", ", entry.Tags))).Append("</td></tr>");
        }

        output.Append("</tbody></table></section>");
    }

    private static async Task Sessions(
        StringBuilder output,
        ITokenSessionService? sessions,
        CancellationToken cancellationToken)
    {
        output.Append("<section><h2>Sessions</h2>");
        if (sessions is null)
        {
            output.Append("<p class=\"not-applicable\">No token-session service is registered.</p></section>");
            return;
        }

        TokenSessionInspection inspection;
        try
        {
            inspection = await sessions.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            output.Append("<p class=\"fail\">Sessions could not be inspected.</p></section>");
            return;
        }

        if (!inspection.IsAvailable)
        {
            output.Append("<p class=\"not-applicable\">The session provider exposes no safe operator read model.</p></section>");
            return;
        }

        output.Append("<p>")
            .Append(inspection.Sessions.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" active sessions observed by this instance.</p>")
            .Append("<table><thead><tr><th>Subject</th><th>Session</th><th>Started</th><th>Last used</th><th>Expires</th><th>Device shape</th></tr></thead><tbody>");

        foreach (var session in inspection.Sessions)
        {
            output.Append("<tr><td>").Append(H(session.Subject))
                .Append("</td><td>").Append(H(session.Session))
                .Append("</td><td>").Append(H(session.StartedAt.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</td><td>").Append(H(session.LastUsedAt.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</td><td>").Append(H(session.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</td><td>").Append(H(session.ClientFingerprintShape)).Append("</td></tr>");
        }

        output.Append("</tbody></table><p class=\"muted\">Tokens and raw device fingerprints are never available here. This is not a cluster-wide inventory.</p></section>");
    }

    private static async Task RateLimits(
        StringBuilder output,
        IDistributedRateLimitStore? store,
        CancellationToken cancellationToken)
    {
        output.Append("<section><h2>Rate limits</h2>");
        if (store is null)
        {
            output.Append("<p class=\"not-applicable\">No rate-limit store is registered.</p></section>");
            return;
        }

        RateLimitInspection inspection;
        try
        {
            inspection = await store.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            output.Append("<p class=\"fail\">Rate-limit counters could not be inspected.</p></section>");
            return;
        }

        if (!inspection.IsAvailable)
        {
            output.Append("<p class=\"not-applicable\">The active store exposes no safe counter read model.</p></section>");
            return;
        }

        output.Append("<table><thead><tr><th>Key fingerprint</th><th>Count</th><th>Limit</th><th>Decision</th><th>Observed</th></tr></thead><tbody>");
        foreach (var counter in inspection.Counters)
        {
            output.Append("<tr><td><code>").Append(H(counter.KeyFingerprint))
                .Append("</code></td><td>").Append(counter.CurrentCount.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(counter.Limit?.ToString(CultureInfo.InvariantCulture) ?? "not recorded")
                .Append("</td><td class=\"").Append(counter.IsRejected ? "fail\">rejected" : "pass\">allowed")
                .Append("</td><td>").Append(H(counter.ObservedAt.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</td></tr>");
        }

        output.Append("</tbody></table><p class=\"muted\">Store keys can contain subjects or addresses; only a one-way 12-character fingerprint is shown.</p></section>");
    }

    private static IEnumerable<IConfigurationSection> Leaves(IConfigurationSection section)
    {
        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
        {
            if (section.Value is not null)
            {
                yield return section;
            }

            yield break;
        }

        foreach (var child in children)
        {
            foreach (var leaf in Leaves(child))
            {
                yield return leaf;
            }
        }
    }

    private static string RelativeKey(IConfigurationSection root, IConfigurationSection leaf) =>
        leaf.Path.Length > root.Path.Length + 1
            ? leaf.Path[(root.Path.Length + 1)..]
            : leaf.Key;

    private static string ValueShape(string? value) =>
        value is null ? "missing" : "set";

    private static string H(string value) => HtmlEncoder.Default.Encode(value);
}
