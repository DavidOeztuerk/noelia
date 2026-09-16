using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Infrastructure.Security.Checks;

namespace Noelia.Infrastructure.Tests.Security.Checks;

/// <summary>
/// Pins that an uncovered readiness endpoint is reported rather than passed.
/// </summary>
/// <remarks>
/// <c>/health/ready</c> filters registrations by the <c>ready</c> tag, an empty
/// report is <see cref="HealthStatus.Healthy"/>, and the endpoint therefore
/// answers 200 over an unreachable database. That response is indistinguishable
/// from a service whose every dependency is up, which is the reason this check
/// exists at all — so these tests assert the verdict, never the return value.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ReadinessCoverageSecurityCheckTests
{
    [Fact]
    public async Task A_readiness_endpoint_with_no_tagged_check_fails()
    {
        var check = Check(Composed(), registerReadinessCheck: false);

        var result = await check.RunAsync();

        result.Status.Should().Be(SecurityCheckStatus.Fail);
        result.Summary.Should().Contain("without checking anything");
    }

    [Fact]
    public async Task A_registration_tagged_ready_covers_it()
    {
        var check = Check(Composed(), registerReadinessCheck: true);

        var result = await check.RunAsync();

        result.Status.Should().Be(SecurityCheckStatus.Pass);
        result.Summary.Should().Contain("1 registered check");
    }

    [Fact]
    public async Task A_check_carrying_another_tag_does_not_count_as_readiness()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks()
            .AddCheck<AlwaysHealthy>("liveness-only", tags: ["live"]);
        using var provider = services.BuildServiceProvider();

        var result = await new ReadinessCoverageSecurityCheck(provider, Composed()).RunAsync();

        result.Status.Should().Be(SecurityCheckStatus.Fail);
    }

    [Fact]
    public async Task Without_the_health_checks_module_there_is_nothing_to_cover()
    {
        var composition = new NoeliaComposition(
            [],
            new Dictionary<NoeliaModule, string>(),
            new Dictionary<NoeliaModule, NoeliaModuleContract>());

        var check = Check(composition, registerReadinessCheck: false);

        (await check.RunAsync()).Status.Should().Be(SecurityCheckStatus.NotApplicable);
    }

    [Fact]
    public async Task The_verdict_names_no_registration_and_no_connection_string()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks()
            .AddCheck<AlwaysHealthy>(
                "postgres://admin:secret@example.invalid",
                tags: ["ready"]);
        using var provider = services.BuildServiceProvider();

        var result = await new ReadinessCoverageSecurityCheck(provider, Composed()).RunAsync();

        result.Summary.Should().NotContain("postgres");
        result.Summary.Should().NotContain("secret");
    }

    private static NoeliaComposition Composed() => new(
        [NoeliaModule.HealthChecks],
        new Dictionary<NoeliaModule, string>(),
        new Dictionary<NoeliaModule, NoeliaModuleContract>());

    private static ReadinessCoverageSecurityCheck Check(
        NoeliaComposition composition,
        bool registerReadinessCheck)
    {
        var services = new ServiceCollection();
        var builder = services.AddHealthChecks();

        if (registerReadinessCheck)
        {
            builder.AddCheck<AlwaysHealthy>("probe", tags: ["ready"]);
        }

        var provider = services.BuildServiceProvider();
        return new ReadinessCoverageSecurityCheck(provider, composition);
    }

    private sealed class AlwaysHealthy : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Healthy());
    }
}
