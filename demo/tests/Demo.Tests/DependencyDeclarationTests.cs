using Demo.Platform;
using Microsoft.Extensions.Configuration;

namespace Demo.Tests;

public sealed class DependencyDeclarationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Local_state_is_declared_only_for_its_actual_role(bool readsTokens, bool ownsSessions)
    {
        var environment = DemoEnvironment.Read(new ConfigurationBuilder().Build());
        var declarations = DemoDependencies.State(environment, readsTokens, ownsSessions);
        declarations.Any(d => d.Name.StartsWith("Sessions", StringComparison.Ordinal)).Should().Be(ownsSessions);
        declarations.Any(d => d.Name.StartsWith("Token revocation", StringComparison.Ordinal)).Should().Be(readsTokens);
        declarations.Should().OnlyContain(d => d.EndpointOrConnectionString == "http://localhost");
    }

    [Fact]
    public void Redis_endpoints_are_complete_and_do_not_copy_credentials_or_allow_RESP_as_HTTP()
    {
        var environment = DemoEnvironment.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Demo:Providers:Redis"] = "password=synthetic-secret,redis.internal:6379,[::1]:6380" }).Build());
        var declarations = DemoDependencies.State(environment, readsTokens: false, ownsSessions: false);
        declarations.Should().HaveCount(2).And.OnlyContain(d => !d.Name.Contains("revocation", StringComparison.Ordinal));
        declarations.Select(d => d.EndpointOrConnectionString).Should().BeEquivalentTo("redis://redis.internal:6379/", "redis://[::1]:6380/");
        System.Text.Json.JsonSerializer.Serialize(declarations).Should().NotContain("synthetic-secret");
    }

    [Fact]
    public void Gateway_inventory_comes_from_routes_deduplicated_by_destination()
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < 3; i++)
        {
            values[$"Routes:{i}:DownstreamScheme"] = "http";
            values[$"Routes:{i}:DownstreamHostAndPorts:0:Host"] = i == 2 ? "todo-service" : "user-service";
            values[$"Routes:{i}:DownstreamHostAndPorts:0:Port"] = "8080";
        }
        var dependencies = DemoDependencies.GatewayRoutes(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        dependencies.Select(d => d.EndpointOrConnectionString).Should().BeEquivalentTo(
            "http://user-service:8080/", "http://todo-service:8080/");
    }
}
