using Demo.Platform;
using Noelia.Abstractions.Hosting;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Http;
using Noelia.Dashboard;
using MediatR;
using TodoService.Application;
using TodoService.Contracts;
using TodoService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
const string serviceName = "todo-service";

// Secrets arrive as files, not as environment entries. `docker inspect` and
// /proc/<pid>/environ show the environment of a container to anyone on the
// host; /run/secrets is a tmpfs the container reads and nothing else does.
// KeyPerFile maps Jwt__PrivateKey to Jwt:PrivateKey, so nothing downstream
// knows the difference.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

// This service verifies tokens and never issues them, which is why it needs
// the same JwtSettings as user-service and none of its issuing code.
// The public half only. This service verifies signatures and holds nothing it
// could sign with — so taking it over grants no ability to impersonate anyone.

// What this stage runs and who may look at it. One file per stage; see
// Demo.Platform.DemoEnvironment.
var demo = DemoEnvironment.Read(builder.Configuration);

// TLS ends at the edge. Without this the application sees http on every
// request and the address of the proxy instead of the caller.
builder.Services.TrustForwardedHeadersFrom(demo.TrustedProxies);

builder.Services.AddNoelia(
    builder.Configuration, builder.Environment, serviceName, noelia => noelia
        .UseDefaults()
        .UseJwt(jwt => jwt.VerifyOnly(
            builder.Configuration["Jwt:PublicKey"]
            ?? throw new InvalidOperationException("Jwt:PublicKey is not configured."),
            builder.Configuration["Jwt:KeyId"]
            ?? throw new InvalidOperationException("Jwt:KeyId is not configured.")))
        .Use(NoeliaModule.Principal)
        .UseDemoProviders(demo, serviceName, readsTokens: true)
        .UseDemoDashboard(demo, builder.Environment, "GateCanary"));

// Nothing shared. This service verifies a signature and holds no state that
// any other service has to agree with — which is what removed the extra
// container the demo used to need.

builder.Services
    .AddTodoApplication()
    .AddTodoInfrastructure();

var app = builder.Build();

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

// Every route here is about the caller's own todos, so the requirement sits on
// the group rather than being repeated and eventually forgotten on one route.
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
