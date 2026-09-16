using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Security.Checks;
using Noelia.Infrastructure.Security.Checks;
using Noelia.Infrastructure.Sovereignty;

namespace Noelia.Infrastructure.Tests.Sovereignty;

/// <summary>
/// The check behind the sentence the product is sold on.
/// </summary>
/// <remarks>
/// "The register of destinations is complete" holds only because an undeclared
/// call fails. Until 6.0.0 nothing asserted that, and a comment in
/// EgressPolicyBuilder claimed the report flagged bypassing clients — which it
/// did not, because no such check existed.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class EgressGuardCheckTests
{
    private static async Task<SecurityCheckResult> Run(Action<IServiceCollection>? arrange = null)
    {
        var services = new ServiceCollection();
        arrange?.Invoke(services);
        var provider = services.BuildServiceProvider();

        return await new EgressGuardSecurityCheck(provider).RunAsync();
    }

    [Fact]
    public async Task Without_a_policy_it_is_not_applicable_and_says_what_that_costs()
    {
        var result = await Run();

        result.Status.Should().Be(SecurityCheckStatus.NotApplicable);
        result.Summary.Should().Contain("not constrained");
    }

    [Fact]
    public async Task A_policy_with_declared_hosts_passes()
    {
        var result = await Run(services => services.AddNoeliaEgressPolicy(
            policy => policy.Allow("openbao.internal")));

        result.Status.Should().Be(SecurityCheckStatus.Pass);
        result.Summary.Should().Contain("refused before they leave");
    }

    /// <summary>
    /// A policy that permits everything is the dangerous case.
    /// </summary>
    /// <remarks>
    /// It reads as enforcement from the outside — an egress policy is
    /// registered — while allowing every destination. The register then lists
    /// what configuration happens to mention, which is an inventory and not a
    /// boundary.
    /// </remarks>
    [Fact]
    public async Task A_policy_that_declares_nothing_fails_rather_than_passing()
    {
        var result = await Run(services => services.AddNoeliaEgressPolicy(_ => { }));

        result.Status.Should().Be(SecurityCheckStatus.Fail);
        result.Summary.Should().Contain("every outbound call is permitted");
    }

    /// <summary>
    /// The limit of the guarantee has to be in the answer.
    /// </summary>
    /// <remarks>
    /// A reader who takes "enforced" to mean "every call" will build on that.
    /// The one sentence naming what is not covered is what keeps a true
    /// statement from being read as a larger one.
    /// </remarks>
    [Fact]
    public async Task A_pass_names_what_it_does_not_cover()
    {
        var result = await Run(services => services.AddNoeliaEgressPolicy(
            policy => policy.Allow("openbao.internal")));

        result.Summary.Should().Contain("new HttpClient()");
    }
}
