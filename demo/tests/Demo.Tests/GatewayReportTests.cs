using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Demo.Platform;

namespace Demo.Tests;

public sealed class GatewayReportTests
{
    private const string Secret = "synthetic-gateway-operator";

    [Fact]
    public async Task Gateway_report_matches_its_own_baseline_without_token_modules()
    {
        await using var factory = new GatewayFactory("Development");
        using var client = factory.CreateClient();
        await Demo.TestSupport.DemoBaselineAssertions.MatchAsync(client, "gateway-memory");
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Gateway_exposes_only_authenticated_JSON_and_declares_its_actual_downstreams(string environment)
    {
        await using var factory = new GatewayFactory(environment);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/noelia/report.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Add("X-Noelia-Operator", "wrong-synthetic-key");
        (await client.GetAsync("/noelia/report.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Remove("X-Noelia-Operator");
        client.DefaultRequestHeaders.Add("X-Noelia-Operator", Secret);
        using var report = await client.GetAsync("/noelia/report.json");
        report.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await report.Content.ReadAsStringAsync();
        body.Should().NotContain(Secret).And.NotContain(TestMasterKey.Value);
        using var document = JsonDocument.Parse(body);
        var dependencies = document.RootElement.GetProperty("sovereignty").GetProperty("dependencies").EnumerateArray().ToArray();
        dependencies.Should().Contain(d => d.GetProperty("host").GetString() == "user-service");
        dependencies.Should().Contain(d => d.GetProperty("host").GetString() == "todo-service");
        dependencies.Should().NotContain(d => d.GetProperty("name").GetString()!.StartsWith("Sessions", StringComparison.Ordinal)
            || d.GetProperty("name").GetString()!.StartsWith("Token revocation", StringComparison.Ordinal));
        (await client.GetAsync("/noelia/audit-chain.json")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var route in new[] { "/noelia", "/noelia/composition", "/noelia/assets/dashboard.js", "/noelia/report.json/extra" })
            (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Report_only_mode_requires_a_secret_even_in_development()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Demo:Dashboard:Visibility"] = "OperatorReports" }).Build();
        var read = () => DemoEnvironment.Read(config);
        read.Should().Throw<InvalidOperationException>();
    }

    private sealed class GatewayFactory(string environment) : WebApplicationFactory<Gateway.Api.ServiceEntryPoint>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Demo:Providers:Redis", "");
            builder.UseSetting("Demo:Dashboard:OperatorSecret", Secret);
            builder.UseSetting("Encryption:MasterKey", TestMasterKey.Value);
        }
    }
}
