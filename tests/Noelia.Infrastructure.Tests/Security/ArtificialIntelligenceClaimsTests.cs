using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Audit;
using Noelia.Abstractions.Security.Checks;
using Noelia.Abstractions.Sovereignty;
using Noelia.Infrastructure.Security.Checks;
using Noelia.Infrastructure.Sovereignty;
using NSubstitute;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// R20: the AI checks must not conclude more than they observe. Recognition is by
/// host name, so "nothing recognised" is "not determined", never "no duty"; a
/// registered audit sink is capability, not evidence that a call was recorded.
/// </summary>
[Trait("Category", "Unit")]
public class ArtificialIntelligenceClaimsTests
{
    private static ServiceProvider Build(
        DeclaredArtificialIntelligenceUse? declaration,
        Action<IServiceCollection>? more = null,
        params DeclaredDependency[] dependencies)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddNoeliaSovereigntyReport(dependencies);

        if (declaration is not null)
        {
            services.DeclareNoeliaArtificialIntelligenceUse(
                declaration.UsesArtificialIntelligence, declaration.ModelEndpoint);
        }

        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static Task<SecurityCheckResult> Run<TCheck>(IServiceProvider provider)
        where TCheck : ISecurityCheck =>
        ActivatorUtilities.CreateInstance<TCheck>(provider).RunAsync();

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("https://llm.internal.example")]
    public async Task A_local_or_self_named_model_host_is_never_read_as_no_duty(string endpoint)
    {
        using var provider = Build(null, null, new DeclaredDependency("Model", endpoint));

        var inventory = await Run<ArtificialIntelligenceInventoryCheck>(provider);
        var record = await Run<ArtificialIntelligenceRecordKeepingCheck>(provider);

        inventory.Status.Should().Be(SecurityCheckStatus.Warning);
        inventory.Summary.Should().Contain("Not determined");
        record.Status.Should().Be(SecurityCheckStatus.Warning);
        record.Summary.Should().Contain("Not determined")
            .And.NotContain("no record-keeping duty");
        record.Remediation.Should().Contain("DeclareArtificialIntelligence");
    }

    [Fact]
    public async Task No_declaration_and_no_host_is_not_determined()
    {
        using var provider = Build(null);

        (await Run<ArtificialIntelligenceInventoryCheck>(provider)).Status
            .Should().Be(SecurityCheckStatus.Warning);
        (await Run<ArtificialIntelligenceRecordKeepingCheck>(provider)).Status
            .Should().Be(SecurityCheckStatus.Warning);
    }

    [Fact]
    public async Task Declared_no_ai_is_worded_as_a_declaration_not_an_observation()
    {
        using var provider = Build(new DeclaredArtificialIntelligenceUse(false));

        var inventory = await Run<ArtificialIntelligenceInventoryCheck>(provider);
        var record = await Run<ArtificialIntelligenceRecordKeepingCheck>(provider);

        inventory.Status.Should().Be(SecurityCheckStatus.NotApplicable);
        inventory.Summary.Should().Contain("declared").And.Contain("not an observation");
        record.Status.Should().Be(SecurityCheckStatus.NotApplicable);
        record.Summary.Should().Contain("declaration, not an observation");
    }

    [Fact]
    public async Task Declared_yes_without_a_recognised_host_does_not_become_no_duty()
    {
        using var provider = Build(new DeclaredArtificialIntelligenceUse(true));

        var record = await Run<ArtificialIntelligenceRecordKeepingCheck>(provider);

        record.Status.Should().Be(SecurityCheckStatus.Warning,
            "declared use with no sink asks the sink question instead of standing down");
        record.Summary.Should().Contain("no audit sink is registered");
        (await Run<ArtificialIntelligenceInventoryCheck>(provider)).Status
            .Should().Be(SecurityCheckStatus.Warning);
    }

    [Fact]
    public async Task A_declared_endpoint_enters_the_inventory_whatever_its_host_is_called()
    {
        using var provider = Build(
            new DeclaredArtificialIntelligenceUse(true, "http://localhost:11434"));

        var assessment = provider.GetRequiredService<ISovereigntyReport>().Assess();
        assessment.ArtificialIntelligenceDependencies.Should().ContainSingle(
            "the declaration is how an operator raises the host-name floor");

        (await Run<ArtificialIntelligenceInventoryCheck>(provider)).Summary
            .Should().Contain("Declared model endpoint");
    }

    [Fact]
    public async Task A_known_provider_host_behaves_as_before()
    {
        using var provider = Build(null, null,
            new DeclaredDependency("Assistant", "https://api.openai.com/v1"));

        var inventory = await Run<ArtificialIntelligenceInventoryCheck>(provider);
        var transfer = await Run<ArtificialIntelligenceTransferCheck>(provider);

        inventory.Status.Should().Be(SecurityCheckStatus.Pass);
        inventory.Summary.Should().Contain("Assistant");
        transfer.Status.Should().Be(SecurityCheckStatus.Warning);
    }

    [Fact]
    public async Task A_registered_readable_chained_sink_is_not_claimed_to_record_model_calls()
    {
        using var provider = Build(null,
            services =>
            {
                var sink = Substitute.For<IChainedSovereignAuditSink, IReadableSovereignAuditSink>();
                services.AddSingleton<ISovereignAuditSink>(sink);
                services.AddSingleton(sink);
                services.AddSingleton((IReadableSovereignAuditSink)sink);
            },
            new DeclaredDependency("Assistant", "https://api.openai.com/v1"));

        var record = await Run<ArtificialIntelligenceRecordKeepingCheck>(provider);

        record.Summary.Should().Contain("Whether model calls are recorded in it is not observed");
        record.Summary.Should().NotContain("written automatically");
    }
}
