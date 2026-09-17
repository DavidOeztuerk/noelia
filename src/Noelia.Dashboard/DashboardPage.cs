using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Noelia.Abstractions.Operator;

namespace Noelia.Dashboard;

/// <summary>
/// Draws an <see cref="OperatorReport"/> as the page an operator reads.
/// </summary>
/// <remarks>
/// <para>This renders a report and queries nothing. Until 6.0.0 it read the
/// registered services itself and wrote HTML as it went, which left no model to
/// hand to anything that is not a browser — and meant a second reader had to
/// re-query the same services along a second code path that could disagree with
/// this one without either side noticing.</para>
///
/// <para>The explanatory sentences live here rather than in the model, because
/// they are presentation: they tell a reader how to interpret what they are
/// seeing. A collector consuming the JSON has no use for them, and a data model
/// that carried them would be making a claim about its audience.</para>
/// </remarks>
internal static class DashboardPage
{
    internal static string Render(OperatorReport report, NoeliaDashboardOptions options)
    {
        var output = new StringBuilder(16_384);

        output.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>Noelia · ").Append(H(report.Service)).Append("</title>")
            .Append("<link rel=\"stylesheet\" href=\"").Append(H(options.Path))
            .Append("/assets/dashboard.css\"></head><body><main>")
            .Append("<header><h1>Noelia</h1><div class=\"identity\">")
            .Append(H(report.Service)).Append(" · instance ")
            .Append(H(report.Instance)).Append(" · ")
            .Append(H(report.Environment));

        if (report.Fleet is { } fleet)
        {
            output.Append(" · fleet ").Append(H(fleet));
        }

        output.Append("</div>")
            .Append("<p class=\"muted\">Read-only operator view. Configuration values, tokens, keys and state snapshots are never rendered. ")
            .Append("Times are shown in <span data-zone>UTC</span>.</p></header>");

        Composition(output, report.Composition);
        Configuration(output, report.Configuration);
        Security(output, report.SecurityChecks);
        Sovereignty(output, report.Sovereignty);
        Audit(output, report.Audit);
        Sessions(output, report.Sessions);
        RateLimits(output, report.RateLimits);
        Health(output, report.Health);

        output.Append("<script src=\"").Append(H(options.Path))
            .Append("/assets/dashboard.js\"></script></main></body></html>");

        return output.ToString();
    }

    private static void Composition(StringBuilder output, CompositionView composition)
    {
        output.Append("<section><h2>Composition</h2>")
            .Append("<input type=\"search\" aria-label=\"Filter modules\" placeholder=\"Filter modules\" data-filter=\"#modules tbody tr\">")
            .Append("<table id=\"modules\"><thead><tr><th>Module</th><th>State</th><th>Decision</th></tr></thead><tbody>");

        foreach (var module in composition.Modules)
        {
            output.Append("<tr><td>").Append(H(module.Name)).Append("</td><td class=\"")
                .Append(module.IsRunning ? "active\">running" : "excluded\">not running")
                .Append("</td><td>").Append(H(module.Decision)).Append("</td></tr>");
        }

        output.Append("</tbody></table>");

        foreach (var contract in composition.Contracts)
        {
            output.Append("<h3>").Append(H(contract.Module)).Append(" contract</h3>");

            if (contract.Requirements.Count == 0 && contract.Provisions.Count == 0)
            {
                // Three states, and only one of them is a finding. A module can
                // register nothing on purpose — a pipeline step has no service
                // to offer — and until 5.1.0 that read exactly like a contract
                // nobody had written. An operator cannot act on a warning that
                // turns out to be the design.
                output.Append(contract.RegistersNothingBecause is { } reason
                    ? "<p>Registers nothing, by design: " + H(reason) + "</p>"
                    : "<p class=\"warning\">Running, but its contract declares no requirement "
                      + "or provided effect.</p>");
                continue;
            }

            output.Append("<table><thead><tr><th>Direction</th><th>Service</th><th>State</th><th>Details</th></tr></thead><tbody>");

            foreach (var requirement in contract.Requirements)
            {
                output.Append("<tr><td>requires</td><td><code>")
                    .Append(H(requirement.ServiceType)).Append("</code></td><td class=\"")
                    .Append(requirement.IsPresent ? "pass\">present" : "missing\">missing")
                    .Append("</td><td>")
                    .Append(H(string.Join(" or ", requirement.Providers)))
                    .Append("</td></tr>");
            }

            foreach (var provision in contract.Provisions)
            {
                output.Append("<tr><td>provides</td><td><code>")
                    .Append(H(provision.ServiceType)).Append("</code></td><td class=\"")
                    .Append(provision.IsRegistered ? "pass\">registered" : "missing\">not registered")
                    .Append("</td><td>")
                    .Append(provision.ReadBy.Count == 0
                        ? "No active module declares that it reads this service."
                        : $"Read by {H(string.Join(", ", provision.ReadBy))}.")
                    .Append("</td></tr>");
            }

            output.Append("</tbody></table>");
        }

        output.Append("</section>");
    }

    private static void Configuration(StringBuilder output, ConfigurationView configuration)
    {
        output.Append("<section><h2>Configuration shapes</h2>")
            .Append("<p class=\"muted\">Only keys and shapes are shown. A shape is derived in memory and the value is discarded.</p>")
            .Append("<table><thead><tr><th>Section</th><th>Key</th><th>Shape</th></tr></thead><tbody>");

        foreach (var shape in configuration.Shapes)
        {
            output.Append("<tr><td>").Append(H(shape.Section)).Append("</td><td>")
                .Append(shape.Key is null ? "—" : H(shape.Key)).Append("</td><td>")
                .Append(H(shape.Shape)).Append("</td></tr>");
        }

        output.Append("</tbody></table>");

        if (configuration.ProductionReason is not null)
        {
            // The wording, not a character count. InProduction(reason) already
            // gates composition, so the reason is written down in every case —
            // but until 5.1.0 it was rendered as "recorded (N characters)" and
            // could therefore say anything at all without anyone reading it.
            // An operator looking at this page is exactly the person who has to
            // judge whether the stated reason still holds.
            output.Append("<p>Production exposure reason: ")
                .Append(H(configuration.ProductionReason))
                .Append("</p>");
        }

        output.Append("</section>");
    }

    private static void Security(StringBuilder output, SecurityCheckView checks)
    {
        output.Append("<section><h2>Security checks</h2>");

        if (Note(output, checks.State, checks.Note))
        {
            output.Append("</section>");
            return;
        }

        output.Append("<table><thead><tr><th>Check</th><th>Module</th><th>Status</th><th>Finding</th><th>Remediation</th></tr></thead><tbody>");

        foreach (var result in checks.Results)
        {
            output.Append("<tr><td><code>").Append(H(result.Id)).Append("</code></td><td>")
                .Append(H(result.Module)).Append("</td><td class=\"")
                .Append(H(result.Status.ToLowerInvariant())).Append("\">").Append(H(result.Status))
                .Append(" · ").Append(H(result.Severity)).Append("</td><td>")
                .Append(H(result.Summary)).Append("</td><td>")
                .Append(H(result.Remediation)).Append("</td></tr>");
        }

        output.Append("</tbody></table></section>");
    }

    private static void Sovereignty(StringBuilder output, SovereigntyView sovereignty)
    {
        output.Append("<section><h2>Sovereignty and egress</h2>");

        if (Note(output, sovereignty.State, sovereignty.Note))
        {
            output.Append("</section>");
            return;
        }

        output.Append("<p>Egress enforcement: <span class=\"")
            .Append(sovereignty.EgressIsEnforced ? "pass\">active" : "warning\">not active")
            .Append("</span></p><p>Declared hosts: ")
            .Append(sovereignty.DeclaredHosts.Count == 0
                ? "none"
                : H(string.Join(", ", sovereignty.DeclaredHosts)))
            .Append("</p><table><thead><tr><th>Dependency</th><th>Host</th><th>Assessment</th><th>Reason</th></tr></thead><tbody>");

        foreach (var dependency in sovereignty.Dependencies)
        {
            output.Append("<tr><td>").Append(H(dependency.Name)).Append("</td><td>")
                .Append(H(dependency.Host ?? "not configured")).Append("</td><td class=\"")
                .Append(dependency.Jurisdiction == "SelfHosted" ? "pass" : "warning")
                .Append("\">").Append(H(dependency.Jurisdiction))
                .Append("</td><td>").Append(H(dependency.Note)).Append("</td></tr>");
        }

        output.Append("</tbody></table><p class=\"muted\">Undetermined is not a pass; a hostname cannot prove jurisdiction.</p></section>");
    }

    private static void Audit(StringBuilder output, AuditView audit)
    {
        output.Append("<section><h2>Audit trail</h2>");

        // The one section where a missing provider is a warning rather than a
        // note: the dashboard records who looked at it, and refuses to answer
        // when it cannot. An absent trail here is a finding, not a choice.
        if (audit.State is OperatorSectionState.Absent)
        {
            output.Append("<p class=\"warning\">").Append(H(audit.Note ?? string.Empty))
                .Append("</p></section>");
            return;
        }

        if (Note(output, audit.State, audit.Note))
        {
            output.Append("</section>");
            return;
        }

        output.Append("<p>Instance chain at write time: <span class=\"")
            .Append(audit.IsChainValidAtWriteTime ? "pass\">valid" : "fail\">invalid")
            .Append("</span> · persisted sink: ")
            .Append(audit.VerifiesPersistedSink ? "verified" : "not verified by this provider")
            .Append(" · ")
            .Append(audit.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" entries</p><table><thead><tr><th>Time</th><th>Actor</th><th>Capacity</th><th>Action</th><th>Resource</th></tr></thead><tbody>");

        foreach (var entry in audit.Latest)
        {
            output.Append("<tr><td>");
            Moment(output, entry.Timestamp);
            output.Append("</td><td>").Append(H(entry.ActorId))
                .Append("</td><td>").Append(H(entry.Capacity))
                .Append("</td><td>").Append(H(entry.Action))
                .Append("</td><td>").Append(H(entry.Resource)).Append("</td></tr>");
        }

        output.Append("</tbody></table><p class=\"muted\">This is the chain of the instance named above, not a cluster-wide claim.</p></section>");
    }

    private static void Sessions(StringBuilder output, SessionView sessions)
    {
        output.Append("<section><h2>Sessions</h2>");

        if (Note(output, sessions.State, sessions.Note))
        {
            output.Append("</section>");
            return;
        }

        output.Append("<p>")
            .Append(sessions.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" active sessions observed by this instance.</p>")
            .Append("<table><thead><tr><th>Subject</th><th>Session</th><th>Started</th><th>Last used</th><th>Expires</th><th>Device shape</th></tr></thead><tbody>");

        foreach (var session in sessions.Sessions)
        {
            output.Append("<tr><td>").Append(H(session.Subject))
                .Append("</td><td>").Append(H(session.Session)).Append("</td><td>");
            Moment(output, session.StartedAt);
            output.Append("</td><td>");
            Moment(output, session.LastUsedAt);
            output.Append("</td><td>");
            Moment(output, session.ExpiresAt);
            output.Append("</td><td>").Append(H(session.ClientFingerprintShape)).Append("</td></tr>");
        }

        output.Append("</tbody></table><p class=\"muted\">Tokens and raw device fingerprints are never available here. This is not a cluster-wide inventory.</p></section>");
    }

    private static void RateLimits(StringBuilder output, RateLimitView rateLimits)
    {
        output.Append("<section><h2>Rate limits</h2>");

        if (Note(output, rateLimits.State, rateLimits.Note))
        {
            output.Append("</section>");
            return;
        }

        output.Append("<table><thead><tr><th>Key fingerprint</th><th>Count</th><th>Limit</th><th>Decision</th><th>Observed</th></tr></thead><tbody>");

        foreach (var counter in rateLimits.Counters)
        {
            output.Append("<tr><td><code>").Append(H(counter.KeyFingerprint))
                .Append("</code></td><td>").Append(counter.CurrentCount.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(counter.Limit?.ToString(CultureInfo.InvariantCulture) ?? "not recorded")
                .Append("</td><td class=\"").Append(counter.IsRejected ? "fail\">rejected" : "pass\">allowed")
                .Append("</td><td>");
            Moment(output, counter.ObservedAt);
            output.Append("</td></tr>");
        }

        output.Append("</tbody></table><p class=\"muted\">Store keys can contain subjects or addresses; only a one-way 12-character fingerprint is shown.</p></section>");
    }

    private static void Health(StringBuilder output, HealthView health)
    {
        output.Append("<section><h2>Health</h2>");

        if (Note(output, health.State, health.Note))
        {
            output.Append("</section>");
            return;
        }

        output.Append("<p>Overall: <span class=\"")
            .Append(health.Overall == "Healthy" ? "pass" : "warning")
            .Append("\">").Append(H(health.Overall ?? string.Empty)).Append("</span></p>")
            .Append("<table><thead><tr><th>Check</th><th>Status</th><th>Duration</th><th>Tags</th></tr></thead><tbody>");

        foreach (var entry in health.Entries)
        {
            output.Append("<tr><td>").Append(H(entry.Name)).Append("</td><td>")
                .Append(H(entry.Status)).Append("</td><td>")
                .Append(H(entry.DurationMs.ToString("0.###", CultureInfo.InvariantCulture)))
                .Append(" ms</td><td>").Append(H(string.Join(", ", entry.Tags))).Append("</td></tr>");
        }

        output.Append("</tbody></table></section>");
    }

    /// <summary>
    /// Writes the section's explanation when it has no data, and says whether
    /// it did.
    /// </summary>
    /// <remarks>
    /// The three empty states are not one state. A reader has to be able to
    /// tell "nothing of this kind is registered" from "it is registered and
    /// stopped answering", because the second is an outage and the first is a
    /// decision — so a failure is styled as a failure and never as a note.
    /// </remarks>
    private static bool Note(StringBuilder output, OperatorSectionState state, string? note)
    {
        if (state is OperatorSectionState.Present)
        {
            return false;
        }

        output.Append("<p class=\"")
            .Append(state is OperatorSectionState.Faulted ? "fail" : "not-applicable")
            .Append("\">").Append(H(note ?? string.Empty)).Append("</p>");

        return true;
    }

    /// <summary>
    /// A moment, marked up so the reader sees it in their own zone.
    /// </summary>
    /// <remarks>
    /// <para>The machine-readable value stays UTC in the attribute, which is
    /// what a collector reads and what a signature would cover. The visible
    /// text starts as the same UTC string and is rewritten to local time by
    /// dashboard.js.</para>
    ///
    /// <para>It renders correctly with scripting off — as UTC, marked
    /// <c>Z</c>-suffixed, rather than as a blank. An operator page that needed
    /// JavaScript to show a time would show none at all in the console where
    /// somebody has scripts disabled.</para>
    /// </remarks>
    private static void Moment(StringBuilder output, DateTimeOffset moment)
    {
        var iso = moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        output.Append("<time data-utc datetime=\"").Append(H(iso)).Append("\">")
            .Append(H(iso)).Append("</time>");
    }

    private static string H(string value) => HtmlEncoder.Default.Encode(value);
}
