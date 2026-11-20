using System.Text.Json;
using FluentAssertions;
using Noelia.Abstractions.Operator;
using Noelia.Cli;

namespace Noelia.Infrastructure.Tests.Architecture;

/// <summary>
/// What <c>noelia analyze --url</c> makes of a report, per schema version.
/// </summary>
/// <remarks>
/// The CLI does not reference Noelia.Abstractions, so nothing but the first
/// test ties its idea of the newest known schema to the one the dashboard
/// actually publishes. Without it a schema bump goes through unnoticed and the
/// tool tells its own release that it is out of date (issue 53).
/// </remarks>
[Trait("Category", "Unit")]
public class CliLiveScanSchemaTests
{
    private const string NotEnforced = "noelia.egress.not-enforced";
    private const string Newer = "newer than this tool";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static AnalysisResult Interpret(OperatorReport report) =>
        LiveScan.Interpret(JsonSerializer.SerializeToElement(report, Web), "http://x/report.json");

    private static OperatorReport WithEgress(
        int schema, bool legacyEnforced, HttpEgressPolicyView? egress = null) =>
        new()
        {
            SchemaVersion = schema,
            Sovereignty = new SovereigntyView
            {
                State = OperatorSectionState.Present,
                EgressIsEnforced = legacyEnforced,
                HttpEgress = egress ?? new HttpEgressPolicyView()
            }
        };

    private static HttpEgressPolicyView Guard(bool enforcing) => new()
    {
        State = OperatorSectionState.Present,
        IsEnforcing = enforcing,
        Scope = "factory-clients",
        Evidence = "configuration",
        Redirects = "blocked",
        AllowedTargets = ["secret-host-one.example", "secret-host-two.example"]
    };

    [Fact]
    public void The_known_schema_is_the_one_the_report_publishes() =>
        LiveScan.KnownSchemaVersion.Should().Be(new OperatorReport().SchemaVersion);

    [Fact]
    public void A_current_report_is_not_called_newer_than_the_tool()
    {
        var result = Interpret(new OperatorReport());

        result.Observations.Should().NotContain(o => o.Contains(Newer));
    }

    [Fact]
    public void A_report_one_schema_ahead_is_called_newer_than_the_tool()
    {
        var result = Interpret(new OperatorReport { SchemaVersion = LiveScan.KnownSchemaVersion + 1 });

        result.Observations.Should().Contain(o => o.Contains(Newer));
    }

    [Fact]
    public void A_configured_policy_without_an_enforcing_guard_is_a_finding_in_schema_3()
    {
        var result = Interpret(WithEgress(3, legacyEnforced: true, Guard(enforcing: false)));

        var finding = result.Findings.Should().ContainSingle(f => f.Id == NotEnforced).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.What.Should().Contain("factory-clients")
            .And.Contain("configuration")
            .And.Contain("blocked")
            .And.Contain("2");
        (finding.What + finding.Do).Should().NotContain("secret-host");
    }

    [Fact]
    public void An_enforcing_guard_is_no_finding_whatever_the_legacy_flag_says()
    {
        var result = Interpret(WithEgress(3, legacyEnforced: false, Guard(enforcing: true)));

        result.Findings.Should().NotContain(f => f.Id == NotEnforced);
    }

    [Fact]
    public void An_unreported_guard_is_no_finding_in_schema_3()
    {
        var result = Interpret(WithEgress(3, legacyEnforced: false));

        result.Findings.Should().NotContain(f => f.Id == NotEnforced);
    }

    [Fact]
    public void Schema_2_still_reads_the_legacy_flag()
    {
        var result = Interpret(WithEgress(2, legacyEnforced: false));

        result.Findings.Should().Contain(f => f.Id == NotEnforced);
    }
}
