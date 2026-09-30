using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Infrastructure.Sovereignty;
using Noelia.Abstractions.Operator;
using Noelia.Abstractions.Sovereignty;

namespace Noelia.Infrastructure.Tests.Dashboard;

[Trait("Category", "Unit")]
public sealed class EgressReportContractTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Guard_is_reported_without_dependency_provider_and_empty_policy_remains_unrestricted(bool enforcing)
    {
        await using var app = await DashboardHost.Start(options => options.At("/noelia").VisibleTo(_ => true),
            additionalServices: services => services.AddNoeliaEgressPolicy(policy =>
            {
                if (enforcing) policy.Allow("allowed.internal");
            }));
        var report = JsonSerializer.Deserialize<OperatorReport>(await app.Client.GetStringAsync("/noelia/report.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        report.Sovereignty.State.Should().Be(OperatorSectionState.Absent);
        report.Sovereignty.HttpEgress.State.Should().Be(OperatorSectionState.Present);
        report.Sovereignty.HttpEgress.IsEnforcing.Should().Be(enforcing);
        report.Sovereignty.HttpEgress.Redirects.Should().Be(enforcing ? "DisabledForSupportedTransports" : "TransportDefault");
        var html = await app.Client.GetStringAsync("/noelia/sovereignty");
        html.Should().Contain("Factory HTTP policy").And.Contain("Observed outbound calls")
            .And.Contain("not collected").And.Contain("HttpClientFactory");
    }

    [Fact]
    public async Task Broken_guard_report_does_not_erase_dependency_declarations_or_leak_its_exception()
    {
        await using var app = await DashboardHost.Start(options => options.At("/noelia").VisibleTo(_ => true),
            additionalServices: services =>
            {
                services.AddSingleton<IHttpEgressPolicyReport, BrokenGuardReport>();
                services.AddNoeliaSovereigntyReport(new DeclaredDependency("Database", "https://db.internal"));
            });
        var body = await app.Client.GetStringAsync("/noelia/report.json");
        body.Should().NotContain("synthetic-exception-secret");
        var report = JsonSerializer.Deserialize<OperatorReport>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        report.Sovereignty.State.Should().Be(OperatorSectionState.Present);
        report.Sovereignty.HttpEgress.State.Should().Be(OperatorSectionState.Faulted);
        report.Sovereignty.HttpEgress.IsEnforcing.Should().BeNull();
        report.Sovereignty.Dependencies.Should().ContainSingle();
    }

    [Fact]
    public void Legacy_wire_report_does_not_acquire_new_guard_or_observation_evidence()
    {
        var report = JsonSerializer.Deserialize<OperatorReport>(
            """{"schemaVersion":2,"sovereignty":{"state":"Present","egressIsEnforced":true}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        report.Sovereignty.HttpEgress.State.Should().Be(OperatorSectionState.Unavailable);
        report.Sovereignty.HttpEgress.IsEnforcing.Should().BeNull();
        report.Sovereignty.ObservedCalls.Count.Should().BeNull();
    }

    private sealed class BrokenGuardReport : IHttpEgressPolicyReport
    {
        public HttpEgressPolicyAssessment Assess() => throw new InvalidOperationException("synthetic-exception-secret");
    }

    [Fact]
    public async Task Dependency_declarations_policy_and_unobserved_traffic_are_separate_on_the_wire()
    {
        await using var app = await DashboardHost.Start(options => options.At("/noelia").VisibleTo(_ => true),
            additionalServices: services =>
            {
                services.AddNoeliaEgressPolicy(policy => policy.Allow("allowed-only.internal"));
                services.AddNoeliaSovereigntyReport(new DeclaredDependency("Database",
                    "https://synthetic-user:synthetic-secret@declared-only.internal/path?token=synthetic-canary"));
            });
        var body = await app.Client.GetStringAsync("/noelia/report.json");
        body.Should().NotContain("synthetic-secret").And.NotContain("synthetic-canary");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(3);
        var sovereignty = document.RootElement.GetProperty("sovereignty");
        sovereignty.GetProperty("dependencies").EnumerateArray().Should().ContainSingle()
            .Subject.GetProperty("host").GetString().Should().Be("declared-only.internal");
        var policy = sovereignty.GetProperty("httpEgress");
        policy.GetProperty("state").GetString().Should().Be("Present");
        policy.GetProperty("scope").GetString().Should().Be("HttpClientFactory");
        policy.GetProperty("allowedTargets").EnumerateArray().Single().GetString().Should().Be("allowed-only.internal");
        policy.GetProperty("redirects").GetString().Should().Be("DisabledForSupportedTransports");
        var observations = sovereignty.GetProperty("observedCalls");
        observations.GetProperty("state").GetString().Should().Be("Unavailable");
        observations.GetProperty("count").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_policy_object_alone_does_not_claim_a_registered_factory_guard()
    {
        await using var app = await DashboardHost.Start(options => options.At("/noelia").VisibleTo(_ => true),
            additionalServices: services =>
            {
                services.AddSingleton(new EgressPolicyBuilder().Allow("allowed.internal").Build());
                services.AddNoeliaSovereigntyReport();
            });
        using var document = JsonDocument.Parse(await app.Client.GetStringAsync("/noelia/report.json"));
        document.RootElement.GetProperty("sovereignty").GetProperty("httpEgress")
            .GetProperty("state").GetString().Should().Be("Absent");
    }
}
