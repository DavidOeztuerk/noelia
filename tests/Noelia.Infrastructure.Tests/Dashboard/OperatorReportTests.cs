using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Operator;
using Noelia.Infrastructure.Audit;

namespace Noelia.Infrastructure.Tests.Dashboard;

/// <summary>
/// The operator view, read by something that is not a browser.
/// </summary>
/// <remarks>
/// A fleet view, a verifier or a nightly job needs the same statement the page
/// makes, as data. The risk this suite exists to pin is not that the JSON is
/// malformed — it is that the JSON and the page stop agreeing, quietly, because
/// each was produced along its own path.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class OperatorReportTests
{
    private const string ConfigurationCanary = "NOELIA-REPORT-CANARY-9d1e2f";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task The_report_is_json_and_deserialises_into_the_shipped_type()
    {
        await using var app = await Open();

        var response = await app.Client.GetAsync("/noelia/report.json");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var report = JsonSerializer.Deserialize<OperatorReport>(body, Json);

        report.Should().NotBeNull();
        report!.Service.Should().Be("dashboard-probe");
        report.SchemaVersion.Should().Be(2,
            "a reader that has to infer the format is one version away from misreading it");
        report.GeneratedAt.Should().NotBe(default);
    }

    /// <summary>
    /// The property the whole refactor exists for.
    /// </summary>
    /// <remarks>
    /// Both representations are drawn from one reading. If someone later adds a
    /// second query path for the JSON, the two will disagree the first time a
    /// provider answers differently between two calls — and this is the test
    /// that notices.
    /// </remarks>
    [Fact]
    public async Task What_the_page_shows_is_what_the_report_says()
    {
        await using var app = await Open();

        var html = await app.WholeDashboard();
        var report = await Report(app);

        report.Composition.Modules.Should().NotBeEmpty();

        foreach (var module in report.Composition.Modules)
        {
            html.Should().Contain(module.Name,
                "a module named in the report has to appear on the page");
        }

        // The empty sections carry the agreement just as much as the full ones:
        // whatever reason the report gives for having no data is the sentence
        // the page prints, because there is only one place either can come from.
        foreach (var note in new[]
                 {
                     report.SecurityChecks.Note, report.Sovereignty.Note, report.Audit.Note,
                     report.Sessions.Note, report.RateLimits.Note, report.Health.Note
                 }.Where(note => note is not null))
        {
            html.Should().Contain(note!,
                "the page and the report explain an empty section with the same words");
        }
    }

    [Fact]
    public async Task The_report_is_behind_the_same_door_as_the_page()
    {
        await using var app = await DashboardHost.Start(options => options
            .At("/noelia")
            .VisibleTo(context => context.Request.Headers["X-Noelia-Operator"] == "yes"));

        var page = await app.Client.GetAsync("/noelia");
        var report = await app.Client.GetAsync("/noelia/report.json");

        page.StatusCode.Should().Be(HttpStatusCode.NotFound);
        report.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a report path that were easier to reach than the page would be a way "
            + "around the access rule rather than a second view of it");
        (await report.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task No_configuration_value_reaches_the_report()
    {
        await using var app = await DashboardHost.Start(
            options => options.At("/noelia").VisibleTo(_ => true).InspectConfiguration("Probe"),
            configuration: new Dictionary<string, string?>
            {
                ["Probe:ConnectionString"] = ConfigurationCanary
            });

        var body = await app.Client.GetStringAsync("/noelia/report.json");

        body.Should().NotContain(ConfigurationCanary,
            "the machine-readable view is not a way around the rule that values never leave");
        body.Should().Contain("ConnectionString", "the key and its shape are the point");
        body.Should().Contain("\"shape\":\"set\"");
    }

    /// <summary>
    /// Four states, because collapsing them loses the finding.
    /// </summary>
    /// <remarks>
    /// A service with no rate-limit store and a service whose store stopped
    /// answering both produce an empty list. One is a composition decision and
    /// the other is an outage, and a fleet view that cannot tell them apart
    /// will report an outage as a configuration choice.
    /// </remarks>
    [Fact]
    public async Task A_provider_that_throws_is_faulted_and_not_absent()
    {
        await using var absent = await Open();
        await using var broken = await Open(services =>
            services.AddSingleton<IDistributedRateLimitStore, ThrowingRateLimitStore>());

        (await Report(absent)).RateLimits.State.Should().Be(OperatorSectionState.Absent);

        var faulted = (await Report(broken)).RateLimits;

        faulted.State.Should().Be(OperatorSectionState.Faulted);
        faulted.Note.Should().NotBeNullOrWhiteSpace("a failure has to say that it is one");
    }

    [Fact]
    public async Task A_faulted_section_reads_as_a_failure_on_the_page_too()
    {
        await using var app = await Open(services =>
            services.AddSingleton<IDistributedRateLimitStore, ThrowingRateLimitStore>());

        var html = await app.WholeDashboard();

        html.Should().Contain("class=\"fail\">Rate-limit counters could not be inspected.",
            "an outage styled as a note is an outage nobody reacts to");
    }

    [Fact]
    public async Task The_fleet_is_reported_when_it_is_configured_and_never_invented()
    {
        await using var unnamed = await Open();
        await using var named = await DashboardHost.Start(
            options => options.At("/noelia").VisibleTo(_ => true),
            configuration: new Dictionary<string, string?> { ["Dashboard:Fleet"] = "produktion" });

        (await Report(unnamed)).Fleet.Should().BeNull(
            "a guessed fleet name would group unrelated services and look authoritative doing it");
        (await Report(named)).Fleet.Should().Be("produktion");
    }

    [Fact]
    public async Task A_head_request_answers_without_a_body()
    {
        await using var app = await Open();

        using var request = new HttpRequestMessage(HttpMethod.Head, "/noelia/report.json");
        var response = await app.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_path_under_the_dashboard_is_still_a_404()
    {
        await using var app = await Open();

        (await app.Client.GetAsync("/noelia/report.yaml")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await app.Client.GetAsync("/noelia/report.json/more")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_running_module_appears_with_its_contract()
    {
        await using var app = await Open();

        var report = await Report(app);

        report.Composition.Modules.Should().Contain(module => module.Name == "Dashboard");
        report.Composition.Modules.Where(module => module.IsRunning)
            .Should().OnlyContain(module => module.Decision == "selected");

        var dashboard = report.Composition.Contracts
            .SingleOrDefault(contract => contract.Module == "Dashboard");

        dashboard.Should().NotBeNull();
        dashboard!.Provisions.Should().Contain(provision =>
            provision.ServiceType == "INoeliaDashboard" && provision.IsRegistered);
    }

    /// <summary>
    /// A moment leaves as UTC and says so, whatever the reader's zone.
    /// </summary>
    /// <remarks>
    /// The page localises times in the browser for whoever is reading, but the
    /// value in the markup and the value in the report stay UTC. A timestamp
    /// that changed with the reader would make two people quoting the same
    /// audit entry disagree about when it happened.
    /// </remarks>
    [Fact]
    public async Task Times_leave_as_utc_and_carry_a_machine_readable_value()
    {
        // A real trail, so the dashboard's own access gives the section
        // something to render — the page records who looked at it.
        await using var app = await Open(services => services.AddSovereignAuditTrail());

        var html = await app.WholeDashboard();
        var report = await Report(app);

        report.Audit.Latest.Should().NotBeEmpty();

        report.Audit.Latest.Should().OnlyContain(entry => entry.Timestamp.Offset == TimeSpan.Zero,
            "a collected moment must not depend on where it was read");

        // Each request writes its own audit entry, so the page and the report
        // hold different moments. What has to hold for both is the property,
        // not the value: every marked-up time is UTC.
        var marked = System.Text.RegularExpressions.Regex
            .Matches(html, "<time data-utc datetime=\"([^\"]+)\"")
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value))
            .ToArray();

        marked.Should().NotBeEmpty("the localiser finds moments by that marker");
        marked.Should().OnlyContain(value => DateTimeOffset.Parse(value, null).Offset == TimeSpan.Zero,
            "the markup carries the instant a collector reads, not the localised text");
    }

    private static Task<DashboardHost> Open(Action<IServiceCollection>? additionalServices = null) =>
        DashboardHost.Start(
            options => options.At("/noelia").VisibleTo(_ => true),
            additionalServices: additionalServices);

    private static async Task<OperatorReport> Report(DashboardHost app) =>
        JsonSerializer.Deserialize<OperatorReport>(
            await app.Client.GetStringAsync("/noelia/report.json"), Json)!;

    /// <summary>A store that is registered and cannot answer.</summary>
    private sealed class ThrowingRateLimitStore : IDistributedRateLimitStore
    {
        public Task<RateLimitInspection> InspectAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the counter store is unreachable");

        public Task<long> GetCountAsync(string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<long> IncrementAsync(
            string key, TimeSpan expiration, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<bool> ExpireAsync(
            string key, TimeSpan expiration, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<TimeSpan?> GetTimeToLiveAsync(
            string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<WindowCheckResult> SlidingWindowIncrementAsync(
            string key, int limit, TimeSpan window, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }
}
