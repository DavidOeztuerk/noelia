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

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync();
        Host.Dispose();
    }
}
