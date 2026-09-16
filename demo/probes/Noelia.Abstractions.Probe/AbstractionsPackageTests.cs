using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;

namespace Noelia.Abstractions.Probe;

public sealed class AbstractionsPackageTests
{
    [Fact]
    public void External_module_contract_is_immutable_and_names_its_provider_path()
    {
        var services = new ServiceCollection();
        var module = new NoeliaModule("Probe.Consumer");
        var noelia = new NoeliaBuilder(
            services,
            new ConfigurationBuilder().Build(),
            new ProbeEnvironment(),
            "abstractions-probe",
            [],
            []);

        noelia.Use(
            module,
            builder => builder.Services.AddSingleton<IProvided, Provided>(),
            contract => contract
                .Requires<IMissing>(new NoeliaProviderHint("Probe.Provider", "AddProbeProvider()"))
                .Provides<IProvided>("Probe.Consumer", "UseProbeConsumer()"));

        var composition = noelia.Build();
        var contract = composition.Contracts[module];

        composition.Included.Should().Equal(module);
        contract.Requirements.Should().ContainSingle().Which.Providers
            .Should().ContainSingle().Which.Should().Be(
                new NoeliaProviderHint("Probe.Provider", "AddProbeProvider()"));
        contract.Provisions.Should().ContainSingle().Which.ServiceType.Should().Be<IProvided>();
        typeof(NoeliaBuilder).Assembly.GetName().Name.Should().Be("Noelia.Abstractions");
    }

    private interface IMissing;
    private interface IProvided;
    private sealed class Provided : IProvided;

    private sealed class ProbeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Noelia.Abstractions.Probe";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
