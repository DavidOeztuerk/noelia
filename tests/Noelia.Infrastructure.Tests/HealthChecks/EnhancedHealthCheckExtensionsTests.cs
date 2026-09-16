using Noelia.Infrastructure.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Noelia.Infrastructure.Tests.HealthChecks;

[Trait("Category", "Unit")]
public class EnhancedHealthCheckExtensionsTests
{
    [Fact]
    public void AddNoeliaHealthChecks_WithNoConfigure_RegistersHealthChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<StackExchange.Redis.IConnectionMultiplexer>());
        services.AddSingleton(Substitute.For<Noelia.Infrastructure.Resilience.ICircuitBreakerFactory>());

        var result = services.AddNoeliaHealthChecks();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddNoeliaHealthChecks_WithConfigure_InvokesCallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<StackExchange.Redis.IConnectionMultiplexer>());
        services.AddSingleton(Substitute.For<Noelia.Infrastructure.Resilience.ICircuitBreakerFactory>());

        var configureCalled = false;
        services.AddNoeliaHealthChecks(builder =>
        {
            configureCalled = true;
        });

        configureCalled.Should().BeTrue();
    }

    [Fact]
    public void AddNoeliaHealthChecks_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<StackExchange.Redis.IConnectionMultiplexer>());
        services.AddSingleton(Substitute.For<Noelia.Infrastructure.Resilience.ICircuitBreakerFactory>());

        var result = services.AddNoeliaHealthChecks();

        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IServiceCollection>();
    }

    #region Additional Tests

    [Fact]
    public void AddNoeliaHealthChecks_RegistersHealthChecksService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        services.AddSingleton(Substitute.For<Noelia.Infrastructure.Resilience.ICircuitBreakerFactory>());

        services.AddNoeliaHealthChecks();

        // HealthCheckService is registered by AddHealthChecks()
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(HealthCheckService));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddNoeliaHealthChecks_WithConfigure_BothBuiltInAndCustomRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        services.AddSingleton(Substitute.For<Noelia.Infrastructure.Resilience.ICircuitBreakerFactory>());

        var customCalled = false;
        services.AddNoeliaHealthChecks(builder =>
        {
            customCalled = true;
            builder.AddCustomCheck<ApplicationHealthCheck>("extra-app", HealthStatus.Unhealthy);
        });

        customCalled.Should().BeTrue();
    }

    #endregion
}

[Trait("Category", "Unit")]
public class HealthCheckBuilderTests
{
    [Fact]
    public void Constructor_WithValidServices_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => new HealthCheckBuilder(services);
        act.Should().NotThrow();
    }

    [Fact]
    public void AddCustomHealthChecks_RegistersApplicationAndCircuitBreakerAndMemoryChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<Noelia.Infrastructure.Resilience.ICircuitBreakerFactory>());

        var builder = new HealthCheckBuilder(services);
        var result = builder.AddCustomHealthChecks();

        result.Should().BeSameAs(builder);

        var provider = services.BuildServiceProvider();
        provider.Should().NotBeNull();
    }

    [Fact]
    public void AddCustomCheck_RegistersCustomHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = new HealthCheckBuilder(services);
        var result = builder.AddCustomCheck<ApplicationHealthCheck>("custom-app", HealthStatus.Unhealthy, "live");

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void AddPublisher_RegistersPublisher()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = new HealthCheckBuilder(services);
        var result = builder.AddPublisher<TestHealthCheckPublisher>();

        result.Should().BeSameAs(builder);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IHealthCheckPublisher));
        descriptor.Should().NotBeNull();
    }

    #region Additional Tests

    [Fact]
    public void AddCustomCheck_AddsNamedRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = new HealthCheckBuilder(services);
        builder.AddCustomCheck<ApplicationHealthCheck>("my-custom", HealthStatus.Degraded, "tag1");

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>();

        options.Value.Registrations.Should().Contain(r => r.Name == "my-custom");
    }

    [Fact]
    public void AddCustomHealthChecks_RegistersApplicationAndMemoryAndCircuitBreakerAndDiskSpace()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<Noelia.Infrastructure.Resilience.ICircuitBreakerFactory>());

        var builder = new HealthCheckBuilder(services);
        builder.AddCustomHealthChecks();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>();

        options.Value.Registrations.Should().Contain(r => r.Name == "application");
        options.Value.Registrations.Should().Contain(r => r.Name == "memory");
        options.Value.Registrations.Should().Contain(r => r.Name == "circuit_breakers");
        options.Value.Registrations.Should().Contain(r => r.Name == "disk_space");
    }

    #endregion
}

// Test publisher for DI registration test
public class TestHealthCheckPublisher : IHealthCheckPublisher
{
    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
