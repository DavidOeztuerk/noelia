using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Security.Audit;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Monitoring;
using Serilog;
using AuditMiddleware = Noelia.Infrastructure.Security.Audit.SecurityAuditMiddleware;
using RootAuditMiddleware = Noelia.Infrastructure.Security.SecurityAuditMiddleware;
using RootAuditEvent = Noelia.Abstractions.Security.Audit.SecurityAuditEvent;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// ADR-0007 (#59), decision 3, audit and log: the user id a protocol, an alert or a log line
/// carries comes from the first signed-in person identity, so an identity nobody signed in
/// cannot put a foreign id into the audit.
/// </summary>
[Trait("Category", "Unit")]
public class AuditAuthenticatedIdentityTests
{
    private static ClaimsIdentity Person(string id) =>
        new([new Claim(ClaimTypes.NameIdentifier, id), new Claim("sub", id), new Claim(ClaimTypes.Name, id)], "Bearer");

    private static ClaimsIdentity Stranger(string id) =>
        new([new Claim(ClaimTypes.NameIdentifier, id), new Claim("sub", id), new Claim(ClaimTypes.Name, id)]);

    private static ClaimsPrincipal Order(bool strangerFirst, ClaimsIdentity person, ClaimsIdentity stranger) =>
        strangerFirst ? new ClaimsPrincipal([stranger, person]) : new ClaimsPrincipal([person, stranger]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuditMiddleware_UserId_IsThatOfTheSignedInPerson(bool strangerFirst)
    {
        var service = Substitute.For<ISecurityAuditService>();
        SecurityAuditEvent? written = null;
        service.LogSecurityEventAsync(Arg.Do<SecurityAuditEvent>(e => written = e), Arg.Any<CancellationToken>())
            .Returns("id");
        var context = new DefaultHttpContext { User = Order(strangerFirst, Person("alice"), Stranger("mallory")) };
        context.Request.Method = "POST";
        context.Request.Path = "/api/users";

        await new AuditMiddleware(_ => Task.CompletedTask, service, NullLogger<AuditMiddleware>.Instance)
            .InvokeAsync(context);

        written!.UserId.Should().Be("alice");
    }

    [Fact]
    public async Task AuditMiddleware_OnlyAnUnsignedIdentity_HasNoUserId()
    {
        var service = Substitute.For<ISecurityAuditService>();
        SecurityAuditEvent? written = null;
        service.LogSecurityEventAsync(Arg.Do<SecurityAuditEvent>(e => written = e), Arg.Any<CancellationToken>())
            .Returns("id");
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(Stranger("mallory")) };
        context.Request.Method = "POST";
        context.Request.Path = "/api/users";

        await new AuditMiddleware(_ => Task.CompletedTask, service, NullLogger<AuditMiddleware>.Instance)
            .InvokeAsync(context);

        written!.UserId.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RootAuditMiddleware_UserId_IsThatOfTheSignedInPerson(bool strangerFirst)
    {
        var logger = Substitute.For<ISecurityAuditLogger>();
        var written = new List<RootAuditEvent>();
        logger.LogSecurityEventAsync(Arg.Do<RootAuditEvent>(written.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new DefaultHttpContext { User = Order(strangerFirst, Person("alice"), Stranger("mallory")) };
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/login";

        await new RootAuditMiddleware(_ => Task.CompletedTask, logger, NullLogger<RootAuditMiddleware>.Instance)
            .InvokeAsync(context);

        written.Should().NotBeEmpty();
        written.Should().OnlyContain(e => e.UserId == "alice");
    }

    [Fact]
    public async Task RootAuditMiddleware_OnlyAnUnsignedIdentity_HasNoUserId()
    {
        var logger = Substitute.For<ISecurityAuditLogger>();
        var written = new List<RootAuditEvent>();
        logger.LogSecurityEventAsync(Arg.Do<RootAuditEvent>(written.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(Stranger("mallory")) };
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/login";

        await new RootAuditMiddleware(_ => Task.CompletedTask, logger, NullLogger<RootAuditMiddleware>.Instance)
            .InvokeAsync(context);

        written.Should().NotBeEmpty();
        written.Should().OnlyContain(e => e.UserId == null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlertService_UserId_IsThatOfTheSignedInPerson(bool strangerFirst)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(
            new DefaultHttpContext { User = Order(strangerFirst, Person("alice"), Stranger("mallory")) });
        var service = new SecurityAlertService(
            Substitute.For<IDistributedCache>(),
            Substitute.For<ILogger<SecurityAlertService>>(),
            Options.Create(new SecurityAlertConfiguration { Enabled = false }),
            accessor);

        var alert = await service.SendAlertAsync(
            SecurityAlertLevel.Low, SecurityAlertType.UnauthorizedAccessAttempt, "title", "message");

        alert.UserId.Should().Be("alice");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RequestLog_UserId_IsThatOfTheSignedInPerson(bool strangerFirst)
    {
        var diagnostics = Substitute.For<IDiagnosticContext>();
        var context = new DefaultHttpContext { User = Order(strangerFirst, Person("alice"), Stranger("mallory")) };

        MiddlewarePipelineModule.EnrichRequestLog(diagnostics, context);

        diagnostics.Received().Set("UserId", "alice", Arg.Any<bool>());
        diagnostics.DidNotReceive().Set("UserId", "mallory", Arg.Any<bool>());
    }

    [Fact]
    public void RequestLog_OnlyAnUnsignedIdentity_SetsNoUserId()
    {
        var diagnostics = Substitute.For<IDiagnosticContext>();
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(Stranger("mallory")) };

        MiddlewarePipelineModule.EnrichRequestLog(diagnostics, context);

        diagnostics.DidNotReceive().Set("UserId", Arg.Any<object?>(), Arg.Any<bool>());
    }
}
