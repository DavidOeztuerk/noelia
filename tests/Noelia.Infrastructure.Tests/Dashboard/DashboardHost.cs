using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Dashboard;

namespace Noelia.Infrastructure.Tests.Dashboard;

/// <summary>
/// A real host with a real dashboard on it.
/// </summary>
/// <remarks>
/// Shared by the page tests and the report tests on purpose: both exercise the
/// same middleware, and a second harness would let the two drift into testing
/// subtly different compositions.
/// </remarks>
internal sealed class DashboardHost(IHost host, HttpClient client) : IAsyncDisposable
{
    internal IHost Host { get; } = host;
    internal HttpClient Client { get; } = client;

    internal static Task<DashboardHost> Start(
        Action<NoeliaDashboardBuilder> dashboard,
        string environment = "Development",
        Action<IServiceCollection>? additionalServices = null,
        IReadOnlyDictionary<string, string?>? configuration = null) =>
        StartCustom(
            (services, appConfiguration, hostEnvironment) =>
            {
                services.AddNoeliaDashboard(
                    appConfiguration, hostEnvironment, "dashboard-probe", dashboard);
                additionalServices?.Invoke(services);
            },
            environment,
            configuration);

    internal static async Task<DashboardHost> StartCustom(
        Action<IServiceCollection, IConfiguration, IHostEnvironment> registrations,
        string environment = "Development",
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseEnvironment(environment)
                .UseTestServer()
                .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration))
                .ConfigureServices((context, services) =>
                    registrations(services, context.Configuration, context.HostingEnvironment))
                .Configure(app => app.Run(context =>
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                })));

        var host = await hostBuilder.StartAsync();
        return new DashboardHost(host, host.GetTestClient());
    }

    /// <summary>
    /// Every section of the dashboard, fetched and joined.
    /// </summary>
    /// <remarks>
    /// <para>Since 6.4.0 the operator view is one page per section, so "the
    /// dashboard shows X" is a statement about a set of pages. Fetching all of
    /// them keeps those assertions saying what they always said, and adds one
    /// they could not make before: every section named in the navigation
    /// answers, because a link to a path that 404s would fail here rather than
    /// in somebody's browser.</para>
    ///
    /// <para>Use this where the assertion is about the dashboard. Where it is
    /// about a particular section — that a finding appears on the page built
    /// for it — fetch that path directly, or the test would still pass with the
    /// content on the wrong page.</para>
    /// </remarks>
    /// <param name="root">The dashboard root, as configured.</param>
    internal async Task<string> WholeDashboard(string root = "/noelia")
    {
        string[] sections =
        [
            "", "/composition", "/configuration", "/security", "/sovereignty", "/ai",
            "/obligations", "/audit", "/sessions", "/rate-limits", "/health"
        ];

        var joined = new System.Text.StringBuilder();

        foreach (var section in sections)
        {
            var response = await Client.GetAsync(root + section);

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"the navigation links to {root}{section}");

            joined.Append(await response.Content.ReadAsStringAsync());
        }

        return joined.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync();
        Host.Dispose();
    }
}
