using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Audit;
using Noelia.Abstractions.Observability;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Dashboard;
using Noelia.InMemory.Sessions;
using Noelia.Infrastructure.Audit;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.RateLimiting;
using Noelia.Infrastructure.Security.Sessions;

namespace Noelia.Infrastructure.Tests.Dashboard;

[Trait("Category", "Unit")]
public sealed class DashboardTests
{
    private const string Canary = "NOELIA-DASHBOARD-CANARY-47f3b4";

    [Fact]
    public async Task Without_a_visibility_policy_the_route_is_an_indistinguishable_404()
    {
        await using var app = await DashboardHost.Start(options => options.At("/ops/noelia"));

        var response = await app.Client.GetAsync("/ops/noelia");
        var ordinary404 = await app.Client.GetAsync("/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ordinary404.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        (await ordinary404.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_rejected_operator_also_gets_404_and_an_accepted_operator_gets_the_page()
    {
        await using var app = await DashboardHost.Start(options => options
            .VisibleTo(context => context.Request.Headers["X-Noelia-Operator"] == "yes"));

        (await app.Client.GetAsync("/noelia")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/noelia");
        request.Headers.Add("X-Noelia-Operator", "yes");
        var accepted = await app.Client.SendAsync(request);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await accepted.Content.ReadAsStringAsync()).Should().Contain("Read-only operator view");
    }

    [Fact]
    public async Task The_default_authentication_scheme_runs_before_the_visibility_policy()
    {
        await using var app = await DashboardHost.Start(
            options => options.VisibleTo(context => context.User.IsInRole("Operations")),
            additionalServices: services => services
                .AddAuthentication("operator")
                .AddScheme<AuthenticationSchemeOptions, OperatorAuthenticationHandler>(
                    "operator", _ => { }));

        (await app.Client.GetAsync("/noelia")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/noelia");
        request.Headers.Add("X-Test-Operator", "yes");
        var accepted = await app.Client.SendAsync(request);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Production_requires_a_written_reason_before_the_host_can_start()
    {
        var start = () => DashboardHost.Start(
            options => options.VisibleTo(_ => true),
            Environments.Production);

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*InProduction(reason)*");
    }

    [Fact]
    public async Task Production_with_a_reason_still_needs_the_operator_policy()
    {
        await using var app = await DashboardHost.Start(
            options => options.InProduction("private operations network"),
            Environments.Production);

        (await app.Client.GetAsync("/noelia")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_production_reason_is_shown_to_the_operator_in_its_own_words()
    {
        const string reason = "operations network only, reviewed 2026-09 by the platform team";

        await using var app = await DashboardHost.Start(
            options => options.VisibleTo(_ => true).InProduction(reason),
            Environments.Production);

        var html = await app.Client.GetStringAsync("/noelia");

        html.Should().Contain(reason);
        html.Should().NotContain("characters",
            "until 5.1.0 the page printed the length of the reason instead of the reason, "
            + "which let it say anything at all without a reader ever seeing it");
    }

    [Fact]
    public async Task The_production_reason_travels_with_the_operator_access_check()
    {
        const string reason = "operations network only";

        await using var app = await DashboardHost.Start(
            options => options.VisibleTo(_ => true).InProduction(reason),
            Environments.Production);

        var report = app.Host.Services.GetRequiredService<ISecurityCheckReport>();
        var access = report.Latest.Single(result => result.Id == "noelia.dashboard.operator-access");

        access.Summary.Should().Contain(reason,
            "whoever reads the check is the person who has to judge whether the exposure "
            + "still holds, and they cannot judge a reason they are never shown");
    }

    [Fact]
    public async Task Outside_production_no_reason_is_claimed()
    {
        await using var app = await DashboardHost.Start(options => options.VisibleTo(_ => true));

        var html = await app.Client.GetStringAsync("/noelia");

        html.Should().NotContain("Production exposure reason");
    }

    [Fact]
    public async Task The_audit_entry_carries_the_caller_s_correlation_id()
    {
        var trail = new RecordingAuditTrail();

        await using var app = await DashboardHost.Start(
            options => options.VisibleTo(_ => true),
            additionalServices: services => services.AddSingleton<IAuditTrailService>(trail));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/noelia");
        request.Headers.Add(CorrelationId.HeaderName, "TRACE-ME-ACROSS-SERVICES");
        await app.Client.SendAsync(request);

        trail.Correlations.Should().ContainSingle()
            .Which.Should().Be("TRACE-ME-ACROSS-SERVICES",
                "until 5.2.0 this recorded HttpContext.TraceIdentifier — a per-connection id "
                + "local to this process, under a name that promised it correlated across them");
    }

    [Fact]
    public async Task Without_one_the_entry_still_gets_an_identifier()
    {
        var trail = new RecordingAuditTrail();

        await using var app = await DashboardHost.Start(
            options => options.VisibleTo(_ => true),
            additionalServices: services => services.AddSingleton<IAuditTrailService>(trail));

        await app.Client.GetAsync("/noelia");

        trail.Correlations.Should().ContainSingle()
            .Which.Should().NotBeNullOrWhiteSpace(
                "an entry with some identifier beats an entry with none");
    }

    [Fact]
    public async Task Non_get_requests_are_never_a_dashboard_command_surface()
    {
        await using var app = await DashboardHost.Start(options => options.VisibleTo(_ => true));

        var response = await app.Client.PostAsync("/noelia", new StringContent("anything"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task It_shows_an_effectless_running_module_and_exclusion_reasons()
    {
        var passive = new NoeliaModule("Probe.Passive");
        var excluded = new NoeliaModule("Probe.Excluded");

        await using var app = await DashboardHost.StartCustom((services, configuration, environment) =>
        {
            var noelia = new NoeliaBuilder(
                services, configuration, environment, "contract-probe", [], []);

            noelia.Use(passive, _ => { }, _ => { });
            noelia.Without(excluded, "the gateway owns this boundary");
            noelia.UseDashboard(options => options.VisibleTo(_ => true));
            noelia.Build();
        });

        var html = await app.Client.GetStringAsync("/noelia");

        html.Should().Contain("Probe.Passive");
        html.Should().Contain("Running, but its contract declares no requirement or provided effect.");
        html.Should().Contain("Probe.Excluded");
        html.Should().Contain("the gateway owns this boundary");
    }

    [Fact]
    public async Task A_missing_provider_is_shown_with_the_exact_package_and_registration_call()
    {
        var consumer = new NoeliaModule("Probe.Consumer");

        await using var app = await DashboardHost.StartCustom((services, configuration, environment) =>
        {
            var noelia = new NoeliaBuilder(
                services, configuration, environment, "provider-probe", [], []);

            noelia.Use(
                consumer,
                _ => { },
                contract => contract.Requires<IMissingProvider>(
                    new NoeliaProviderHint("Probe.Provider", "AddProbeProvider()")));
            noelia.UseDashboard(options => options.VisibleTo(_ => true));
            noelia.Build();
        });

        var html = await app.Client.GetStringAsync("/noelia");

        html.Should().Contain("IMissingProvider");
        html.Should().Contain("missing");
        html.Should().Contain("Probe.Provider");
        html.Should().Contain("AddProbeProvider()");
    }

    [Fact]
    public async Task Configuration_values_never_reach_the_html()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Dashboard:PrivateKey"] = Canary,
            ["Dashboard:Endpoint"] = "https://internal.example"
        };

        await using var app = await DashboardHost.Start(
            options => options.VisibleTo(_ => true),
            configuration: configuration);

        var html = await app.Client.GetStringAsync("/noelia");

        html.Should().NotContain(Canary);
        html.Should().NotContain("https://internal.example");
        html.Should().Contain("PrivateKey");
        html.Should().Contain("<td>set</td>");
    }

    [Fact]
    public async Task Security_results_are_filtered_to_the_actual_composition()
    {
        await using var app = await DashboardHost.Start(
            options => options.VisibleTo(_ => true),
            additionalServices: services => services.AddSingleton<ISecurityCheckReport>(new StubReport(
            [
                Finding("noelia.dashboard.expected", NoeliaModule.Dashboard),
                Finding("noelia.jwt.must-not-appear", NoeliaModule.Jwt)
            ])));

        var html = await app.Client.GetStringAsync("/noelia");

        html.Should().Contain("noelia.dashboard.expected");
        html.Should().NotContain("noelia.jwt.must-not-appear");
    }

    [Fact]
    public async Task The_page_and_assets_carry_non_cacheable_browser_boundaries()
    {
        await using var app = await DashboardHost.Start(options => options.VisibleTo(_ => true));

        var page = await app.Client.GetAsync("/noelia");
        var css = await app.Client.GetAsync("/noelia/assets/dashboard.css");
        var script = await app.Client.GetAsync("/noelia/assets/dashboard.js");

        page.Headers.CacheControl!.NoStore.Should().BeTrue();
        page.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'none'");
        css.StatusCode.Should().Be(HttpStatusCode.OK);
        css.Content.Headers.ContentType!.MediaType.Should().Be("text/css");
        script.StatusCode.Should().Be(HttpStatusCode.OK);
        script.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
    }

    [Fact]
    public async Task A_dashboard_only_host_reports_exactly_the_dashboard_module_and_check()
    {
        await using var app = await DashboardHost.Start(options => options.VisibleTo(_ => true));

        var composition = app.Host.Services.GetRequiredService<NoeliaComposition>();
        var report = app.Host.Services.GetRequiredService<ISecurityCheckReport>();

        composition.Included.Should().Equal(NoeliaModule.Dashboard);
        report.Latest.Should().ContainSingle();
        report.Latest.Single().Module.Should().Be(NoeliaModule.Dashboard);
        report.Latest.Single().Id.Should().Be("noelia.dashboard.operator-access");
    }

    [Fact]
    public async Task Runtime_surfaces_are_instance_scoped_and_never_render_raw_state()
    {
        const string device = "CANARY-DEVICE-VALUE";
        const string rateKey = "rate:CANARY-SUBJECT-192.0.2.9";
        const string healthDetail = "CANARY-HEALTH-EXCEPTION";

        await using var app = await DashboardHost.Start(
            options => options.VisibleTo(_ => true),
            additionalServices: services =>
            {
                services.AddLogging();
                services.AddSingleton<ISovereignAuditSink, InMemorySovereignAuditSink>();
                services.AddSingleton<IAuditTrailService, AuditTrailService>();
                services.AddSingleton<IRefreshTokenStore, InMemoryRefreshTokenStore>();
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(Options.Create(new TokenSessionOptions()));
                services.AddSingleton<SessionObservations>();
                services.AddSingleton<ITokenSessionService, TokenSessionService>();
                services.AddMemoryCache();
                services.AddSingleton<IDistributedRateLimitStore, InProcessRateLimitStore>();
                services.AddHealthChecks().AddCheck(
                    "readiness",
                    () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(
                        healthDetail,
                        new InvalidOperationException(healthDetail)));
            });

        var sessions = app.Host.Services.GetRequiredService<ITokenSessionService>();
        var signIn = await sessions.SignInAsync(SubjectId.New(), device);
        var rateLimits = app.Host.Services.GetRequiredService<IDistributedRateLimitStore>();
        await rateLimits.SlidingWindowIncrementAsync(rateKey, 1, TimeSpan.FromMinutes(1));
        await rateLimits.SlidingWindowIncrementAsync(rateKey, 1, TimeSpan.FromMinutes(1));

        var html = await app.Client.GetStringAsync("/noelia");

        html.Should().Contain("Instance chain at write time:");
        html.Should().Contain(signIn.Session.ToString());
        html.Should().Contain("<td>set</td>");
        html.Should().Contain("rejected");
        html.Should().Contain("Unhealthy");
        html.Should().NotContain(signIn.RefreshToken);
        html.Should().NotContain(device);
        html.Should().NotContain(rateKey);
        html.Should().NotContain("CANARY-SUBJECT");
        html.Should().NotContain(healthDetail);
    }

    [Fact]
    public async Task The_dashboard_module_joins_the_full_Noelia_composition_and_security_runner()
    {
        await using var app = await DashboardHost.StartCustom((services, configuration, environment) =>
            services.AddNoelia(configuration, environment, "integrated-probe", noelia => noelia
                .UseDashboard(options => options.VisibleTo(_ => true))));

        var composition = app.Host.Services.GetRequiredService<NoeliaComposition>();
        var report = app.Host.Services.GetRequiredService<ISecurityCheckReport>();
        var html = await app.Client.GetStringAsync("/noelia");

        composition.Included.Should().Equal(NoeliaModule.Dashboard);
        report.Latest.Select(result => result.Module).Should()
            // Four Composition-category checks run everywhere: providers,
            // readiness coverage, the data protection key ring and the scope of
            // the audit chain.
            .BeEquivalentTo([
                NoeliaModule.Composition,
                NoeliaModule.Composition,
                NoeliaModule.Composition,
                NoeliaModule.Composition,
                NoeliaModule.Dashboard]);
        html.Should().Contain("noelia.composition.providers");
        html.Should().Contain("noelia.dashboard.operator-access");
    }

    private sealed class RecordingAuditTrail : IAuditTrailService
    {
        public List<string?> Correlations { get; } = [];

        public Task<AuditEvent<T>> RecordAsync<T>(
            string actorId, string capacity, string action, string resource,
            T? before = default, T? after = default, string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            Correlations.Add(correlationId);
            return Task.FromResult(new AuditEvent<T>
            {
                ActorId = actorId, Capacity = capacity, Action = action,
                Resource = resource, CorrelationId = correlationId
            });
        }

        public Task<AuditEvent<T>> RecordAsync<T>(
            string actorId, Capacity capacity, string action, string resource,
            T? before = default, T? after = default, string? correlationId = null,
            CancellationToken cancellationToken = default) =>
            RecordAsync(actorId, capacity.ToString()!, action, resource,
                before, after, correlationId, cancellationToken);
    }

    private static SecurityCheckResult Finding(string id, NoeliaModule module) => new(
        id,
        module,
        SecurityCheckCategory.Composition,
        SecurityCheckStatus.Pass,
        SecurityCheckSeverity.Low,
        "Safe summary.",
        "No action required.");

    private interface IMissingProvider;

    private sealed record StubReport(IReadOnlyList<SecurityCheckResult> Latest) : ISecurityCheckReport;

    private sealed class OperatorAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-Operator"] != "yes")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, "operator"),
                new(ClaimTypes.Role, "Operations")
            ];
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class DashboardHost(IHost host, HttpClient client) : IAsyncDisposable
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
}
