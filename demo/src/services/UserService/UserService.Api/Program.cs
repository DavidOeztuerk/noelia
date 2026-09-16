using Demo.Platform;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Http;
using Noelia.Dashboard;
using MediatR;
using Microsoft.EntityFrameworkCore;
using UserService.Api;
using UserService.Application;
using UserService.Contracts;
using UserService.Domain;
using UserService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
const string serviceName = "user-service";

// Secrets arrive as files, not as environment entries. `docker inspect` and
// /proc/<pid>/environ show the environment of a container to anyone on the
// host; /run/secrets is a tmpfs the container reads and nothing else does.
// KeyPerFile maps Jwt__PrivateKey to Jwt:PrivateKey, so nothing downstream
// knows the difference.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

// The only service holding the private half. Every other service gets the
// public one and is then unable to mint a token, whatever happens to it.
var keyId = builder.Configuration["Jwt:KeyId"]
    ?? throw new InvalidOperationException("Jwt:KeyId is not configured.");


// What this stage runs and who may look at it. One file per stage; see
// Demo.Platform.DemoEnvironment.
var demo = DemoEnvironment.Read(builder.Configuration);

// TLS ends at the edge. Without this the application sees http on every
// request and the address of the proxy instead of the caller.
builder.Services.TrustForwardedHeadersFrom(demo.TrustedProxies);

builder.Services.AddNoelia(
    builder.Configuration, builder.Environment, serviceName, noelia => noelia
        .UseDefaults()
        .UseJwt(jwt => jwt
            .Issue(
                builder.Configuration["Jwt:PrivateKey"]
                ?? throw new InvalidOperationException("Jwt:PrivateKey is not configured."),
                keyId)
            .AlsoVerify(
                builder.Configuration["Jwt:PublicKey"]
                ?? throw new InvalidOperationException("Jwt:PublicKey is not configured."),
                keyId))
        .Use(NoeliaModule.Principal)
        .Use(NoeliaModule.PasswordHashing)
        .Use(NoeliaModule.TokenSessions)
        .UseDemoProviders(demo, serviceName, readsTokens: true)
        .UseDemoDashboard(demo, builder.Environment, "GateCanary"));

builder.Services
    .AddUserApplication()
    .AddUserInfrastructure(builder.Configuration["Database:Path"] ?? "user-service.db");

var app = builder.Build();

// One file, created on first run. A real service would migrate instead.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<DemoDbContext>().Database.EnsureCreated();
}

app.UseNoelia(builder.Environment, serviceName, pipeline => pipeline
    .UseForwardedHeaders()
    .UseExceptionHandling()
    .UseCorrelationId()
    .UseSecurityHeaders()
    // Development only, and the module decides that — not this line.
    // /swagger answered 404 everywhere because the module was composed
    // and its pipeline step was never added, which is the composition
    // saying one thing and the running service another.
    .UseSwagger()
    // Health checks before the brake, and the brake outside authentication.
    // Both orders carry weight: a braked liveness probe takes the container out
    // of the load balancer, which makes the limiter itself the outage; and a
    // limiter inside authentication counts only the callers who already got
    // through, which is the opposite of what it is for.
    .UseHealthCheckEndpoints()
    .UseRateLimiting()
    .UseAuth()
    .UsePrincipal()
    // After the principal: an audit entry that cannot name who acted is a
    // record of a request, not of a decision.
    .UseSecurityAudit());

var auth = app.MapGroup("/api/auth");

auth.MapPost("/register", async (
    RegisterRequest request,
    HttpContext http,
    IMediator mediator,
    ITokenSessionService sessions,
    IAccessTokenIssuer accessTokens,
    CancellationToken cancellationToken) =>
{
    var result = await mediator.Send(
        new RegisterUserCommand(request.DisplayName, request.Email, request.Password), cancellationToken);

    return result switch
    {
        RegistrationResult.Registered registered =>
            await StartSessionAsync(registered.User, http, sessions, accessTokens, cancellationToken),
        RegistrationResult.Rejected rejected =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [rejected.Field] = [rejected.Message] }),
        // 409 rather than a field error: the address is not malformed, it is
        // in use, and the person's next step is to sign in instead.
        RegistrationResult.EmailTaken => Results.Problem(
            title: "Diese Adresse ist bereits vergeben.",
            detail: "Melde dich stattdessen an.",
            statusCode: StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException($"Unhandled result {result.GetType().Name}")
    };
});

auth.MapPost("/login", async (
    SignInRequest request,
    HttpContext http,
    IMediator mediator,
    ITokenSessionService sessions,
    IAccessTokenIssuer accessTokens,
    CancellationToken cancellationToken) =>
{
    var user = await mediator.Send(new SignInCommand(request.Email, request.Password), cancellationToken);

    return user is null
        ? Results.Problem(
            title: "E-Mail oder Passwort stimmt nicht.",
            statusCode: StatusCodes.Status401Unauthorized)
        : await StartSessionAsync(user, http, sessions, accessTokens, cancellationToken);
});

// The access token is short-lived and never checked against a list; this is
// what keeps a person signed in, and the only place the refresh token is read.
auth.MapPost("/refresh", async (
    HttpContext http,
    ITokenSessionService sessions,
    IUserRepository users,
    IAccessTokenIssuer accessTokens,
    CancellationToken cancellationToken) =>
{
    if (http.Request.Cookies[RefreshCookie.Name] is not { } presented)
    {
        return Results.Unauthorized();
    }

    var refreshed = await sessions.RefreshAsync(presented, cancellationToken);
    if (!refreshed.Succeeded)
    {
        RefreshCookie.Clear(http);
        return Results.Unauthorized();
    }

    // Whose session it is comes from the stored record, never from anything
    // the caller sent — the presented token is the only thing they proved.
    var subject = await users.FindAsync(refreshed.Subject!.Value.Value, cancellationToken);
    if (subject is null)
    {
        RefreshCookie.Clear(http);
        return Results.Unauthorized();
    }

    RefreshCookie.Set(http, refreshed.RefreshToken!, refreshed.ExpiresAt!.Value);
    var access = await accessTokens.IssueAsync(subject, refreshed.Session!.Value, cancellationToken);

    return Results.Ok(new SessionResponse(
        subject.Id.Value, subject.DisplayName, access.Value, access.ExpiresAt));
});

// Signing out ends the session in this service's own database. Nothing else
// has to be running for that to take effect.
auth.MapPost("/sign-out", async (
    HttpContext http,
    ICurrentPrincipal principal,
    ITokenSessionService sessions,
    CancellationToken cancellationToken) =>
{
    if (SessionId.TryParse(http.User.FindFirst("session_id")?.Value, out var session))
    {
        await sessions.SignOutAsync(session, cancellationToken);
    }

    RefreshCookie.Clear(http);
    return Results.NoContent();
}).RequireAuthorization();

auth.MapPost("/sign-out-everywhere", async (
    HttpContext http,
    ICurrentPrincipal principal,
    ITokenSessionService sessions,
    CancellationToken cancellationToken) =>
{
    await sessions.SignOutEverywhereAsync(principal.Require().Subject, cancellationToken);
    RefreshCookie.Clear(http);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/users/me", async (
    ICurrentPrincipal principal,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    var user = await users.FindAsync(principal.Require().Subject.Value, cancellationToken);

    return user is null
        ? Results.NotFound()
        : Results.Ok(new UserResponse(user.Id.Value, user.DisplayName, user.Email.Value, user.CreatedAt));
}).RequireAuthorization();

// A person's own sessions, so they can see and end them. No record of what
// anyone did — only that a sign-in exists.
app.MapGet("/api/users/me/sessions", async (
    ICurrentPrincipal principal,
    ITokenSessionService sessions,
    CancellationToken cancellationToken) =>
    Results.Ok((await sessions.ActiveSessionsAsync(principal.Require().Subject, cancellationToken))
        .Select(s => new SessionSummaryResponse(
            s.Session.Value, s.StartedAt, s.LastUsedAt, s.ExpiresAt))))
    .RequireAuthorization();

app.Logger.LogInformation("{ServiceName} is ready", serviceName);
app.Run();

static async Task<IResult> StartSessionAsync(
    User user,
    HttpContext http,
    ITokenSessionService sessions,
    IAccessTokenIssuer accessTokens,
    CancellationToken cancellationToken)
{
    var signIn = await sessions.SignInAsync(user.Id, cancellationToken: cancellationToken);
    RefreshCookie.Set(http, signIn.RefreshToken, signIn.ExpiresAt);

    var access = await accessTokens.IssueAsync(user, signIn.Session, cancellationToken);

    return Results.Ok(new SessionResponse(
        user.Id.Value, user.DisplayName, access.Value, access.ExpiresAt));
}
