using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Dashboard;

namespace Noelia.Dashboard.Probe;

public sealed class DashboardPackageTests
{
    private const string Canary = "DASHBOARD-PACKAGE-CANARY-4ce122";

    [Fact]
    public void The_project_directly_references_no_other_Noelia_package()
    {
        var project = File.ReadAllText(ProjectFile());
        var packageLines = project.Split('\n')
            .Where(line => line.Contains("PackageReference Include=\"Noelia.", StringComparison.Ordinal))
            .ToArray();

        packageLines.Should().ContainSingle();
        packageLines.Single().Should().Contain("Noelia.Dashboard");
    }

    [Fact]
    public async Task Without_the_operator_policy_the_page_is_404()
    {
        await using var app = await ProbeHost.Start(_ => { });

        var response = await app.Client.GetAsync("/noelia");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_isolated_page_contains_only_the_dashboard_module_and_check()
    {
        await using var app = await ProbeHost.Start(options => options.VisibleTo(_ => true));

        var composition = app.Host.Services.GetRequiredService<NoeliaComposition>();
        var report = app.Host.Services.GetRequiredService<ISecurityCheckReport>();
        var html = await app.Client.GetStringAsync("/noelia");

        composition.Included.Should().Equal(NoeliaModule.Dashboard);
        report.Latest.Should().ContainSingle(result =>
            result.Module == NoeliaModule.Dashboard
            && result.Id == "noelia.dashboard.operator-access");
        html.Should().Contain("Dashboard");
        html.Should().NotContain("Jwt contract");
        html.Should().NotContain("SecurityHeaders contract");
    }

    [Fact]
    public async Task Configuration_canaries_are_replaced_by_shapes()
    {
        await using var app = await ProbeHost.Start(
            options => options.VisibleTo(_ => true),
            new Dictionary<string, string?>
            {
                ["Dashboard:PrivateKey"] = Canary,
                ["Dashboard:ConnectionString"] = $"Server={Canary}"
            });

        var html = await app.Client.GetStringAsync("/noelia");

        html.Should().NotContain(Canary);
        html.Should().Contain("<td>set</td>");
        html.Should().NotContain($"{Canary.Length} characters");
        html.Should().Contain("PrivateKey");
    }

    [Fact]
    public async Task The_surface_is_read_only_and_assets_share_its_policy()
    {
        await using var app = await ProbeHost.Start(options => options
            .VisibleTo(context => context.Request.Headers["X-Operator"] == "yes"));

        var post = await app.Client.PostAsync("/noelia", new StringContent("change=true"));
        var hiddenAsset = await app.Client.GetAsync("/noelia/assets/dashboard.css");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/noelia/assets/dashboard.css");
        request.Headers.Add("X-Operator", "yes");
        var visibleAsset = await app.Client.SendAsync(request);

        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
        hiddenAsset.StatusCode.Should().Be(HttpStatusCode.NotFound);
        visibleAsset.StatusCode.Should().Be(HttpStatusCode.OK);
        visibleAsset.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private static string ProjectFile() => Path.Combine(
        RepositoryRoot(), "probes", "Noelia.Dashboard.Probe", "Noelia.Dashboard.Probe.csproj");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Noelia.TodoDemo.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Noelia.TodoDemo.sln was not found.");
    }

    private sealed class ProbeHost(IHost host, HttpClient client) : IAsyncDisposable
    {
        internal IHost Host { get; } = host;
        internal HttpClient Client { get; } = client;

        internal static async Task<ProbeHost> Start(
            Action<NoeliaDashboardBuilder> dashboard,
            IReadOnlyDictionary<string, string?>? values = null)
        {
            var hostBuilder = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseEnvironment("Development")
                    .UseTestServer()
                    .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(values))
                    .ConfigureServices((context, services) => services.AddNoeliaDashboard(
                        context.Configuration,
                        context.HostingEnvironment,
                        "dashboard-package-probe",
                        dashboard))
                    .Configure(app => app.Run(context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    })));

            var host = await hostBuilder.StartAsync();
            return new ProbeHost(host, host.GetTestClient());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}
