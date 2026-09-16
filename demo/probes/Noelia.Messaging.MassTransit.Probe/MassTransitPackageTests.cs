using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Messaging;
using Noelia.Messaging.MassTransit;

namespace Noelia.Messaging.MassTransit.Probe;

public sealed class MassTransitPackageTests
{
    [Fact]
    public void Messaging_module_registers_the_event_bus_without_logging_credentials()
    {
        var canary = Environment.GetEnvironmentVariable("NOELIA_GATE_CANARY")
            ?? "MASS-TRANSIT-CANARY-PASSWORD";
        var host = Host.CreateApplicationBuilder();
        host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = "broker.internal",
            ["RabbitMQ:Username"] = "probe",
            ["RabbitMQ:Password"] = canary
        });
        var noelia = new NoeliaBuilder(
            host.Services, host.Configuration, host.Environment, "messaging-probe", [], []);
        var original = Console.Out;
        using var output = new StringWriter();

        try
        {
            Console.SetOut(output);
            noelia.UseMassTransitMessaging(typeof(MassTransitPackageTests).Assembly);
            var composition = noelia.Build();
            using var provider = host.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            composition.Included.Should().Equal(MassTransitNoeliaModule.Module);
            scope.ServiceProvider.GetRequiredService<IEventBus>()
                .GetType().Assembly.GetName().Name.Should().Be("Noelia.Messaging.MassTransit");
            output.ToString().Should().Contain("broker.internal").And.NotContain(canary);
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
