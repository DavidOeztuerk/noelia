using Demo.Platform;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Http;
using Noelia.Infrastructure.Middleware;
using Noelia.Dashboard;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;

var builder = WebApplication.CreateBuilder(args);
const string serviceName = "gateway";

// Secrets arrive as files, not as environment entries. `docker inspect` and
// /proc/<pid>/environ show the environment of a container to anyone on the
// host; /run/secrets is a tmpfs the container reads and nothing else does.
// KeyPerFile maps Jwt__PrivateKey to Jwt:PrivateKey, so nothing downstream
// knows the difference.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

builder.Configuration.AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);

// The gateway routes; it never reads a token and never builds a principal.
// Each service verifies for itself, so a gateway that waved requests through
// could not be mistaken for authorisation.

// What this stage runs and who may look at it. One file per stage; see
// Demo.Platform.DemoEnvironment.
var demo = DemoEnvironment.Read(builder.Configuration);

// TLS ends at the edge. Without this the application sees http on every
// request and the address of the proxy instead of the caller.
builder.Services.TrustForwardedHeadersFrom(demo.TrustedProxies);

builder.Services.AddNoelia(
    builder.Configuration, builder.Environment, serviceName, noelia => noelia
        .UseDefaults()
        .Without(NoeliaModule.Jwt, "routes API traffic; never reads a token")
        .UseDemoProviders(demo, serviceName)
        .UseDemoDashboard(demo, builder.Environment, "GateCanary"));

// Ocelot builds its own outgoing HTTP pipeline, so Noelia's correlation
// handler is wired in explicitly — otherwise the gateway's generated
// correlation id would stop at the gateway. The `true` makes it global:
// without it the handler would only apply to routes that list it in
// ocelot.json's "DelegatingHandlers" array.
// The gateway is ready when it can route, and routing needs nothing of its
// own. Gating its readiness on the services behind it would take the gateway
// out of rotation for a fault it does not have and cannot fix — one slow
// service would remove the entrance for all of them. Each service answers for
// itself on its own /health/ready.
builder.Services.AddHealthChecks()
    .AddCheck(
        "routing",
        () => HealthCheckResult.Healthy("routes only; downstream readiness is reported downstream"),
        tags: ["ready"]);

builder.Services
    .AddOcelot(builder.Configuration)
    .AddDelegatingHandler<CorrelationIdHandler>(true)
    // The gateway is a proxy, so it forwards what a proxy forwards. Without
    // this the chain ends here: TLS terminates at the edge, the gateway learns
    // the request was https, and the service behind it sees plain http — and
    // issues a refresh cookie without Secure because it honestly believes the
    // transport was insecure. The monolith got it right only because nothing
    // sits between it and the edge.
    .AddDelegatingHandler<ForwardedOriginHandler>(true);

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<ForwardedOriginHandler>();

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
    .UseRateLimiting());

app.Logger.LogInformation("{ServiceName} is ready and routing API traffic", serviceName);
await app.UseOcelot();
await app.RunAsync();
