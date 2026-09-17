using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Compliance;
using Noelia.Abstractions.Operator;
using Noelia.Abstractions.Security.Checks;
using Noelia.Abstractions.Sovereignty;
using Noelia.Dashboard;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Sovereignty;

namespace Noelia.Infrastructure.Tests.Dashboard;

/// <summary>
/// What the operator view says about model endpoints and the articles that ask
/// about them.
/// </summary>
/// <remarks>
/// Every assertion here is about an effect somebody reads — a row on a page, a
/// field in the report, a status code. None of them would still pass with the
/// body of what they test deleted, which is the standing rule in this suite
/// after three <c>…_ReturnsSelf</c> tests turned out to be guarding
/// commented-out methods.
/// </remarks>
[Trait("Category", "Unit")]
public class ComplianceViewTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A host composed the way a real service is.
    /// </summary>
    /// <remarks>
    /// <c>AddNoelia</c> rather than <c>AddNoeliaDashboard</c>, because the
    /// checks under test are Composition-category ones that the engine
    /// registers. A dashboard-only host has no engine and therefore none of
    /// them, which is correct and would make these tests assert nothing.
    /// </remarks>
    private static Task<DashboardHost> Open(params DeclaredDependency[] dependencies) =>
        DashboardHost.StartCustom((services, configuration, environment) =>
        {
            services.AddNoelia(configuration, environment, "dashboard-probe", noelia => noelia
                .UseDashboard(options => options.At("/noelia").VisibleTo(_ => true)));

            services.AddNoeliaSovereigntyReport(dependencies);
        });

    [Fact]
    public async Task A_model_endpoint_is_recognised_and_an_ordinary_one_is_not()
    {
        await using var app = await Open(
            new DeclaredDependency("Assistant", "https://api.openai.com/v1"),
            new DeclaredDependency("Mail", "https://api.sendgrid.net"),
            new DeclaredDependency("Database", "Host=db.internal;Port=5432"));

        var report = app.Host.Services.GetRequiredService<ISovereigntyReport>().Assess();

        report.ArtificialIntelligenceDependencies
            .Select(dependency => dependency.Name)
            .Should().Equal(["Assistant"],
                "a mail relay and a database are not model endpoints, however far away they run");

        report.Dependencies.Single(dependency => dependency.Name == "Assistant")
            .Jurisdiction.Should().Be(Jurisdiction.ThirdCountryProvider,
                "the two questions are independent, and this host answers both");
    }

    [Fact]
    public async Task A_self_hosted_model_under_a_known_name_is_still_recognised()
    {
        await using var app = await Open(
            new DeclaredDependency("Inference", "http://ollama.internal:11434"));

        var report = app.Host.Services.GetRequiredService<ISovereigntyReport>().Assess();

        report.ArtificialIntelligenceDependencies.Should().BeEmpty(
            "recognition is by host name, so a model behind a name of the operator's own "
            + "choosing is invisible here — and the page has to say so rather than imply a "
            + "complete inventory");
    }

    [Fact]
    public async Task The_ai_page_lists_the_endpoint_and_says_what_it_cannot_see()
    {
        await using var app = await Open(
            new DeclaredDependency("Assistant", "https://api.anthropic.com"));

        var html = await app.Client.GetStringAsync("/noelia/ai");

        html.Should().Contain("api.anthropic.com");
        html.Should().Contain("ThirdCountryProvider");
        html.Should().Contain("floor, not a ceiling",
            "an inventory read as exhaustive when it is not is the worst possible input to "
            + "an Article 26 assessment, and the caveat has to be on the page that lists "
            + "endpoints as much as on the one that lists none");
        html.Should().Contain("high-risk AI system under Annex III",
            "the classification the page refuses to make has to be named, or a reader will "
            + "assume it was made");
    }

    [Fact]
    public async Task With_no_model_endpoint_the_ai_page_says_so_without_a_finding()
    {
        await using var app = await Open(
            new DeclaredDependency("Database", "Host=db.internal"));

        var html = await app.Client.GetStringAsync("/noelia/ai");

        html.Should().Contain("No configured destination is a recognised model");
        html.Should().NotContain("db.internal",
            "the AI page lists model endpoints; a database on it would be noise that "
            + "teaches the reader to skim");
    }

    [Fact]
    public async Task The_obligations_page_cites_the_article_and_names_what_it_leaves_open()
    {
        await using var app = await Open(
            new DeclaredDependency("Assistant", "https://api.openai.com/v1"));

        var html = await app.Client.GetStringAsync("/noelia/obligations");

        // The en dash leaves as a character reference; the assertion is about
        // the citation reaching the page, not about how HTML spells a dash.
        html.Should().Contain("GDPR Chapter V (Art. 44");
        html.Should().Contain("Regulation (EU) 2016/679");
        html.Should().Contain("noelia.ai.transfer",
            "an obligation without the check that evidences it is an unsourced claim");
        html.Should().Contain("transfer impact assessment",
            "every citation carries what a person still decides, in the same row, so the "
            + "table cannot be screenshotted into a claim it does not make");
        html.Should().Contain("Nothing here states that an obligation is met");
    }

    [Fact]
    public async Task A_citation_travels_into_the_machine_readable_report()
    {
        await using var app = await Open(
            new DeclaredDependency("Assistant", "https://api.openai.com/v1"));

        var report = JsonSerializer.Deserialize<OperatorReport>(
            await app.Client.GetStringAsync("/noelia/report.json"), Json)!;

        var transfer = report.SecurityChecks.Results
            .Single(result => result.Id == "noelia.ai.transfer");

        transfer.References.Should().NotBeEmpty();
        transfer.References.Should().Contain(reference =>
            reference.Citation == "GDPR Chapter V (Art. 44–49)");
        transfer.References.Should().OnlyContain(reference => reference.Reader.Length > 0,
            "a citation that settled its own article would be exactly the overreach this "
            + "vocabulary exists to avoid");

        report.Sovereignty.Dependencies.Single(d => d.Name == "Assistant")
            .Kind.Should().Be("ArtificialIntelligence");
    }

    [Fact]
    public async Task The_transfer_check_warns_for_a_model_endpoint_under_third_country_law()
    {
        await using var app = await Open(
            new DeclaredDependency("Assistant", "https://api.openai.com/v1"));

        var runner = app.Host.Services.GetRequiredService<ISecurityCheckRunner>();
        var results = await runner.RunAsync();

        results.Single(result => result.Id == "noelia.ai.transfer")
            .Status.Should().Be(SecurityCheckStatus.Warning);

        results.Single(result => result.Id == "noelia.ai.inventory")
            .Summary.Should().Contain("Assistant");
    }

    [Fact]
    public async Task Without_a_model_endpoint_the_ai_checks_stand_down_rather_than_pass_loudly()
    {
        await using var app = await Open(new DeclaredDependency("Database", "Host=db.internal"));

        var results = await app.Host.Services
            .GetRequiredService<ISecurityCheckRunner>().RunAsync();

        results.Single(result => result.Id == "noelia.ai.transfer")
            .Status.Should().Be(SecurityCheckStatus.NotApplicable);

        results.Single(result => result.Id == "noelia.ai.record-keeping")
            .Status.Should().Be(SecurityCheckStatus.NotApplicable,
                "a green tick against an article that does not apply is noise in the one "
                + "document meant to cut through it");
    }

    [Fact]
    public async Task Every_section_in_the_navigation_answers_and_nothing_else_does()
    {
        await using var app = await Open();

        var overview = await app.Client.GetStringAsync("/noelia");

        // Each link the navigation offers, taken from the navigation itself.
        var links = System.Text.RegularExpressions.Regex
            .Matches(overview, "<nav class=\"sections\".*?</nav>",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Single().Value;

        var paths = System.Text.RegularExpressions.Regex
            .Matches(links, "href=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        paths.Should().HaveCount(11);

        foreach (var path in paths)
        {
            (await app.Client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK,
                $"the navigation offers {path}");
        }

        (await app.Client.GetAsync("/noelia/invented")).StatusCode
            .Should().Be(HttpStatusCode.NotFound,
                "a path that names no section must not render the overview under a wrong "
                + "address, or every typo becomes a second URL for the same page");
    }

    [Fact]
    public async Task A_section_page_carries_the_navigation_so_a_reader_can_leave_it()
    {
        await using var app = await Open();

        var html = await app.Client.GetStringAsync("/noelia/health");

        html.Should().Contain("aria-current=\"page\"");
        html.Should().Contain("/noelia/sovereignty",
            "a section page without the navigation is a dead end reached by link");
    }

    [Fact]
    public void Every_cited_reference_names_what_a_person_still_decides()
    {
        RegulatoryReferences.All.Should().OnlyContain(reference =>
            reference.Reader.Length > 0 && reference.Obligation.Length > 0);

        RegulatoryReferences.All.Select(reference => reference.Citation)
            .Should().OnlyHaveUniqueItems(
                "two entries for one article would let the mapping show the same duty twice "
                + "with different words");
    }
}
