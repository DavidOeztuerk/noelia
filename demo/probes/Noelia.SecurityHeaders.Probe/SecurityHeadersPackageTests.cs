using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Noelia.SecurityHeaders.Probe;

public sealed class SecurityHeadersPackageTests
{
    private static string Canary => Environment.GetEnvironmentVariable("NOELIA_GATE_CANARY")
        ?? "INFRASTRUCTURE-PROBE-CANARY-MUST-NOT-LEAK";

    [Fact]
    public async Task Composition_contains_only_the_module_under_test()
    {
        await using var app = await CreateAppAsync();

        var composition = app.Services.GetRequiredService<NoeliaComposition>();

        composition.Included.Select(module => module.Name)
            .Should().Equal(NoeliaModule.SecurityHeaders.Name);
        composition.Excluded.Should().BeEmpty();
    }

    [Fact]
    public async Task Startup_security_report_contains_only_composition_and_the_active_module()
    {
        await using var app = await CreateAppAsync();

        var report = app.Services.GetRequiredService<ISecurityCheckReport>();

        // Composition-category checks run in every host, whether or not the
        // module they report on is composed — a probe that exposes no readiness
        // endpoint and keeps no audit trail gets NotApplicable, not a finding.
        report.Latest.Select(result => result.Id).Should().Equal(
            "noelia.audit.chain-scope",
            "noelia.composition.providers",
            "noelia.dataprotection.key-ring",
            "noelia.egress.guard",
            "noelia.headers.browser-baseline",
            "noelia.health.readiness-coverage");
        report.Latest.Should().OnlyContain(result =>
            result.Module == NoeliaModule.Composition
            || result.Module == NoeliaModule.SecurityHeaders);
    }

    [Fact]
    public async Task No_anonymous_security_endpoint_is_exposed()
    {
        await using var app = await CreateAppAsync();

        using var response = await app.GetTestClient().GetAsync("/security");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Https_response_carries_the_browser_security_boundary()
    {
        await using var app = await CreateAppAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://probe.example/page");

        using var response = await app.GetTestClient().SendAsync(request);

        response.EnsureSuccessStatusCode();
        Header(response, "Strict-Transport-Security").Should().Contain("max-age=31536000");
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "X-Frame-Options").Should().Be("DENY");
        Header(response, "Referrer-Policy").Should().Be("strict-origin-when-cross-origin");
        Header(response, "Permissions-Policy").Should().NotBeNullOrWhiteSpace();
        Header(response, "Cross-Origin-Opener-Policy").Should().Be("same-origin");
        Header(response, "Cross-Origin-Resource-Policy").Should().Be("same-origin");
        Header(response, "Content-Security-Policy").Should().Contain("default-src");
    }

    [Fact]
    public async Task Api_response_uses_csp_frame_ancestors_instead_of_legacy_frame_header()
    {
        await using var app = await CreateAppAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://probe.example/api/probe");

        using var response = await app.GetTestClient().SendAsync(request);

        response.EnsureSuccessStatusCode();
        response.Headers.Contains("X-Frame-Options").Should().BeFalse();
        Header(response, "Content-Security-Policy").Should().Contain("frame-ancestors 'none'");
        Header(response, "X-API-Version").Should().Be("1.0");
    }

    [Fact]
    public async Task Missing_module_provider_refuses_startup_with_the_exact_remedy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNoelia(
            builder.Configuration,
            builder.Environment,
            "missing-provider-probe",
            noelia => noelia.Use(NoeliaModule.TokenSessions));
        await using var app = builder.Build();

        var start = () => app.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*module 'TokenSessions'*IRefreshTokenStore*")
            .WithMessage("*Noelia.InMemory*UseInMemoryRefreshTokens()*")
            .WithMessage("*Noelia.Data.EntityFrameworkCore*AddEntityFrameworkRefreshTokens<TContext>()*");
    }

    [Fact]
    public async Task Security_results_and_logs_never_contain_configuration_canaries()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await CreateAppAsync(logs);
        var report = app.Services.GetRequiredService<ISecurityCheckReport>();

        var material = JsonSerializer.Serialize(report.Latest) + string.Join('\n', logs.Messages);

        material.Should().NotContain(Canary);
        report.Latest.Should().OnlyContain(result =>
            result.Status == SecurityCheckStatus.Pass
            || result.Status == SecurityCheckStatus.NotApplicable);
    }

    private static async Task<WebApplication> CreateAppAsync(ILoggerProvider? loggerProvider = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(SecurityHeadersPackageTests).Assembly.GetName().Name,
            EnvironmentName = Environments.Production
        });

        builder.WebHost.UseTestServer();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SecurityHeaders:EnableHsts"] = "true",
            ["SecurityHeaders:HstsMaxAge"] = "31536000",
            ["SecurityHeaders:EnableDefaultCsp"] = "true",
            ["SecurityHeaders:EnablePermissionsPolicy"] = "true",
            ["SecurityHeaders:EnableCrossOriginOpenerPolicy"] = "true",
            ["SecurityHeaders:EnableCrossOriginResourcePolicy"] = "true",
            ["SecurityHeadersMiddleware:AnalyzeSecurityHeaders"] = "false",
            ["SecurityHeadersMiddleware:LogToAuditSystem"] = "false",
            ["SecurityHeaders:PrivateKey"] = Canary,
            ["SecurityHeaders:ConnectionString"] = $"Server={Canary}"
        });

        builder.Services.AddNoelia(
            builder.Configuration,
            builder.Environment,
            "security-headers-probe",
            noelia => noelia.Use(NoeliaModule.SecurityHeaders));

        var app = builder.Build();
        app.UseNoelia(builder.Environment, "security-headers-probe", pipeline =>
            pipeline.UseSecurityHeaders());
        app.MapGet("/page", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/probe", () => Results.Ok(new { status = "ok" }));
        await app.StartAsync();
        return app;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.Single()
            : throw new InvalidOperationException($"Response has no {name} header.");

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        internal IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
