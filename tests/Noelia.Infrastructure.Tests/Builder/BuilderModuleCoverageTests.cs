using Noelia.Abstractions.Caching;
using Noelia.Infrastructure.Security.InputSanitization;
using Noelia.InMemory.Security;
using Noelia.Abstractions.Security.Audit;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Audit;
using Noelia.Infrastructure.Security.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Metrics;
using AuditSecurityAuditMiddleware = Noelia.Infrastructure.Security.Audit.SecurityAuditMiddleware;
using RootSecurityAuditMiddleware = Noelia.Infrastructure.Security.SecurityAuditMiddleware;
using RootSecurityAuditEvent = Noelia.Abstractions.Security.Audit.SecurityAuditEvent;

namespace Noelia.Infrastructure.Tests.Builder;

[Trait("Category", "Unit")]
public class BuilderModuleCoverageTests
{
    private readonly IServiceCollection _services = new ServiceCollection();
    private readonly IHostEnvironment _environment = Substitute.For<IHostEnvironment>();

    public BuilderModuleCoverageTests()
    {
        _environment.EnvironmentName.Returns("Development");
    }

    #region JwtModule

    [Fact]
    public void AddJwtAuthentication_SetsJwtEnabledFlag()
    {
        var configData = new Dictionary<string, string?>
        {
            ["JwtSettings:Secret"] = "TestSecret-AtLeast32Characters-Long-Enough!",
            ["JwtSettings:Issuer"] = "test-issuer",
            ["JwtSettings:Audience"] = "test-audience"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var builder = new InfrastructureBuilder(_services, config, _environment, "TestService");

        var result = builder.AddJwtAuthentication();

        result.Should().BeSameAs(builder);
        builder.JwtEnabled.Should().BeTrue();
    }

    [Fact]
    public void AddJwtAuthentication_RegistersAuthenticationServices()
    {
        var configData = new Dictionary<string, string?>
        {
            ["JwtSettings:Secret"] = "TestSecret-AtLeast32Characters-Long-Enough!",
            ["JwtSettings:Issuer"] = "test-issuer",
            ["JwtSettings:Audience"] = "test-audience"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var builder = new InfrastructureBuilder(_services, config, _environment, "TestService");

        builder.AddJwtAuthentication();

        // Authentication services should be registered
        _services.Should().Contain(sd => sd.ServiceType.FullName!.Contains("IAuthenticationService")
            || sd.ServiceType.FullName!.Contains("IAuthentication"));
    }

    #endregion

    #region MiddlewarePipelineModule

    [Fact]
    public void UseSecurityHeaders_RegistersMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, app) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseSecurityHeaders();

        result.Should().BeSameAs(mwBuilder);
        // UseMiddleware internally calls IApplicationBuilder.Use()
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseCorrelationId_RegistersMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, app) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseCorrelationId();

        result.Should().BeSameAs(mwBuilder);
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseRequestLogging_RegistersMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, app) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseRequestLogging();

        result.Should().BeSameAs(mwBuilder);
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseExceptionHandling_RegistersMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, app) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseExceptionHandling();

        result.Should().BeSameAs(mwBuilder);
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseInputSanitization_RegistersMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, app) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseInputSanitization();

        result.Should().BeSameAs(mwBuilder);
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseCors_RegistersMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, _) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseCors();

        result.Should().BeSameAs(mwBuilder);
    }

    [Fact]
    public void UseAuth_RegistersAuthMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, _) = CreateMiddlewareBuilderWithServices(services =>
        {
            services.AddAuthorization();

            // UseAuth() adds UseAuthentication(), which needs a scheme. It now
            // says so by name instead of letting the framework report an
            // unresolvable IAuthenticationSchemeProvider.
            services.AddAuthentication();
        });

        var result = mwBuilder.UseAuth();

        result.Should().BeSameAs(mwBuilder);
    }

    [Fact]
    public void UseSecurityAudit_RegistersMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, app) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseSecurityAudit();

        result.Should().BeSameAs(mwBuilder);
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseSwagger_InDevelopment_ReturnsBuilder()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddSwaggerGen();

        // UseSwaggerUI resolves IWebHostEnvironment from the container.
        var webEnv = Substitute.For<IWebHostEnvironment>();
        webEnv.EnvironmentName.Returns("Development");
        webEnv.ApplicationName.Returns("TestService");
        webEnv.ContentRootPath.Returns(AppContext.BaseDirectory);
        webEnv.WebRootPath.Returns(AppContext.BaseDirectory);
        webEnv.ContentRootFileProvider.Returns(new NullFileProvider());
        webEnv.WebRootFileProvider.Returns(new NullFileProvider());
        services.AddSingleton(webEnv);

        var serviceProvider = services.BuildServiceProvider();
        var app = Substitute.For<IApplicationBuilder>();
        app.ApplicationServices.Returns(serviceProvider);
        var mwBuilder = new InfrastructureMiddlewareBuilder(app, env, "TestService");

        var result = mwBuilder.UseSwagger();

        result.Should().BeSameAs(mwBuilder);
    }

    [Fact]
    public void UseSwagger_InProduction_SkipsSwagger_ReturnsBuilder()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var app = Substitute.For<IApplicationBuilder>();
        var mwBuilder = new InfrastructureMiddlewareBuilder(app, env, "TestService");

        var result = mwBuilder.UseSwagger();

        result.Should().BeSameAs(mwBuilder);
        // In production, no Use() should be called for swagger
        app.DidNotReceive().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseRateLimiting_RegistersMiddleware_ReturnsBuilder()
    {
        var (mwBuilder, app) = CreateMiddlewareBuilderWithServices(services =>
        {
            services.AddLogging();
            services.AddSingleton(Substitute.For<IDistributedRateLimitStore>());
        });

        var result = mwBuilder.UseRateLimiting();

        result.Should().BeSameAs(mwBuilder);
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseHealthCheckEndpoints_ReturnsBuilder()
    {
        var (mwBuilder, _) = CreateMiddlewareBuilderWithServices(services =>
        {
            services.AddLogging();
            services.AddOptions();
            services.AddHealthChecks();
        });

        var result = mwBuilder.UseHealthCheckEndpoints();

        result.Should().BeSameAs(mwBuilder);
    }

    [Fact]
    public void UsePermissions_ReturnsBuilder()
    {
        var (mwBuilder, _) = CreateMiddlewareBuilder();

        var result = mwBuilder.UsePermissions();

        result.Should().BeSameAs(mwBuilder);
    }

    [Fact]
    public void UseHttpCaching_ReturnsBuilder()
    {
        var (mwBuilder, _) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseHttpCaching();

        result.Should().BeSameAs(mwBuilder);
    }

    [Fact]
    public void UseSerilogLogging_ReturnsBuilder()
    {
        var (mwBuilder, _) = CreateMiddlewareBuilderWithServices(services =>
        {
            services.AddLogging();
        });

        var result = mwBuilder.UseSerilogLogging();

        result.Should().BeSameAs(mwBuilder);
    }

    [Fact]
    public void UseTelemetry_ReturnsBuilder()
    {
        var (mwBuilder, _) = CreateMiddlewareBuilder();

        var result = mwBuilder.UseTelemetry();

        result.Should().BeSameAs(mwBuilder);
    }

    [Fact]
    public void FluentChaining_MiddlewarePipeline_Works()
    {
        var (mwBuilder, _) = CreateMiddlewareBuilderWithServices(services =>
        {
            services.AddAuthorization();
            services.AddAuthentication();
            services.AddSingleton(Substitute.For<ISecurityHeadersService>());
            services.AddSingleton(Substitute.For<IInputSanitizer>());
            services.AddSingleton(Substitute.For<ISecurityAuditLogger>());
        });

        var result = mwBuilder
            .UseSecurityHeaders()
            .UseCorrelationId()
            .UseRequestLogging()
            .UseExceptionHandling()
            .UseInputSanitization()
            .UseCors()
            .UseAuth()
            .UseSecurityAudit();

        result.Should().BeSameAs(mwBuilder);
    }

    #endregion

    #region SecurityAuditMiddleware (Noelia.Infrastructure.Security namespace)

    [Fact]
    public async Task RootSecurityAuditMiddleware_InvokeAsync_CallsNext()
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var auditLogger = Substitute.For<ISecurityAuditLogger>();
        var logger = NullLogger<RootSecurityAuditMiddleware>.Instance;
        var middleware = new RootSecurityAuditMiddleware(next, auditLogger, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/data";
        context.Request.Method = "GET";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task RootSecurityAuditMiddleware_AuthPath_AuditsRequestStartedAndCompleted()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var auditLogger = Substitute.For<ISecurityAuditLogger>();
        var logger = NullLogger<RootSecurityAuditMiddleware>.Instance;
        var middleware = new RootSecurityAuditMiddleware(next, auditLogger, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/auth/login";
        context.Request.Method = "POST";

        await middleware.InvokeAsync(context);

        await auditLogger.Received(2).LogSecurityEventAsync(
            Arg.Any<RootSecurityAuditEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RootSecurityAuditMiddleware_AdminPath_AuditsEvents()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var auditLogger = Substitute.For<ISecurityAuditLogger>();
        var logger = NullLogger<RootSecurityAuditMiddleware>.Instance;
        var middleware = new RootSecurityAuditMiddleware(next, auditLogger, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/admin/users";
        context.Request.Method = "GET";

        await middleware.InvokeAsync(context);

        await auditLogger.Received(2).LogSecurityEventAsync(
            Arg.Any<RootSecurityAuditEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RootSecurityAuditMiddleware_NonSensitivePath_SkipsAudit()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var auditLogger = Substitute.For<ISecurityAuditLogger>();
        var logger = NullLogger<RootSecurityAuditMiddleware>.Instance;
        var middleware = new RootSecurityAuditMiddleware(next, auditLogger, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/jobs";
        context.Request.Method = "GET";
        context.Response.StatusCode = 200;

        await middleware.InvokeAsync(context);

        await auditLogger.DidNotReceive().LogSecurityEventAsync(
            Arg.Any<RootSecurityAuditEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RootSecurityAuditMiddleware_AuditLoggerThrows_DoesNotBubble()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var auditLogger = Substitute.For<ISecurityAuditLogger>();
        auditLogger.LogSecurityEventAsync(Arg.Any<RootSecurityAuditEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Audit failure"));
        var logger = NullLogger<RootSecurityAuditMiddleware>.Instance;
        var middleware = new RootSecurityAuditMiddleware(next, auditLogger, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/auth/token";
        context.Request.Method = "POST";

        // Should not throw even though audit logger throws
        var act = () => middleware.InvokeAsync(context);

        await act.Should().NotThrowAsync();
    }

    #endregion


    #region SecurityHeadersExtensions (DI Registration)

    [Fact]
    public void AddSecurityHeaders_WithConfiguration_RegistersService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        _services.AddSecurityHeaders(config);

        _services.Should().Contain(sd =>
            sd.ServiceType == typeof(ISecurityHeadersService));
    }

    [Fact]
    public void AddSecurityHeaders_WithActionOverload_RegistersService()
    {
        _services.AddSecurityHeaders(opts => { opts.EnableHsts = true; });

        _services.Should().Contain(sd =>
            sd.ServiceType == typeof(ISecurityHeadersService));
    }

    [Fact]
    public void AddSecurityHeaders_WithBuilderOverload_RegistersService()
    {
        _services.AddSecurityHeaders(builder =>
        {
            builder.ForDevelopment();
        });

        _services.Should().Contain(sd =>
            sd.ServiceType == typeof(ISecurityHeadersService));
    }

    #endregion

    #region SecurityHeadersMiddlewareExtensions (IApplicationBuilder)

    [Fact]
    public void UseSecurityHeaders_Extension_OnIApplicationBuilder_CallsUse()
    {
        var app = Substitute.For<IApplicationBuilder>();

        var result = SecurityHeadersMiddlewareExtensions.UseSecurityHeaders(app);

        result.Should().NotBeNull();
        // UseMiddleware<T> internally calls IApplicationBuilder.Use()
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    [Fact]
    public void UseSecurityHeaders_WithOptions_OnIApplicationBuilder_CallsUse()
    {
        var app = Substitute.For<IApplicationBuilder>();

        var result = SecurityHeadersMiddlewareExtensions.UseSecurityHeaders(app, opts =>
        {
            opts.EnableSecurityHeaders = true;
            opts.LogSecurityHeaders = true;
        });

        result.Should().NotBeNull();
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    #endregion

    #region SecurityAuditExtensions (DI Registration)

    [Fact]
    public void AddSecurityAudit_RegistersAuditMiddleware()
    {
        _services.AddLogging();
        _services.AddSecurityAudit();

        _services.Should().Contain(sd =>
            sd.ServiceType == typeof(AuditSecurityAuditMiddleware));
    }

    [Fact]
    public void AddSecurityAuditMiddleware_RegistersTransient()
    {
        _services.AddSecurityAuditMiddleware();

        _services.Should().Contain(sd =>
            sd.ServiceType == typeof(AuditSecurityAuditMiddleware)
            && sd.Lifetime == ServiceLifetime.Transient);
    }

    #endregion

    #region SecurityAuditMiddlewareExtensions (IApplicationBuilder)

    [Fact]
    public void UseSecurityAudit_Extension_OnIApplicationBuilder_CallsUse()
    {
        var app = Substitute.For<IApplicationBuilder>();

        var result = SecurityAuditMiddlewareExtensions.UseSecurityAudit(app);

        result.Should().NotBeNull();
        app.Received().Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>());
    }

    #endregion

    #region Helpers

    private (InfrastructureMiddlewareBuilder mwBuilder, IApplicationBuilder app) CreateMiddlewareBuilder()
    {
        var app = Substitute.For<IApplicationBuilder>();
        var mwBuilder = new InfrastructureMiddlewareBuilder(app, _environment, "TestService");
        return (mwBuilder, app);
    }

    private (InfrastructureMiddlewareBuilder mwBuilder, IApplicationBuilder app) CreateMiddlewareBuilderWithServices(
        Action<IServiceCollection> configureServices)
    {
        var services = new ServiceCollection();
        configureServices(services);
        var serviceProvider = services.BuildServiceProvider();

        var app = Substitute.For<IApplicationBuilder>();
        app.ApplicationServices.Returns(serviceProvider);
        var mwBuilder = new InfrastructureMiddlewareBuilder(app, _environment, "TestService");
        return (mwBuilder, app);
    }

    #endregion
}
