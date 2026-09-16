using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Extensions;

namespace Noelia.Infrastructure.Tests.Architecture;

/// <summary>
/// Holds every module contract to the container it describes.
/// </summary>
/// <remarks>
/// Both halves are already enforced where it matters: an unmet
/// <c>Requires</c> stops the process at startup, and
/// <c>NoeliaBuilder.EnsureProvisionsWereRegistered</c> refuses to build a
/// composition whose module promised a service it did not register. Writing
/// the contracts for the eight modules that had none found that out
/// immediately — <c>AddSerilog()</c> registers an <c>ILoggerFactory</c>, not a
/// <c>Serilog.ILogger</c>, and the claim was rejected before any test ran.
/// <para>
/// What was not covered is the third state: a module that declares nothing at
/// all. That is what these tests are for.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ModuleContractsAreTrueTests
{
    /// <summary>
    /// The default set composes, which is what proves every declared provision
    /// is real: <c>Build()</c> throws otherwise.
    /// </summary>
    [Fact]
    public void The_default_set_composes_with_every_contract_it_now_declares()
    {
        var compose = () => Compose();

        compose.Should().NotThrow();
    }

    /// <summary>
    /// A module that genuinely registers nothing has to say so.
    /// </summary>
    /// <remarks>
    /// <c>PermissionEnforcement</c> is a pipeline step and registers nothing at
    /// all, which is correct and deliberate. Without a way to declare that, the
    /// dashboard printed the same "declares no requirement or provided effect"
    /// it prints for a module whose contract nobody ever wrote — so a decision
    /// and an omission looked identical.
    /// </remarks>
    [Fact]
    public void No_default_module_leaves_its_contract_unwritten()
    {
        var services = Compose();
        using var provider = services.BuildServiceProvider();
        var composition = provider.GetRequiredService<NoeliaComposition>();

        var silent = composition.Contracts
            .Where(entry => entry.Value.Requirements.Count == 0
                            && entry.Value.Provisions.Count == 0
                            && entry.Value.RegistersNothingBecause is null)
            .Select(entry => entry.Key.Name)
            .ToArray();

        silent.Should().BeEmpty(
            "either the module provides something and says so, or it registers nothing "
            + "and says that — silence leaves the operator guessing which");
    }

    private static IServiceCollection Compose()
    {
        var services = new ServiceCollection();

        services.AddNoelia(
            new ConfigurationBuilder().Build(),
            new Umgebung(),
            "contract-probe",
            noelia => noelia.UseDefaults());

        return services;
    }

    private sealed class Umgebung : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "contract-probe";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
