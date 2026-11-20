using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.ServiceTokens;
using Noelia.Infrastructure.Tests.Dashboard;
using Noelia.Infrastructure.Tests.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.Checks;

/// <summary>
/// ADR-0004: the report is read straight after a real start, never through a hand-made
/// <c>ISecurityCheckRunner.RunAsync()</c> afterwards.
/// </summary>
/// <remarks>
/// The check reads the mapped routes, and ASP.NET only copies them into the container's
/// <c>EndpointDataSource</c> in <c>GenericWebHostService.StartAsync</c>. A test that runs
/// the check after the start sees the routes and therefore could not see that the report
/// the host produced at startup was computed over none. Both ways a service is built are
/// covered because the hosted-service order differs between them.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class ServiceCallerPolicyStartupReportTests
{
    private const string CheckId = "noelia.m2m.caller-policy";

    public enum Way
    {
        WebApplication,
        HostBuilder
    }

    public static TheoryData<Way> Ways => new() { Way.WebApplication, Way.HostBuilder };

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_correct_issuer_passes_in_the_report_after_start(Way way)
    {
        var config = StandardConfig();
        await using var host = await StartedHost.Start(
            way, config, noelia => Standard(noelia, config),
            endpoints => endpoints.MapServiceTokenEndpoint().RequireRateLimiting("token"));

        var result = host.Result();

        result.Status.Should().Be(SecurityCheckStatus.Pass, result.Summary);
        // Only what was examined is claimed: an issuer-only service has no door and no handler to vouch for.
        result.Summary.Should().NotContain("refusal handler").And.NotContain("door");
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_receiver_with_a_door_and_its_fallback_passes_in_the_report_after_start(Way way)
    {
        await using var host = await StartedHost.Start(way, Config(), Receiver, endpoints =>
        {
            var doors = endpoints.MapGroup("/internal");
            doors.MapGet("/account", () => "x").RequireServiceCaller("advisor");
            doors.MapServiceFallback();
        });

        var result = host.Result();

        result.Status.Should().Be(SecurityCheckStatus.Pass, result.Summary);
        result.Summary.Should().Contain("1 service door");
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_receiver_with_an_anonymous_door_fails_in_the_report_after_start(Way way)
    {
        await using var host = await StartedHost.Start(way, Config(), Receiver, endpoints =>
        {
            var doors = endpoints.MapGroup("/internal").AllowAnonymous();
            doors.MapGet("/account", () => "x").RequireServiceCaller("advisor");
            doors.MapServiceFallback();
        });

        host.Result().Status.Should().Be(SecurityCheckStatus.Fail);
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_receiver_without_any_door_is_a_warning_and_not_a_pass(Way way)
    {
        await using var host = await StartedHost.Start(
            way, Config(), Receiver, endpoints => endpoints.MapGet("/plain", () => "x"));

        var result = host.Result();

        result.Status.Should().Be(SecurityCheckStatus.Warning);
        result.Summary.Should().ContainEquivalentOf("no service door is mapped");
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_receiver_and_issuer_with_no_door_and_no_token_endpoint_fails(Way way)
    {
        var config = StandardConfig();
        await using var host = await StartedHost.Start(
            way, config,
            noelia => Standard(noelia, config).UseServiceCallers(ServiceCallerHost.ThisAudience),
            endpoints => endpoints.MapGet("/plain", () => "x"));

        var result = host.Result();

        // The error outranks the warning about the missing doors.
        result.Status.Should().Be(SecurityCheckStatus.Fail);
        result.Summary.Should().Contain("0 token endpoints");
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task Before_the_routes_are_built_the_check_says_so_instead_of_passing_or_failing(Way way)
    {
        await using var host = StartedHost.Build(way, Config(), Receiver, endpoints =>
        {
            var doors = endpoints.MapGroup("/internal");
            doors.MapGet("/account", () => "x").RequireServiceCaller("advisor");
            doors.MapServiceFallback();
        });

        var results = await host.Services.GetRequiredService<ISecurityCheckRunner>().RunAsync();
        var result = results.Single(r => r.Id == CheckId);

        result.Status.Should().Be(SecurityCheckStatus.Warning);
        result.Summary.Should().ContainEquivalentOf("routes not built yet");
        host.Result().Status.Should().Be(SecurityCheckStatus.Warning);
    }

    [Fact]
    public async Task A_service_with_nothing_composed_also_says_not_built_yet_before_the_routes_exist()
    {
        await using var host = StartedHost.Build(
            Way.HostBuilder, Config(), noelia => { }, endpoints => endpoints.MapGet("/plain", () => "x"));

        var results = await host.Services.GetRequiredService<ISecurityCheckRunner>().RunAsync();

        // A door without a module is why this is a Composition check, so "nothing composed"
        // cannot be answered before the routes are known.
        results.Single(r => r.Id == CheckId).Summary.Should().ContainEquivalentOf("routes not built yet");
    }

    [Fact]
    public async Task The_routes_built_state_belongs_to_one_container_and_is_not_static()
    {
        await using var started = await StartedHost.Start(
            Way.HostBuilder, Config(), Receiver, endpoints => endpoints.MapGet("/plain", () => "x"));
        await using var notStarted = StartedHost.Build(
            Way.HostBuilder, Config(), Receiver, endpoints => endpoints.MapGet("/plain", () => "x"));

        var results = await notStarted.Services.GetRequiredService<ISecurityCheckRunner>().RunAsync();

        results.Single(r => r.Id == CheckId).Summary.Should().ContainEquivalentOf("routes not built yet");
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task A_failure_of_the_second_run_is_caught_and_does_not_abort_the_start(Way way)
    {
        var runner = new FailingSecondRunRunner();
        await using var host = await StartedHost.Start(
            way, Config(), Receiver, endpoints => endpoints.MapGet("/plain", () => "x"),
            services => services.AddSingleton<ISecurityCheckRunner>(runner));

        runner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task No_built_in_check_of_the_standalone_dashboard_path_reads_routes()
    {
        // StandaloneDashboardSecurityReport runs the checks once, in StartAsync, before the
        // routes exist (ADR-0004, Grenze). That is only harmless while nothing built in
        // reads routes there; the caller-policy check does, so it must not be on that path.
        await using var host = await DashboardHost.Start(dashboard => { });

        host.Host.Services.GetServices<ISecurityCheck>()
            .Should().NotContain(check => check.Id == CheckId);
        host.Host.Services.GetServices<EndpointDataSource>().Should().BeEmpty(
            "a built-in check that read routes would need one, and would see it empty at startup");
    }

    private static void Receiver(NoeliaBuilder noelia) => noelia
        .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
        .UseServiceCallers(ServiceCallerHost.ThisAudience);

    private sealed class FailingSecondRunRunner : ISecurityCheckRunner
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<SecurityCheckResult>> RunAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls > 1)
            {
                throw new InvalidOperationException("the second run fails");
            }

            return Task.FromResult<IReadOnlyList<SecurityCheckResult>>([]);
        }
    }

    /// <summary>A service built either way, so the report is read the way an operator meets it.</summary>
    private sealed class StartedHost : IAsyncDisposable
    {
        private readonly Func<Task> _start;
        private readonly Func<ValueTask> _dispose;

        private StartedHost(IServiceProvider services, Func<Task> start, Func<ValueTask> dispose)
        {
            Services = services;
            _start = start;
            _dispose = dispose;
        }

        public IServiceProvider Services { get; }

        public SecurityCheckResult Result() =>
            Services.GetRequiredService<ISecurityCheckReport>().Latest.Single(r => r.Id == CheckId);

        public static async Task<StartedHost> Start(
            Way way,
            IConfiguration configuration,
            Action<NoeliaBuilder> compose,
            Action<IEndpointRouteBuilder> map,
            Action<IServiceCollection>? services = null)
        {
            var host = Build(way, configuration, compose, map, services);
            await host._start();
            return host;
        }

        public static StartedHost Build(
            Way way,
            IConfiguration configuration,
            Action<NoeliaBuilder> compose,
            Action<IEndpointRouteBuilder> map,
            Action<IServiceCollection>? services = null)
        {
            void Register(IServiceCollection sv)
            {
                sv.AddLogging();
                sv.AddRouting();
                sv.AddNoelia(configuration, new Env(), "billing", noelia =>
                {
                    noelia.UseDefaults().Without(NoeliaModule.Logging, "not the subject of this test");
                    compose(noelia);
                });
                services?.Invoke(sv);
            }

            if (way == Way.WebApplication)
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseTestServer();
                Register(builder.Services);
                var app = builder.Build();
                app.UseNoelia(new Env(), "billing");
                map(app);
                return new StartedHost(
                    app.Services,
                    () => app.StartAsync(),
                    async () =>
                    {
                        await app.StopAsync();
                        await app.DisposeAsync();
                    });
            }

            var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(Register)
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseNoelia(new Env(), "billing");
                        app.UseEndpoints(map);
                    }))
                .Build();
            return new StartedHost(
                host.Services,
                () => host.StartAsync(),
                async () =>
                {
                    await host.StopAsync();
                    host.Dispose();
                });
        }

        public ValueTask DisposeAsync() => _dispose();
    }
}
