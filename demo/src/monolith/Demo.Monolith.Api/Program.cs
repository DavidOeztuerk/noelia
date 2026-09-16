using Demo.Platform;
using Demo.Monolith.Api;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Application.Extensions;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Http;
using Noelia.Dashboard;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TodoService.Application;
using TodoService.Contracts;
using TodoService.Infrastructure;
using UserService.Application;
using UserService.Contracts;
using UserService.Domain;
using UserService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
const string serviceName = "todo-monolith";

// Secrets arrive as files, not as environment entries. `docker inspect` and
// /proc/<pid>/environ show the environment of a container to anyone on the
// host; /run/secrets is a tmpfs the container reads and nothing else does.
// KeyPerFile maps Jwt__PrivateKey to Jwt:PrivateKey, so nothing downstream
// knows the difference.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

var keyId = builder.Configuration["Jwt:KeyId"]
    ?? throw new InvalidOperationException("Jwt:KeyId is not configured.");

// A monolith owns both sides of authentication: it issues and verifies in the
// same process. No gateway and no second service are required for either role.

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

// Register both feature assemblies in one mediator pipeline. Calling each
// feature's AddCQRS wrapper separately would register the pipeline twice.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddCQRS(
    typeof(UserApplicationServiceCollectionExtensions).Assembly,
    typeof(TodoApplicationServiceCollectionExtensions).Assembly);
builder.Services
    .AddUserInfrastructure(builder.Configuration["Database:Path"] ?? "todo-monolith.db")
    .AddTodoInfrastructure();

var app = builder.Build();

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
        ? Results.Problem(title: "E-Mail oder Passwort stimmt nicht.", statusCode: StatusCodes.Status401Unauthorized)
        : await StartSessionAsync(user, http, sessions, accessTokens, cancellationToken);
});

auth.MapPost("/refresh", async (
    HttpContext http,
    ITokenSessionService sessions,
    IUserRepository users,
    IAccessTokenIssuer accessTokens,
    CancellationToken cancellationToken) =>
{
    if (http.Request.Cookies[MonolithRefreshCookie.Name] is not { } presented)
    {
        return Results.Unauthorized();
    }

    var refreshed = await sessions.RefreshAsync(presented, cancellationToken);
    if (!refreshed.Succeeded)
    {
        MonolithRefreshCookie.Clear(http);
        return Results.Unauthorized();
    }

    var subject = await users.FindAsync(refreshed.Subject!.Value.Value, cancellationToken);
    if (subject is null)
    {
        MonolithRefreshCookie.Clear(http);
        return Results.Unauthorized();
    }

    MonolithRefreshCookie.Set(http, refreshed.RefreshToken!, refreshed.ExpiresAt!.Value);
    var access = await accessTokens.IssueAsync(subject, refreshed.Session!.Value, cancellationToken);

    return Results.Ok(new SessionResponse(
        subject.Id.Value, subject.DisplayName, access.Value, access.ExpiresAt));
});

auth.MapPost("/sign-out", async (
    HttpContext http,
    ITokenSessionService sessions,
    CancellationToken cancellationToken) =>
{
    if (SessionId.TryParse(http.User.FindFirst("session_id")?.Value, out var session))
    {
        await sessions.SignOutAsync(session, cancellationToken);
    }

    MonolithRefreshCookie.Clear(http);
    return Results.NoContent();
}).RequireAuthorization();

auth.MapPost("/sign-out-everywhere", async (
    HttpContext http,
    ICurrentPrincipal principal,
    ITokenSessionService sessions,
    CancellationToken cancellationToken) =>
{
    await sessions.SignOutEverywhereAsync(principal.Require().Subject, cancellationToken);
    MonolithRefreshCookie.Clear(http);
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

app.MapGet("/api/users/me/sessions", async (
    ICurrentPrincipal principal,
    ITokenSessionService sessions,
    CancellationToken cancellationToken) =>
    Results.Ok((await sessions.ActiveSessionsAsync(principal.Require().Subject, cancellationToken))
        .Select(s => new SessionSummaryResponse(s.Session.Value, s.StartedAt, s.LastUsedAt, s.ExpiresAt))))
    .RequireAuthorization();

var todos = app.MapGroup("/api/todos").RequireAuthorization();

todos.MapGet("", async (ICurrentPrincipal principal, IMediator mediator, CancellationToken cancellationToken) =>
    Results.Ok(await mediator.Send(new ListTodosQuery(principal.Require().Subject), cancellationToken)));

todos.MapPost("", async (
    CreateTodoRequest request,
    ICurrentPrincipal principal,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["title"] = ["Gib der Aufgabe einen Titel."]
        });
    }

    var todo = await mediator.Send(
        new CreateTodoCommand(principal.Require().Subject, request.Title), cancellationToken);
    return Results.Created($"/api/todos/{todo.Id}", todo);
});

todos.MapPatch("/{todoId:guid}/complete", async (
    Guid todoId,
    ICurrentPrincipal principal,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var todo = await mediator.Send(
        new CompleteTodoCommand(principal.Require().Subject, todoId), cancellationToken);
    return todo is null ? Results.NotFound() : Results.Ok(todo);
});

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
    MonolithRefreshCookie.Set(http, signIn.RefreshToken, signIn.ExpiresAt);
    var access = await accessTokens.IssueAsync(user, signIn.Session, cancellationToken);
    return Results.Ok(new SessionResponse(user.Id.Value, user.DisplayName, access.Value, access.ExpiresAt));
}
