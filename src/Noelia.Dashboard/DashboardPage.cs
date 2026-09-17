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
/// <summary>
/// One page of the operator view.
/// </summary>
/// <remarks>
/// <para>Until 6.4.0 every section was on one page. That works while there are
/// four of them and stops working at ten: the reader who came to answer one
/// question — where can this service reach, is the chain intact — scrolls past
/// nine answers to somebody else's question first, and a page that has to be
/// scrolled past is a page that gets skimmed.</para>
///
/// <para>Each section therefore has its own address. A real URL, so it can be
/// linked in a ticket and opened by the person who has to act on it, and
/// server-rendered, so it works with scripting off and in a console where a tab
/// widget would not.</para>
/// </remarks>
internal enum DashboardSection
{
    /// <summary>The state of every section, one line each.</summary>
    Overview,

    Composition,
    Configuration,
    Security,
    Sovereignty,

    /// <summary>Model endpoints and what follows from having them.</summary>
    ArtificialIntelligence,

    /// <summary>Which observations serve as evidence for which obligation.</summary>
    Obligations,

    Audit,
    Sessions,
    RateLimits,
    Health
}

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
    /// <summary>The address of each section, relative to the dashboard root.</summary>
    /// <remarks>
    /// Written once and read in both directions — the router matches a request
    /// against it, the navigation builds links from it — so a section cannot
    /// acquire a link that routes nowhere.
    /// </remarks>
    private static readonly (DashboardSection Section, string Slug, string Title)[] Sections =
    [
        (DashboardSection.Overview, "", "Overview"),
        (DashboardSection.Composition, "composition", "Composition"),
        (DashboardSection.Configuration, "configuration", "Configuration"),
        (DashboardSection.Security, "security", "Security checks"),
        (DashboardSection.Sovereignty, "sovereignty", "Sovereignty"),
        (DashboardSection.ArtificialIntelligence, "ai", "AI"),
        (DashboardSection.Obligations, "obligations", "Obligations"),
        (DashboardSection.Audit, "audit", "Audit trail"),
        (DashboardSection.Sessions, "sessions", "Sessions"),
        (DashboardSection.RateLimits, "rate-limits", "Rate limits"),
        (DashboardSection.Health, "health", "Health")
    ];

    /// <summary>
    /// Which section a path under the dashboard root asks for.
    /// </summary>
    /// <param name="remaining">The path after the dashboard root, such as <c>/ai</c>.</param>
    /// <param name="section">The section, when the path names one.</param>
    /// <returns><c>false</c> for a path that names no section, so the caller can 404.</returns>
    internal static bool TryParseSection(string remaining, out DashboardSection section)
    {
        var slug = remaining.Trim('/');

        foreach (var (candidate, candidateSlug, _) in Sections)
        {
            if (slug.Equals(candidateSlug, StringComparison.OrdinalIgnoreCase))
            {
                section = candidate;
                return true;
            }
        }

        section = DashboardSection.Overview;
        return false;
    }

    internal static string Render(
        OperatorReport report,
        NoeliaDashboardOptions options,
        DashboardSection section = DashboardSection.Overview)
    {
        var output = new StringBuilder(16_384);
        var title = Sections.First(entry => entry.Section == section).Title;

        output.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>Noelia · ").Append(H(report.Service)).Append(" · ").Append(H(title))
            .Append("</title>")
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

        Navigation(output, options, report, section);

        switch (section)
        {
            case DashboardSection.Overview: Overview(output, options, report); break;
            case DashboardSection.Composition: Composition(output, report.Composition); break;
            case DashboardSection.Configuration: Configuration(output, report.Configuration); break;
            case DashboardSection.Security: Security(output, report.SecurityChecks); break;
            case DashboardSection.Sovereignty: Sovereignty(output, report.Sovereignty); break;
            case DashboardSection.ArtificialIntelligence:
                ArtificialIntelligence(output, report); break;
            case DashboardSection.Obligations: Obligations(output, report.SecurityChecks); break;
            case DashboardSection.Audit: Audit(output, report.Audit); break;
            case DashboardSection.Sessions: Sessions(output, report.Sessions); break;
            case DashboardSection.RateLimits: RateLimits(output, report.RateLimits); break;
            case DashboardSection.Health: Health(output, report.Health); break;
        }

        output.Append("<script src=\"").Append(H(options.Path))
            .Append("/assets/dashboard.js\"></script></main></body></html>");

        return output.ToString();
    }

    /// <summary>
    /// The links between sections, with the one being read marked.
    /// </summary>
    /// <remarks>
    /// <c>aria-current</c> rather than a class alone, because the mark has to
    /// reach a screen reader as "this is where you are" and not as a colour.
    /// </remarks>
    private static void Navigation(
        StringBuilder output,
        NoeliaDashboardOptions options,
        OperatorReport report,
        DashboardSection current)
    {
        output.Append("<nav class=\"sections\" aria-label=\"Sections\"><ul>");

        foreach (var (section, slug, title) in Sections)
        {
            var attention = NeedsAttention(report, section);

            output.Append("<li><a href=\"").Append(H(options.Path));

            if (slug.Length > 0)
            {
                output.Append('/').Append(slug);
            }

            output.Append('"');

            if (section == current)
            {
                output.Append(" aria-current=\"page\"");
            }

            output.Append('>').Append(H(title));

            if (attention)
            {
                // A dot, and a word for anyone not seeing it. The point of the
                // navigation is that a reader on one section still learns that
                // another one has something to say.
                output.Append("<span class=\"dot\" role=\"img\" aria-label=\"needs attention\">•</span>");
            }

            output.Append("</a></li>");
        }

        output.Append("</ul></nav>");
    }

    /// <summary>
    /// Whether a section holds something the reader would want to know about.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. A mark that appears for ordinary states is one a
    /// reader learns to ignore within a week, and then it is worse than no mark
    /// at all — so this answers only for a failing check, an unenforced egress
    /// boundary, a broken chain or an unhealthy probe.
    /// </remarks>
    private static bool NeedsAttention(OperatorReport report, DashboardSection section) =>
        section switch
        {
            DashboardSection.Security =>
                report.SecurityChecks.Results.Any(r => r.Status is "Fail" or "Warning"),

            DashboardSection.Sovereignty =>
                report.Sovereignty.State is OperatorSectionState.Present
                && (!report.Sovereignty.EgressIsEnforced
                    || report.Sovereignty.Dependencies.Any(d => d.Jurisdiction == "ThirdCountryProvider")),

            DashboardSection.ArtificialIntelligence =>
                report.Sovereignty.Dependencies.Any(d =>
                    d.Kind == "ArtificialIntelligence" && d.Jurisdiction != "SelfHosted"),

            DashboardSection.Audit =>
                report.Audit.State is OperatorSectionState.Absent or OperatorSectionState.Faulted
                || (report.Audit.State is OperatorSectionState.Present
                    && !report.Audit.IsChainValidAtWriteTime),

            DashboardSection.Health =>
                report.Health.State is OperatorSectionState.Present
                && report.Health.Overall is not null and not "Healthy",

            DashboardSection.Composition =>
                report.Composition.Contracts.Any(c => c.Requirements.Any(r => !r.IsPresent)),

            _ => false
        };

    /// <summary>
    /// One line per section, so the reader chooses where to go from what is
    /// there rather than from the name.
    /// </summary>
    private static void Overview(
        StringBuilder output,
        NoeliaDashboardOptions options,
        OperatorReport report)
    {
        output.Append("<section><h2>Overview</h2>")
            .Append("<p class=\"muted\">Generated ");
        Moment(output, report.GeneratedAt);
        output.Append(". Each section is a page of its own; the machine-readable form of all of them is ")
            .Append("<a href=\"").Append(H(options.Path)).Append("/report.json\"><code>report.json</code></a>.</p>")
            .Append("<table><thead><tr><th>Section</th><th>State</th><th>What is there</th></tr></thead><tbody>");

        foreach (var (section, slug, title) in Sections)
        {
            if (section == DashboardSection.Overview)
            {
                continue;
            }

            var (state, detail) = Summarise(report, section);

            output.Append("<tr><td><a href=\"").Append(H(options.Path));

            if (slug.Length > 0)
            {
                output.Append('/').Append(slug);
            }

            output.Append("\">").Append(H(title)).Append("</a></td><td class=\"")
                .Append(NeedsAttention(report, section) ? "warning" : "pass")
                .Append("\">").Append(H(state)).Append("</td><td>")
                .Append(H(detail)).Append("</td></tr>");
        }

        output.Append("</tbody></table></section>");
    }

    private static (string State, string Detail) Summarise(
        OperatorReport report,
        DashboardSection section)
    {
        switch (section)
        {
            case DashboardSection.Composition:
                var missing = report.Composition.Contracts
                    .SelectMany(c => c.Requirements)
                    .Count(r => !r.IsPresent);
                var running = report.Composition.Modules.Count(m => m.IsRunning);

                return (missing == 0 ? "complete" : "incomplete",
                    $"{running} module(s) running, {missing} unmet requirement(s).");

            case DashboardSection.Configuration:
                return ("read",
                    $"{report.Configuration.Shapes.Count} key(s), shapes only.");

            case DashboardSection.Security:
                var results = report.SecurityChecks.Results;
                var failed = results.Count(r => r.Status == "Fail");
                var warned = results.Count(r => r.Status == "Warning");

                return (failed > 0 ? "failing" : warned > 0 ? "warnings" : "passing",
                    $"{results.Count} check(s) ran: {failed} fail, {warned} warning.");

            case DashboardSection.Sovereignty:
                if (report.Sovereignty.State is not OperatorSectionState.Present)
                {
                    return (report.Sovereignty.State.ToString().ToLowerInvariant(),
                        report.Sovereignty.Note ?? string.Empty);
                }

                var abroad = report.Sovereignty.Dependencies
                    .Count(d => d.Jurisdiction == "ThirdCountryProvider");

                return (report.Sovereignty.EgressIsEnforced ? "enforced" : "not enforced",
                    $"{report.Sovereignty.Dependencies.Count} destination(s), "
                    + $"{abroad} under third-country access law.");

            case DashboardSection.ArtificialIntelligence:
                var models = report.Sovereignty.Dependencies
                    .Count(d => d.Kind == "ArtificialIntelligence");

                return (models == 0 ? "none recognised" : "in use",
                    models == 0
                        ? "No configured destination is a recognised model endpoint."
                        : $"{models} recognised model endpoint(s).");

            case DashboardSection.Obligations:
                var cited = report.SecurityChecks.Results
                    .SelectMany(r => r.References)
                    .Select(r => r.Citation)
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                return ("evidence only",
                    $"{cited} obligation(s) have evidence here. None of them is thereby met.");

            case DashboardSection.Audit:
                if (report.Audit.State is not OperatorSectionState.Present)
                {
                    return (report.Audit.State.ToString().ToLowerInvariant(),
                        report.Audit.Note ?? string.Empty);
                }

                return (report.Audit.IsChainValidAtWriteTime ? "intact" : "broken",
                    $"{report.Audit.Length.ToString(CultureInfo.InvariantCulture)} entries on "
                    + "this instance.");

            case DashboardSection.Sessions:
                return report.Sessions.State is OperatorSectionState.Present
                    ? ("readable",
                        $"{report.Sessions.Count.ToString(CultureInfo.InvariantCulture)} active "
                        + "session(s) seen by this instance.")
                    : (report.Sessions.State.ToString().ToLowerInvariant(),
                        report.Sessions.Note ?? string.Empty);

            case DashboardSection.RateLimits:
                return report.RateLimits.State is OperatorSectionState.Present
                    ? ("readable", $"{report.RateLimits.Counters.Count} counter(s) observed.")
                    : (report.RateLimits.State.ToString().ToLowerInvariant(),
                        report.RateLimits.Note ?? string.Empty);

            case DashboardSection.Health:
                return report.Health.State is OperatorSectionState.Present
                    ? (report.Health.Overall ?? "unknown",
                        $"{report.Health.Entries.Count} probe(s).")
                    : (report.Health.State.ToString().ToLowerInvariant(),
                        report.Health.Note ?? string.Empty);

            default:
                return ("—", string.Empty);
        }
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

    /// <summary>
    /// The model endpoints this service is configured to reach, and what having
    /// them obliges its operator to do.
    /// </summary>
    /// <remarks>
    /// <para>Its own page because the reader is its own person. An AI Act
    /// assessment is not done by whoever watches the health probes, and the
    /// first thing that assessment needs is the list nobody keeps: which model
    /// endpoints this service actually talks to.</para>
    ///
    /// <para>The page says what it cannot see, twice — once in the empty state
    /// and once under the table. An inventory read as exhaustive when it is not
    /// would send somebody into an Article 26 assessment with a hole in it, and
    /// the hole would be invisible precisely because this page looked
    /// complete.</para>
    /// </remarks>
    private static void ArtificialIntelligence(StringBuilder output, OperatorReport report)
    {
        output.Append("<section><h2>Artificial intelligence</h2>");

        if (report.Sovereignty.State is not OperatorSectionState.Present)
        {
            output.Append("<p class=\"not-applicable\">No destination register is available, so ")
                .Append("model endpoints cannot be listed. ")
                .Append(H(report.Sovereignty.Note ?? string.Empty))
                .Append("</p></section>");
            return;
        }

        var models = report.Sovereignty.Dependencies
            .Where(dependency => dependency.Kind == "ArtificialIntelligence")
            .ToArray();

        if (models.Length == 0)
        {
            output.Append("<p class=\"pass\">No configured destination is a recognised model or ")
                .Append("inference endpoint.</p>")
                .Append("<p class=\"muted\">Recognition is by host name. A model served from a ")
                .Append("name of your own choosing, or reached through a gateway, would not ")
                .Append("appear here — so this is a floor, not a ceiling.</p></section>");
        }
        else
        {
            output.Append("<p>")
                .Append(models.Length.ToString(CultureInfo.InvariantCulture))
                .Append(" recognised model endpoint(s).</p>")
                .Append("<table><thead><tr><th>Dependency</th><th>Host</th><th>Jurisdiction</th>")
                .Append("<th>Reason</th></tr></thead><tbody>");

            foreach (var model in models)
            {
                output.Append("<tr><td>").Append(H(model.Name)).Append("</td><td>")
                    .Append(H(model.Host ?? "not configured")).Append("</td><td class=\"")
                    .Append(model.Jurisdiction == "SelfHosted" ? "pass" : "warning")
                    .Append("\">").Append(H(model.Jurisdiction)).Append("</td><td>")
                    .Append(H(model.Note)).Append("</td></tr>");
            }

            output.Append("</tbody></table>")
                .Append("<p class=\"muted\">Every prompt sent to an endpoint outside the Union ")
                .Append("is a cross-border transfer of whatever that prompt contains. Recognition ")
                .Append("is by host name, so a model served under a name of your own choosing, ")
                .Append("or reached through a gateway, would not appear here — this is a ")
                .Append("floor, not a ceiling.</p>");
        }

        var checks = report.SecurityChecks.Results
            .Where(result => result.Id.StartsWith("noelia.ai.", StringComparison.Ordinal))
            .ToArray();

        if (checks.Length > 0)
        {
            output.Append("<h3>Checks</h3>")
                .Append("<table><thead><tr><th>Check</th><th>Status</th><th>Finding</th>")
                .Append("<th>Remediation</th></tr></thead><tbody>");

            foreach (var check in checks)
            {
                output.Append("<tr><td><code>").Append(H(check.Id)).Append("</code></td><td class=\"")
                    .Append(H(check.Status.ToLowerInvariant())).Append("\">").Append(H(check.Status))
                    .Append("</td><td>").Append(H(check.Summary)).Append("</td><td>")
                    .Append(H(check.Remediation)).Append("</td></tr>");
            }

            output.Append("</tbody></table>");
        }

        output.Append("<h3>What a person still decides</h3>")
            .Append("<ul>")
            .Append("<li>Whether any of this is a high-risk AI system under Annex III. That is a ")
            .Append("classification of the <em>use</em>, and nothing in a configuration file ")
            .Append("carries it.</li>")
            .Append("<li>Whether prompts contain personal data. This page sees destinations, ")
            .Append("never payloads.</li>")
            .Append("<li>Whether a processing agreement and a transfer safeguard are on file for ")
            .Append("each provider above.</li>")
            .Append("<li>Who holds human oversight, and whether the provider's instructions for ")
            .Append("use are being followed.</li>")
            .Append("</ul></section>");
    }

    /// <summary>
    /// Which observation is evidence for which obligation — and what it does
    /// not settle.
    /// </summary>
    /// <remarks>
    /// <para>The mapping is the product of this page, and the disclaimer is
    /// half of the mapping. Every row pairs a citation with the thing a person
    /// still has to decide, in the same table, because a two-column table
    /// cannot be screenshotted into a claim the third column contradicts.</para>
    ///
    /// <para>No status is shown per obligation. There is no honest way to put
    /// a tick against "Art. 28(3)" from inside a process — the article is about
    /// a contract — and a tick is what a reader would remember.</para>
    /// </remarks>
    private static void Obligations(StringBuilder output, SecurityCheckView checks)
    {
        output.Append("<section><h2>Obligations</h2>")
            .Append("<p class=\"muted\">This is a map from what was observed to the articles that ")
            .Append("ask about it. Nothing here states that an obligation is met: a program can ")
            .Append("show that a record exists, is automatic and has not been edited. It cannot ")
            .Append("show that a contract is adequate, that a risk assessment is any good, or ")
            .Append("that a use case was classified correctly.</p>");

        var mapped = checks.Results
            .SelectMany(result => result.References.Select(reference => (result, reference)))
            .GroupBy(pair => pair.reference.Regime, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        if (mapped.Length == 0)
        {
            output.Append("<p class=\"not-applicable\">No check in this composition produced ")
                .Append("evidence for a cited obligation.</p></section>");
            return;
        }

        foreach (var regime in mapped)
        {
            output.Append("<h3>").Append(H(RegimeTitle(regime.Key))).Append("</h3>")
                .Append("<table><thead><tr><th>Article</th><th>What it asks for</th>")
                .Append("<th>Evidence here</th><th>What you still decide</th></tr></thead><tbody>");

            var articles = regime
                .GroupBy(pair => pair.reference.Citation, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal);

            foreach (var article in articles)
            {
                var first = article.First().reference;
                var evidence = string.Join(", ", article
                    .Select(pair => pair.result.Id)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal));

                output.Append("<tr><td>").Append(H(first.Citation)).Append("</td><td>")
                    .Append(H(first.Obligation)).Append("</td><td><code>")
                    .Append(H(evidence)).Append("</code></td><td>")
                    .Append(H(first.Reader)).Append("</td></tr>");
            }

            output.Append("</tbody></table>");
        }

        output.Append("</section>");
    }

    private static string RegimeTitle(string regime) => regime switch
    {
        "Gdpr" => "GDPR — Regulation (EU) 2016/679",
        "AiAct" => "EU AI Act — Regulation (EU) 2024/1689",
        "Nis2" => "NIS2 — Directive (EU) 2022/2555",
        "Dora" => "DORA — Regulation (EU) 2022/2554",
        _ => regime
    };

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
