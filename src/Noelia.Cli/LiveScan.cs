using System.Net.Http.Json;
using System.Text.Json;

namespace Noelia.Cli;

/// <summary>
/// Reads the report a running service publishes.
/// </summary>
/// <remarks>
/// The authoritative reading. A static scan sees literals in files; this sees
/// the composition that actually happened, the checks that actually ran and the
/// destinations the running configuration actually resolves to. Whenever a
/// service is up, this is the answer and the static scan is an approximation
/// of it.
/// </remarks>
internal static class LiveScan
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<AnalysisResult> RunAsync(
        string url,
        string? operatorSecret,
        CancellationToken cancellationToken)
    {
        var observations = new List<string>();

        var reportUrl = url.TrimEnd('/') + "/report.json";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        if (operatorSecret is { Length: > 0 })
        {
            client.DefaultRequestHeaders.Add("X-Noelia-Operator", operatorSecret);
        }

        JsonElement report;

        try
        {
            using var response = await client.GetAsync(reportUrl, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 is the dashboard's answer to anyone it does not recognise:
                // it refuses to admit it exists rather than returning 401, so
                // "not found" and "not allowed" arrive identically and the
                // message has to name both.
                return new AnalysisResult(reportUrl, "live",
                [
                    new Finding(
                        "noelia.report.unreachable",
                        FindingSeverity.Problem,
                        reportUrl,
                        $"The report answered {(int)response.StatusCode}. A dashboard hidden "
                        + "from a caller answers 404 rather than 401, so this is either the "
                        + "wrong path or a visibility rule that does not admit this caller.",
                        "Check the dashboard path, and pass --operator-secret if the service "
                        + "gates it on a header.")
                ], observations);
            }

            report = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return new AnalysisResult(reportUrl, "live",
            [
                new Finding(
                    "noelia.report.unreachable",
                    FindingSeverity.Problem,
                    reportUrl,
                    "The report could not be reached: " + exception.Message,
                    "Check that the service is running and that the URL is its dashboard root.")
            ], observations);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AnalysisResult(reportUrl, "live",
            [
                new Finding(
                    "noelia.report.unreachable",
                    FindingSeverity.Problem,
                    reportUrl,
                    "The report did not answer within twenty seconds.",
                    "Check that the service is running and reachable from here.")
            ], observations);
        }

        return Interpret(report, reportUrl);
    }

    /// <summary>
    /// The newest report schema this tool reads. The CLI does not reference
    /// Noelia.Abstractions, so a test ties this to
    /// <c>OperatorReport.SchemaVersion</c>; raise it together with the reader.
    /// </summary>
    internal const int KnownSchemaVersion = 3;

    /// <summary>
    /// Reads an already fetched report. Split from the HTTP part so the reading
    /// can be tested without a running dashboard.
    /// </summary>
    internal static AnalysisResult Interpret(JsonElement report, string reportUrl)
    {
        var findings = new List<Finding>();
        var observations = new List<string>();

        var schema = SchemaOf(report);

        Describe(report, schema, observations);
        Checks(report, findings);
        Sovereignty(report, schema, findings);
        Contracts(report, findings);
        Audit(report, findings);

        return new AnalysisResult(
            reportUrl,
            "live",
            [.. findings.OrderByDescending(finding => finding.Severity)
                        .ThenBy(finding => finding.Id, StringComparer.Ordinal)],
            observations);
    }

    private static int SchemaOf(JsonElement report) =>
        report.TryGetProperty("schemaVersion", out var version)
        && version.ValueKind == JsonValueKind.Number
        && version.TryGetInt32(out var schema)
            ? schema
            : 0;

    private static void Describe(JsonElement report, int schema, List<string> observations)
    {
        var service = Text(report, "service") ?? "unnamed";
        var environment = Text(report, "environment") ?? "unknown";

        observations.Add($"{service} in {environment}, report schema {schema}.");

        if (schema > KnownSchemaVersion)
        {
            observations.Add(
                "The report is newer than this tool. Fields it does not know about were not "
                + "read, so a finding may be missing rather than absent.");
        }
    }

    private static void Checks(JsonElement report, List<Finding> findings)
    {
        if (!report.TryGetProperty("securityChecks", out var section)
            || !section.TryGetProperty("results", out var results))
        {
            return;
        }

        foreach (var result in results.EnumerateArray())
        {
            var status = Text(result, "status");

            if (status is not ("Fail" or "Warning"))
            {
                continue;
            }

            findings.Add(new Finding(
                Text(result, "id") ?? "noelia.check",
                status == "Fail" ? FindingSeverity.Problem : FindingSeverity.Warning,
                "security check",
                Text(result, "summary") ?? string.Empty,
                Text(result, "remediation") ?? string.Empty));
        }
    }

    private static void Sovereignty(JsonElement report, int schema, List<Finding> findings)
    {
        if (!report.TryGetProperty("sovereignty", out var section)
            || Text(section, "state") != "Present")
        {
            return;
        }

        if (schema >= 3)
        {
            // From schema 3 egressIsEnforced is a legacy policy flag, explicitly
            // not evidence of an installed guard, so it is no longer read: a
            // configured policy with no guard would otherwise pass silently.
            // httpEgress carries the evidence. Absent, Faulted and Unavailable
            // (or an unreported isEnforcing) say nothing about enforcement and
            // therefore raise nothing here; noelia.egress.guard speaks for them.
            //
            // observedCalls is deliberately not read: no outbound collector is
            // wired in, so that section is always Unavailable and would only
            // read as "nothing was called".
            if (section.TryGetProperty("httpEgress", out var egress)
                && Text(egress, "state") == "Present"
                && egress.TryGetProperty("isEnforcing", out var enforcing)
                && enforcing.ValueKind == JsonValueKind.False)
            {
                var targets = egress.TryGetProperty("allowedTargets", out var list)
                    && list.ValueKind == JsonValueKind.Array
                        ? list.GetArrayLength()
                        : 0;

                // Counts and shapes only; the allowed hosts are never copied.
                findings.Add(new Finding(
                    "noelia.egress.not-enforced",
                    FindingSeverity.Warning,
                    "sovereignty",
                    "The HTTP guard is registered but its allow policy does not restrict "
                    + $"requests (scope: {Text(egress, "scope") ?? "not reported"}, "
                    + $"evidence: {Text(egress, "evidence") ?? "not reported"}, "
                    + $"redirects: {Text(egress, "redirects") ?? "not reported"}, "
                    + $"{targets} allowed target(s)). The destination register lists what "
                    + "was configured rather than what is reachable.",
                    "Declare the hosts this service may reach, so that what is absent from the "
                    + "register is unreachable rather than merely unmentioned."));
            }
        }
        else if (section.TryGetProperty("egressIsEnforced", out var enforced)
            && enforced.ValueKind == JsonValueKind.False)
        {
            findings.Add(new Finding(
                "noelia.egress.not-enforced",
                FindingSeverity.Warning,
                "sovereignty",
                "Outbound calls are not constrained, so the destination register lists what "
                + "was configured rather than what is reachable.",
                "Declare the hosts this service may reach, so that what is absent from the "
                + "register is unreachable rather than merely unmentioned."));
        }

        if (!section.TryGetProperty("dependencies", out var dependencies))
        {
            return;
        }

        foreach (var dependency in dependencies.EnumerateArray())
        {
            var jurisdiction = Text(dependency, "jurisdiction");
            var kind = Text(dependency, "kind");
            var name = Text(dependency, "name") ?? "a dependency";
            var host = Text(dependency, "host") ?? "no host";

            if (kind == "ArtificialIntelligence")
            {
                findings.Add(new Finding(
                    "noelia.ai.endpoint",
                    jurisdiction == "SelfHosted" ? FindingSeverity.Note : FindingSeverity.Warning,
                    $"{name} · {host}",
                    "A recognised model or inference endpoint. Whether this makes the service "
                    + "a high-risk AI system is a classification of the use, which no tool "
                    + "can make.",
                    "Record it in the AI inventory, with the processing agreement and, if it "
                    + "is outside the Union, the transfer safeguard that covers it."));

                continue;
            }

            if (jurisdiction == "ThirdCountryProvider")
            {
                findings.Add(new Finding(
                    "noelia.destination.third-country",
                    FindingSeverity.Warning,
                    $"{name} · {host}",
                    "The destination belongs to a provider subject to third-country access "
                    + "law.",
                    "Record the transfer safeguard that covers it, or move it to a provider "
                    + "that needs none."));
            }
        }
    }

    private static void Contracts(JsonElement report, List<Finding> findings)
    {
        if (!report.TryGetProperty("composition", out var composition)
            || !composition.TryGetProperty("contracts", out var contracts))
        {
            return;
        }

        foreach (var contract in contracts.EnumerateArray())
        {
            if (!contract.TryGetProperty("requirements", out var requirements))
            {
                continue;
            }

            foreach (var requirement in requirements.EnumerateArray())
            {
                if (!requirement.TryGetProperty("isPresent", out var present)
                    || present.ValueKind != JsonValueKind.False)
                {
                    continue;
                }

                var providers = requirement.TryGetProperty("providers", out var hints)
                    ? string.Join(" or ", hints.EnumerateArray().Select(hint => hint.GetString()))
                    : "a provider";

                findings.Add(new Finding(
                    "noelia.requirement.unmet",
                    FindingSeverity.Problem,
                    Text(contract, "module") ?? "a module",
                    $"{Text(requirement, "serviceType")} is required and not registered.",
                    providers));
            }
        }
    }

    private static void Audit(JsonElement report, List<Finding> findings)
    {
        if (!report.TryGetProperty("audit", out var section))
        {
            return;
        }

        if (Text(section, "state") == "Absent")
        {
            findings.Add(new Finding(
                "noelia.audit.absent",
                FindingSeverity.Warning,
                "audit",
                "No audit trail is registered, so nothing the service does is recorded "
                + "anywhere it could later be read.",
                "Call AddSovereignAuditTrail(), and UseRedisSovereignAudit() where more than "
                + "one replica shares the chain."));
            return;
        }

        if (section.TryGetProperty("isChainValidAtWriteTime", out var valid)
            && valid.ValueKind == JsonValueKind.False)
        {
            findings.Add(new Finding(
                "noelia.audit.chain-broken",
                FindingSeverity.Problem,
                "audit",
                "The instance reports its own chain as invalid at write time.",
                "Fetch /audit-chain.json for the first break, and check whether two replicas "
                + "are advancing a chain neither of them owns."));
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
