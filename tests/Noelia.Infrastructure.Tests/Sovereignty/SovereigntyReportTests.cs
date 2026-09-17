using Noelia.Infrastructure.Sovereignty;
using Noelia.Abstractions.Sovereignty;
using Microsoft.Extensions.Configuration;

namespace Noelia.Infrastructure.Tests.Sovereignty;

[Trait("Category", "Unit")]
public class HostJurisdictionTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.9")]
    [InlineData("openbao.internal")]
    [InlineData("postgres.svc.cluster.local")]
    [InlineData("valkey")]
    public void InfrastructureYouRunYourselfIsRecognised(string host)
    {
        HostJurisdiction.Classify(host).Jurisdiction.Should().Be(Jurisdiction.SelfHosted);
    }

    [Theory]
    [InlineData("my-cache.abc.eu-central-1.cache.amazonaws.com")]
    [InlineData("mystore.vault.azure.com")]
    [InlineData("storage.googleapis.com")]
    [InlineData("app.datadoghq.com")]
    public void ThirdCountryProvidersAreRecognisedEvenOnEuropeanEndpoints(string host)
    {
        // A region in Frankfurt does not change who operates the service or
        // which law reaches it.
        var (jurisdiction, note) = HostJurisdiction.Classify(host);

        jurisdiction.Should().Be(Jurisdiction.ThirdCountryProvider);
        note.Should().Contain("third-country");
    }

    /// <summary>
    /// The provider's own domain, with nothing in front of it.
    /// </summary>
    /// <remarks>
    /// Every recognised domain in the list is written with a leading dot and
    /// matched with EndsWith, which answers correctly for
    /// <c>api.openai.com</c> and not at all for <c>openai.com</c>. A service
    /// configured against the apex — which is where a REST API usually lives —
    /// would come back Undetermined, and Undetermined is the answer that means
    /// "we could not tell".
    /// </remarks>
    [Theory]
    [InlineData("openai.com")]
    [InlineData("amazonaws.com")]
    [InlineData("cloudflare.com")]
    [InlineData("sentry.io")]
    public void AProvidersOwnDomainIsRecognisedWithoutASubdomain(string host)
    {
        HostJurisdiction.Classify(host).Jurisdiction
            .Should().Be(Jurisdiction.ThirdCountryProvider);
    }

    /// <summary>
    /// A file is not a host, however many dots it has.
    /// </summary>
    /// <remarks>
    /// Found by wiring up a service that declared <c>invoices.db</c> as its
    /// ledger. It came back Undetermined — "who operates it cannot be told from
    /// the name" — about a file sitting on the same disk. A false Undetermined
    /// teaches an operator to skip past the real ones.
    /// </remarks>
    [Theory]
    [InlineData("invoices.db")]
    [InlineData("app.sqlite3")]
    [InlineData("/var/lib/app/ledger.db")]
    [InlineData("./data/store.db")]
    [InlineData("C:\\data\\ledger.mdf")]
    [InlineData("settings.json")]
    public void AFileIsSelfHostedAndNotAnOpenQuestion(string path)
    {
        var (jurisdiction, note) = HostJurisdiction.Classify(path);

        jurisdiction.Should().Be(Jurisdiction.SelfHosted);
        note.Should().Contain("file");
    }

    /// <summary>
    /// The layer an application actually calls.
    /// </summary>
    /// <remarks>
    /// The list covered where a system is hosted and not what it talks to, so a
    /// customer running Stripe and SendGrid was told twice that jurisdiction
    /// could not be determined — and read it as an all-clear.
    /// </remarks>
    [Theory]
    [InlineData("api.stripe.com")]
    [InlineData("api.sendgrid.net")]
    [InlineData("api.twilio.com")]
    [InlineData("acme.auth0.com")]
    [InlineData("cluster0.abc.mongodb.net")]
    public void SaasProvidersAreRecognisedToo(string host) =>
        HostJurisdiction.Classify(host).Jurisdiction
            .Should().Be(Jurisdiction.ThirdCountryProvider);

    [Theory]
    [InlineData("db.example.eu")]
    [InlineData("secrets.some-provider.de")]
    [InlineData("8.8.8.8")]
    public void AnythingElseIsUndeterminedRatherThanAssumedSovereign(string host)
    {
        // Not matching the list is not evidence of anything. Saying so is the
        // difference between a report and a rubber stamp.
        HostJurisdiction.Classify(host).Jurisdiction.Should().Be(Jurisdiction.Undetermined);
    }

    [Fact]
    public void NoHostIsUndetermined()
    {
        HostJurisdiction.Classify(null).Jurisdiction.Should().Be(Jurisdiction.Undetermined);
        HostJurisdiction.Classify("  ").Jurisdiction.Should().Be(Jurisdiction.Undetermined);
    }
}

[Trait("Category", "Unit")]
public class SovereigntyReportTests
{
    private static SovereigntyReport ReportFor(
        Dictionary<string, string?> configuration,
        IEgressPolicy? egress = null,
        params DeclaredDependency[] declared) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
            egress ?? EgressPolicy.Unrestricted,
            declared);

    [Fact]
    public void ConnectionStringsArePickedUpWithoutBeingDeclared()
    {
        var report = ReportFor(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=postgres.internal;Database=app;Username=u;Password=p",
            ["ConnectionStrings:Valkey"] = "valkey.internal:6379"
        });

        var assessment = report.Assess();

        assessment.Dependencies.Select(d => d.Name).Should().BeEquivalentTo(["Postgres", "Valkey"]);
        assessment.Dependencies.Should().OnlyContain(d => d.Jurisdiction == Jurisdiction.SelfHosted);
        assessment.NoKnownThirdCountryDependency.Should().BeTrue();
    }

    [Fact]
    public void CredentialsNeverReachTheReport()
    {
        // The report is something people paste into tickets.
        var report = ReportFor(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=db.internal;Username=admin;Password=hunter2"
        });

        var assessment = report.Assess();

        assessment.Dependencies.Single().Host.Should().Be("db.internal");
        assessment.Dependencies.Single().Host.Should().NotContain("hunter2");
    }

    [Fact]
    public void AThirdCountryDependencyIsNamed()
    {
        var report = ReportFor(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cache"] = "my.eu-central-1.cache.amazonaws.com:6379"
        });

        var assessment = report.Assess();

        assessment.NoKnownThirdCountryDependency.Should().BeFalse();
        assessment.ThirdCountryDependencies.Single().Name.Should().Be("Cache");
    }

    [Fact]
    public void DeclaredDependenciesAppearBesideConnectionStrings()
    {
        var report = ReportFor(
            new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = "Host=db.internal" },
            egress: null,
            new DeclaredDependency("Secrets", "https://openbao.internal:8200"),
            new DeclaredDependency("Telemetry", "http://localhost:4317"));

        var assessment = report.Assess();

        assessment.Dependencies.Select(d => d.Name)
            .Should().BeEquivalentTo(["Postgres", "Secrets", "Telemetry"]);
    }

    [Fact]
    public void TheEgressPolicyIsPartOfThePicture()
    {
        var egress = new EgressPolicyBuilder().Allow("openbao.internal").Build();

        var assessment = ReportFor(new Dictionary<string, string?>(), egress).Assess();

        assessment.EgressIsEnforced.Should().BeTrue();
        assessment.DeclaredEgressHosts.Should().BeEquivalentTo(["openbao.internal"]);
    }

    [Fact]
    public void WithoutAnEgressPolicyTheReportSaysSo()
    {
        var assessment = ReportFor(new Dictionary<string, string?>()).Assess();

        assessment.EgressIsEnforced.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://openbao.internal:8200/v1", "openbao.internal")]
    [InlineData("Host=db.internal;Port=5432", "db.internal")]
    [InlineData("Server=sql.internal,1433", "sql.internal")]
    [InlineData("valkey.internal:6379,abortConnect=false", "valkey.internal")]
    [InlineData("Data Source=oracle.internal", "oracle.internal")]
    public void HostsAreExtractedFromTheUsualShapes(string value, string expected)
    {
        SovereigntyReport.ExtractHost(value).Should().Be(expected);
    }
}
