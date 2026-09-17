# Noelia

A shared foundation for .NET microservices: a CQRS pipeline, security and
identity primitives, caching, messaging, health probes, resilience and
observability — built once, reused by every service.

Targets `net10.0`.

The Noelia identity starts with 5.0.0. The rename is deliberately complete:
package ids, namespaces, public API names, configuration prefixes, telemetry,
storage keys and cryptographic domain identifiers all move together. Existing
4.x data and configuration therefore require an explicit migration before a
5.0 process is started.

## Packages

Noelia is split so that a service takes only what it runs. The engine names no
driver: choosing where data lives is the operator's decision, and it is made in
the application's composition root.

| Package | Contents | Provider dependency | NuGet load¹ |
|---|---|---|---:|
| `Noelia.Core` | Entities, domain exceptions, identity primitives, compliance ports | none | 0 |
| `Noelia.Contracts` | Boundary DTOs: paging, contract versioning | none | 0 |
| `Noelia.Abstractions` | **Every port**, plus `INoeliaBuilder` | none | 8 |
| `Noelia.Application` | Mediator, pipeline behaviours, base handlers | none | 4 |
| `Noelia.Infrastructure` | Middleware, builder, telemetry, resilience, headers, input sanitisation, sessions, password hashing | none | 44 |
| `Noelia.Http` | Correlation, the rate limit, the client address — the pipeline without the engine | **none** (framework only) | 0 |
| `Noelia.Dashboard` | Read-only operator view, server-rendered HTML and embedded assets | **none** (framework only) | 0 |
| `Noelia.Redis` | Cache, rate counters, secrets, keys, audit trail, resource permissions, readiness | StackExchange.Redis | 15 |
| `Noelia.InMemory` | The same ports, in process | none | 10 |
| `Noelia.Passwords.BCrypt` | bcrypt, to write or to read what a system already has | BCrypt.Net-Next | 9 |
| `Noelia.Passwords.Argon2` | Argon2id, where custom hardware is part of the threat | Konscious | 10 |
| `Noelia.Messaging.MassTransit` | Event bus, correlation filters, broker health | MassTransit 8, RabbitMQ | 4 |
| `Noelia.Data.EntityFrameworkCore` | Id converters, tenant filters, readiness probe, exception mapping, **refresh token store** | EF Core (no database provider) | 19 |

¹ Direct and transitive NuGet packages in the restored `net10.0` graph. An
architecture test pins every number, including zero, so dependency growth is a
reviewed decision rather than an invisible side effect.

Dependencies point inward, as Clean Architecture requires. `Noelia.Core`,
`Noelia.Contracts` and `Noelia.Abstractions` are held to that by a build
target — adding an infrastructure package to any of them fails the build with
`NOELIA0001`.

The reasoning is in [ADR-0001](docs/adr/0001-souveraenitaet-durch-portschnitt.md);
what sovereignty means in practice is in [SOVEREIGNTY.md](SOVEREIGNTY.md).

## Versions, and what a major number promises

Noelia follows [SemVer](https://semver.org). All packages ship under one
version and move together — a service should never have to reason about which
combination of Noelia packages is compatible with which.

| | |
|---|---|
| **Patch** (5.0.**1**) | A fix. Nothing you call changes shape. |
| **Minor** (5.**1**.0) | Something was added. Existing code keeps compiling and keeps meaning what it meant. |
| **Major** (**6**.0.0) | Something you call changed or is gone. Every such change is named in the release notes, with what to do instead. |

**Be warned about the pace so far.** The predecessor code line went 3.0.1 →
4.4.0 in four weeks,
and those major steps were real: `PermissionEnforcement` was split out of
`Authorization`, input sanitisation was rewritten from word matching to syntax
detection, and `AddCommunication` changed what it requires. While there was one
consumer, a break cost an afternoon. That is exactly what changes when the
packages are public — so from 4.4.0 on:

- **A breaking change is announced in a minor release before it lands**, as an
  `[Obsolete]` attribute naming the replacement, and only then removed in the
  next major.
- **The previous major keeps receiving security fixes for six months** after
  its successor ships. Functional fixes go to the current major only.
- **Preview versions** (for example `6.0.0-preview.1`) remain CI artifacts or
  use an explicit local feed. Only stable releases go to nuget.org.

Security reports go through the process in [SECURITY.md](SECURITY.md), not
through public issues.

> **Security notice, 4.4.3.** This release fixes bypasses and unverifiable
> state in Redis rate limiting, `SecretManager`, security audit signing and
> PBKDF2 defaults. Existing SecretManager and Redis audit records need an
> explicit migration; the rate limiter now fails closed by default. Read
> [MIGRATION.md](MIGRATION.md) before upgrading.

> **Security notice, 4.4.2.** The 4.4.1 Redis predecessor encrypted the payload, but did
> not authenticate the surrounding envelope metadata. An attacker with write
> access to the stored envelope could change how authenticated bytes were
> interpreted while decryption still reported integrity. Read
> [Security notice — 4.4.2](#security-notice--442-encryption-envelope-metadata-was-not-authenticated)
> before upgrading a system that stored 4.4.1 envelopes.

> **Security notice, 4.4.1.** `AddEncryption()` did not encrypt in any version
> up to and including 4.4.0: the shipped `IDataEncryptionService` stored the
> plaintext and reported `AES256GCM`. If you ever called it, read
> [Security notice — 4.4.1](#security-notice--441-addencryption-did-not-encrypt)
> before anything else in this file.

## Seeing it run

`demo/` holds one todo application built twice — as microservices behind a
gateway, and as a monolith — and run three times, in Development, Staging and
Production, from a single `docker compose`. The application code is identical
in all six; what differs is where the state lives and who may open the operator
dashboard, and both are configuration rather than code.

```bash
cd demo
cp .env.example .env
docker compose --profile all up -d --build --wait
```

Then `http://mono-dev.localhost:8080` for the application and
`http://mono-dev.localhost:8080/noelia` for the dashboard. The details, and the
matrix of what each stage runs, are in [demo/README.md](demo/README.md).

The demo installs Noelia from nuget.org like anyone else would, and an
architecture test keeps it that way — a gate that builds against the working
tree it was cut from proves nothing about the packages.

## Getting started

```bash
dotnet restore Noelia.slnx
dotnet build   Noelia.slnx
dotnet test    Noelia.slnx
```

Package versions are managed centrally in `Directory.Packages.props`.

### A minimal service

Fifteen lines, and a service that runs:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNoelia(
    builder.Configuration, builder.Environment, "identity-service",
    noelia => noelia.UseDefaults());

var app = builder.Build();

app.UseNoelia(builder.Environment, "identity-service", pipeline => pipeline
    .UseExceptionHandling()
    .UseCorrelationId()
    .UseSecurityHeaders()
    .UseCors()
    .UseAuth()
    .UseHealthCheckEndpoints());

app.Run();
```

`UseDefaults()` is a call you can see and delete. Delete it and you get nothing —
the same shape as Entity Framework without a provider, and for the same reason:
what infrastructure a service runs is a decision that belongs to whoever operates
it, and a decision made silently is one nobody can review.

It registers **no** cache, no secret store, no audit sink and no message bus.
Those come from provider packages, so that adding Noelia never adds a server you
have to run.

### The modules

| Module | What it does | What it needs | In `UseDefaults()` |
|---|---|---|---|
| `Logging` | Serilog, configured from this service's configuration | — | ✅ |
| `HttpContextAccess` | the ambient `HttpContext` several modules read | — | ✅ |
| `JsonOptions` | camelCase in and out, indented while developing | — | ✅ |
| `Jwt` | `IJwtService`, `ITotpService`, error messages | — | ✅ |
| `SecurityMonitoring` | failed sign-ins, lockouts, alerts | `IDistributedCache` (from `Caching`) | ✅ |
| `Resilience` | circuit breakers and retries for outgoing calls | — | ✅ |
| `SecretManagement` | reading secrets through one canonical provider port | `ISecretProvider` | ❌ |
| `Audit` | the security audit trail | — | ✅ |
| `InputSanitization` | refusing requests that carry injection *syntax* | — | ✅ |
| `RateLimiting` | counting requests, refusing the ones over the line | — (brings an in-process counter; a provider replaces it) | ✅ |
| `HealthChecks` | liveness and readiness | — | ✅ |
| `Caching` | the two in-process caches other modules build on | — | ✅ |
| `Observability` | traces, metrics, the telemetry pipeline | — | ✅ |
| `SecurityHeaders` | the response headers a browser enforces | — | ✅ |
| `Authorization` | permission policies — what answers `[RequirePermission]` | — | ✅ |
| `PermissionEnforcement` | the pipeline step that refuses a request no permission covers | — (registers nothing) | ✅ |
| `CorrelationPropagation` | carries the correlation id out on every HTTP call | — | ✅ |
| `ApiDocumentation` | Swagger, in development only | — | ✅ |
| `Cors` | cross-origin rules from the configured origins | — | ✅ |
| `HttpResponseCaching` | ETags and conditional responses | `IDistributedCacheService` | ❌ |
| `Communication` | calling other services, with caching and deduplication | `IEventBus` | ❌ |
| `ResourceAuthorization` | resource and ownership policies | `IResourceAuthorizationService` | ❌ |
| `Encryption` | encrypting values at rest | a master key | ❌ |
| `PasswordHashing` | hashing and verifying passwords | a hashing provider | ❌ |
| `TokenSessions` | refresh tokens and sessions | a refresh token store | ❌ |
| `Principal` | the current principal, read from the request | — | ❌ |
| `SovereignPlatform` | a declared egress boundary, the sovereignty report, the audit trail | — | ❌ |
| `Dashboard` | read-only operator view of the actual composition and security posture | an explicit visibility policy | ❌ |

Provider packages participate in that same composition instead of remaining
invisible registrations:

| Package | Composition call | Declared effect or requirement |
|---|---|---|
| `Noelia.InMemory` | `UseInMemoryCache(prefix)` / `UseInMemoryRefreshTokens()` | cache and refresh-token providers |
| `Noelia.Http` | `UseInProcessRateLimits()` | in-process `IDistributedRateLimitStore` |
| `Noelia.Redis` | `UseRedisEncryption()` | requires Redis connection and master-key provider; provides AEAD encryption |
| `Noelia.Data.EntityFrameworkCore` | `UseEntityFrameworkRefreshTokens<TContext>()` | requires `TContext`; provides the refresh-token store |
| `Noelia.Passwords.Argon2` | `UseArgon2Passwords()` | Argon2id password provider |
| `Noelia.Passwords.BCrypt` | `UseBCryptPasswords()` | bcrypt password provider |
| `Noelia.Messaging.MassTransit` | `UseMassTransitMessaging(assemblies)` | event-bus provider |

The line between the two halves is one rule: **everything in `UseDefaults()`
starts with nothing else registered** — checked by building the container with
`ValidateOnBuild`, so a module that registers a consumer without its dependency
fails there rather than on the first request that needs it. Provider-backed
modules are not in it: they each need a decision Noelia must not make on anyone's
behalf — which cache, broker, secret store, key, hashing algorithm or state
store. Including them would produce a default set that refuses to start, which
is not a default.

The rule now holds for the **pipeline** too, and did not before. `RateLimiting`
was in the default set and registered no counter, so the default chain refused
to compose — `UseRateLimiting() needs IDistributedRateLimitStore` — and the
shortest documented way to stand a service up died at startup. It brings an
in-process counter now, the same shape as the framework's own
`AddDistributedMemoryCache()` that `Caching` already registers, and
`AddRedisCache(prefix)` or `AddInMemoryCache(prefix)` replaces it because the
last registration of a service is the one that wins. **A counter in one process
counts per replica**, so a shared store belongs in place before a second
instance runs.

`Authorization` and `PermissionEnforcement` are two modules because they are two
things. The first supplies the policy provider that answers
`[RequirePermission]` on the endpoints carrying it; the second is the pipeline
step that refuses *everything else* a caller has no permission for. A service
with any public surface at all — a sign-in page, a careers page, a door behind a
shared secret rather than a token — leaves the second one out and keeps the
first:

```csharp
noelia.UseDefaults()
      .Without(NoeliaModule.PermissionEnforcement,
               "we have a public surface, and it is declared at the endpoints");
```

### What input sanitization actually looks at

`UseInputSanitization()` inspects the **query string**, **form bodies**, the
**`X-Forwarded-For` and `X-Real-IP` headers**, and every **string value in a
JSON body**. Not the path, not other headers, and not binary content. A promise
wider than its effect is more dangerous than none, because people rely on it.

It matches **syntax, never words**. A keyword carries no information about
intent: `Union-Investment` is a fund manager, `Drop-In-Zentrum` is a place, and
`O'Brien` is a name. What it matches is a quote that ends a literal and starts
an operator, a comment introducer after a quote, a statement terminator followed
by a keyword, a tautology, an LDAP filter breakout, a shell metacharacter
followed by a command, markup, and path traversal.

Until 4.1 it matched the bare keyword at a word boundary — and, on its own,
every semicolon, pipe, backtick, apostrophe, quote, bracket and brace. A hyphen
is a word boundary and an underscore is not, so `delete-account` was an attack
and `delete_account` was not. It also treated `Referer` as input, so every
request from a page whose own URL contained such a word was refused, including
the one asking who is signed in. Meanwhile JSON bodies were exempt — so for an
application whose whole write surface is JSON, it inspected nowhere anything
arrives and refused ordinary traffic everywhere else.

**A refused request is refused; an accepted one is passed on untouched.** The
JSON body used to be re-serialised on every request whether or not anything had
changed, which rebuilt property names and turned every number into a `decimal`.
Editing someone's text without saying so is not a weaker refusal, it is a
different and worse thing: nothing downstream can tell it happened.

The refusal is a **problem document** (`application/problem+json`) naming the
correlation id, like every other error Noelia writes. What it cannot name is the
**field**: a request refused here never reached the validator that knows which
one. For an application whose own validation answers `422` with the field name
that is a loss — a precise answer replaced by a blunt one — so it can hand the
body back:

```csharp
services.Configure<InputSanitizationOptions>(o => o.InspectJsonBodies = false);
```

That is a decision about *who reports the error*, not about whether the value is
refused. Turning it off **without** validators is a decision to refuse nothing.

### When you want to differ

`Use` adds, `Without` removes, in any order, and the last mention of a module
wins. What order you write them in never changes what the service does: modules
register in Noelia's order, because they read what earlier ones set up.

**No broker on this service.** `Communication` is not in the defaults, so this is
only worth writing if someone might expect it:

```csharp
noelia.UseDefaults()
      .Without(NoeliaModule.Communication, "reads only; nothing to publish");
```

**Nothing may be cached, for legal reasons.** The reason is required, and this is
why: in a year the line is still there and still says what the auditor asked.

```csharp
noelia.UseDefaults()
      .Without(NoeliaModule.Caching, "ADR-0013: consent decisions must not be cached");
```

**A brake of your own.** The two settings that decide whether a rate limit is a
limit at all — whom it counts, and whom it believes about who that is:

```csharp
noelia.UseDefaults()
      .UseRateLimiting(rate => rate
          .TrustForwardedHeadersFrom("10.0.0.0/8")   // the load balancer, and nobody else
          .PerOrigin()                                // a sign-in route counts per origin
          .Allowing(perMinute: 5, perHour: 20, perDay: 100));
```

`TrustNoForwardedHeaders()` is the default written out loud. It changes nothing,
and it is worth writing: it tells the next reader that this service is meant to
be reached directly, so putting a proxy in front of it later is a change to that
line rather than a silent change in who gets counted.

**A service that reads tokens but must not be able to write them.** Every service
except the one that signs people in:

```csharp
noelia.UseDefaults()
      .UseJwt(jwt => jwt.VerifyOnly(publicKey, keyId));
```

**Everything, for a single instance.** The provider packages extend the builder
themselves:

```csharp
noelia.UseDefaults()
      .Use(NoeliaModule.HttpResponseCaching)
      .Use(NoeliaModule.TokenSessions)
      .UseInMemoryCache("identity-service")
      .UseInMemoryRefreshTokens();
```

**Nothing at all.** A legitimate thing to want, and a visible thing to have done:

```csharp
builder.Services.AddNoelia(config, env, "jobs-service", _ => { });
```

### Choosing providers

```csharp
// Redis, Valkey, Garnet or KeyDB — the same wire protocol
builder.Services
    .AddRedisConnection(connectionString, instanceName: "identity")
    .AddRedisCache("identity")
    .AddRedisSecretProvider(builder.Configuration, builder.Environment)
    .AddConfiguredMasterKey()       // or AddSecretStoreMasterKey()
    .AddRedisSecurityAudit()
    .AddRedisResourceAuthorization()
    .AddRedisTokenRevocation(maxTokenLifetime: TimeSpan.FromHours(24))
    .AddRedisEncryption();

// or, for a single instance and for tests
builder.Services
    .AddInMemoryCache("identity")
    .AddInMemorySecretProvider()
    .AddInMemorySecurityAudit()
    .AddInMemoryResourceAuthorization()
    .AddInMemoryTokenRevocation()
    .AddInMemoryRefreshTokens();
```

Rate limiting brings an in-process counter of its own, so it works from the
first line. `AddRedisCache` or `AddInMemoryCache` replaces it and decides whether
the counters are shared between instances.

`AddRedisSecurityAudit()` derives a purpose-specific HMAC key from the
registered `IMasterKeyProvider`. It never invents a process-local signing key:
every replica and every restart must verify the same trail. Pass a stable
32-byte key to `AddRedisSecurityAudit(signingKey)` when audit signing uses a
separate root. New records hash every event field in a canonical v2 format and
use an atomic Redis compare-and-set when multiple processes append. Audit
records from 4.4.2 and earlier need the separate migration described in
[MIGRATION.md](MIGRATION.md).

Every in-memory registration documents what it costs: state is invisible to
other instances, so a rate limit counts per process and an audit trail does not
survive a restart. That is sound for tests and a single replica, and stated
rather than implied.

### Contributing a module from another package

Noelia does not know what modules exist. `NoeliaModule` is a name, not an
enumeration, and `Use(module, register, contract)` takes the registration and
its verifiable contract along with it —
so a package Noelia has never heard of extends a composition the way a database
provider extends Entity Framework's options builder:

```csharp
namespace Contoso.Billing;

public static class BillingNoeliaModules
{
    public static NoeliaModule Billing => new("Contoso.Billing");

    /// <summary>
    /// Sets up billing. Without it, the endpoints under /api/billing answer 404.
    /// </summary>
    public static NoeliaBuilder UseContosoBilling(this NoeliaBuilder noelia, string apiKey) =>
        noelia.Use(
            Billing,
            g => g.Services.AddContosoBilling(apiKey),
            contract => contract
                .Requires<IPaymentGateway>(
                    new NoeliaProviderHint("Contoso.Payments", "AddContosoPayments()"))
                .Provides<IBillingService>(
                    "Contoso.Billing", "UseContosoBilling(apiKey)"));
}
```

The module then behaves like any other: it appears in the composition, and it can
be left out with a reason. Prefix the name with your package so two packages
cannot collide.

### What this service is running

The composition is in the container, so a service can report it:

```csharp
var composition = app.Services.GetRequiredService<NoeliaComposition>();

foreach (var module in composition.Included)
{
    app.Logger.LogInformation("Noelia module {Module} is active", module);
}

foreach (var (module, reason) in composition.Excluded)
{
    app.Logger.LogInformation("Noelia module {Module} left out: {Reason}", module, reason);
}
```

A module nobody mentioned is in neither list. Silence is not a decision, and
recording it as one would make the report longer and less true.
`composition.Contracts` contains the `Requires` and `Provides` declarations only
for modules whose registration actually ran. A module excluded with `Without`
therefore cannot appear active merely because documentation or defaults once
mentioned it.

### What a module says when something is missing

Modules stand alone: each registers what it owns and nothing else. Where a
module genuinely needs something it cannot provide — a cache needs a cache
server — it says so **at startup**, naming the call that fixes it:

```
Noelia is missing 1 provider registration(s):
  • module 'HttpResponseCaching' needs IDistributedCacheService — call Noelia.Redis → AddRedisCache(prefix) / Noelia.InMemory → AddInMemoryCache(prefix)
Provider packages: Noelia.Redis, Noelia.InMemory, Noelia.Messaging.MassTransit, Noelia.Data.EntityFrameworkCore.
```

The count is services to register, not modules that asked. Two modules needing
one cache is one line and one thing to do — but both are named, because you may
be removing one of them rather than adding the provider.

The pipeline side does the same while it is being composed. `UseHttpCaching()`
without the caching module, or `UseRateLimiting()` without a store, throws there
rather than on the first request that happens to reach the middleware — in
production, naming a Noelia-internal type the reader never wrote.

### The CQRS pipeline asks for a cache only if you cache

`AddCQRS(assemblies)` reads what you hand it. Where nothing implements
`ICacheableQuery` or `ICacheInvalidatingCommand`, the two cache behaviours are
not put in the pipeline at all — no cache is needed, and your composition root
does not have to register one it never uses:

```csharp
services.AddCQRS(typeof(Program).Assembly);   // caches nothing, needs nothing
```

Where something does, the requirement follows the same rule as the modules
above, and names the type that caused it rather than the interface you would
have to go looking for:

```
Noelia is missing 1 provider registration(s):
  • AddCQRS() (GetJobQuery implements ICacheableQuery) needs IDistributedCacheService — call AddRedisCache(prefix) or AddInMemoryCache(prefix)
Provider packages: Noelia.Redis, Noelia.InMemory, Noelia.Messaging.MassTransit, Noelia.Data.EntityFrameworkCore.
```

A cache, and nothing else. Commands that declare `ETagInvalidationPatterns`
clear stale ETags through that same cache, so a command pipeline never drags
HTTP response caching in behind it.

Any `IDistributedCacheService` satisfies the requirement. The check asks for the
interface, not for who registered it, so a provider you wrote yourself counts.

## Identity and multi-tenancy

A principal has two independent axes, and neither is nullable:

```csharp
Principal.Subject   // SubjectId — who is acting
Principal.Acting    // Capacity  — in what capacity
```

`Capacity` is a closed hierarchy with exactly two cases, so matches over it are
exhaustive:

```csharp
if (principal.Acting is not Capacity.ForCompany company)
    return TypedResults.Problem(statusCode: 403);

var jobs = db.Jobs.Where(j => j.Tenant == company.Tenant);
```

`Capacity.ForCompany` carries no role: a token states which company a caller
acts for, never with what rights. Roles are read from the membership store per
operation.

### Keys: who may issue, and who may only verify

With a shared secret the verification key **is** the signing key. Every service
able to check a token can mint one — for any subject, with any role. Compromise
the least important service and you can impersonate anyone at the most
important one.

A key pair separates the two, and `SigningKey` makes that a property of the
object rather than a rule someone has to remember:

```bash
# one line each, no PEM, no escaping
NOELIA_JWT_KID=2026-08
NOELIA_JWT_PRIVATE_KEY=MIGH…    # the issuing service, and nothing else
NOELIA_JWT_PUBLIC_KEY=MFkw…     # everyone who verifies
```

```csharp
// identity-service: issues and verifies
infra.AddJwtAuthentication(o =>
{
    o.SigningKey = SigningKey.FromEcdsaPrivateKey(config["NOELIA_JWT_PRIVATE_KEY"]!, kid);
    o.ValidationKeys.Add(SigningKey.FromEcdsaPublicKey(config["NOELIA_JWT_PUBLIC_KEY"]!, kid));
});

// every other service: verifies, and cannot issue
infra.AddJwtAuthentication(o =>
    o.ValidationKeys.Add(SigningKey.FromEcdsaPublicKey(config["NOELIA_JWT_PUBLIC_KEY"]!, kid)));
```

Asking the second one to issue throws, naming why. ES256 rather than RS256 by
default: both halves are a single base64 line that fits in an environment
variable, and signing happens on every sign-in.

Generate a pair with `SigningKey.GenerateKeyPair(kid)`, or in development let
several services derive the same one from a seed — which refuses to run outside
development, because everyone holding the seed can issue:

```csharp
o.SigningKey = SigningKey.DevelopmentFromSeed(config["NOELIA_DEV_KEY_SEED"]!, env);
```

**`ValidationKeys` is a list on purpose.** Rotation and the move off a shared
secret both need two live keys at once — the new `kid` starts issuing while
tokens under the previous one are still in circulation. With a single key,
either change signs every user out at the moment it takes effect:

```csharp
o.SigningKey = current;                    // ES256, kid 2026-08
o.ValidationKeys.Add(current);
o.ValidationKeys.Add(previous);            // ES256, kid 2026-07 — until they expire
o.ValidationKeys.Add(legacySharedSecret);  // HS256 — until the window closes
```

The accepted algorithms come from these keys, never from a token header.

Calling `AddJwtAuthentication()` with no options keeps the shared-secret path
from `JwtSettings:Secret` or `JWT_SECRET`, unchanged.

### Swapping Noelia's own sign-in for a provider

A library whose authentication cannot be exchanged for Keycloak, Zitadel or
authentik is itself the dependency it claims to prevent. The same verification
path takes either source:

```csharp
// Noelia's own keys
infra.AddJwtAuthentication(o =>
    o.ValidationKeys.Add(SigningKey.FromEcdsaPublicKey(publicKey, kid)));

// or a provider's published key set — discovery, JWKS, kid rotation
infra.AddJwtAuthentication(o => o.Authority = "https://keycloak.intern/realms/wt");
```

A service behind a provider issues nothing, so it needs no signing key and gets
no `IJwtService`. Both together is the shape a migration has, where the old and
the new issuer are live at once.

### Wiring

```csharp
builder.Services.AddSharedInfrastructure(
    builder.Configuration, builder.Environment, "jobs-service", infra => infra
        .AddJwtAuthentication()
        .AddPrincipal());

app.UseNoelia(builder.Environment, "jobs-service", pipeline => pipeline
    .UseAuth()          // authentication, then authorization
    .UsePrincipal());   // translates the token once
```

`AddPrincipal` is separate from `AddJwtAuthentication` on purpose: a gateway
verifies tokens without ever building a principal, and a service may build one
from claims another scheme established.

The middleware answers 401 when a token's claims cannot be translated, rather
than letting the request continue without a principal. Downstream code reads
`ICurrentPrincipal` instead of inspecting claims again.

Endpoints declare the capacity they need:

```csharp
[Authorize(Policy = NoeliaPolicies.ActingForCompany)]
[Authorize(Policy = NoeliaPolicies.ActingAsSelf)]
```

Issue a company token only after verifying membership. `AddJwtAuthentication`
registers `IJwtService` for that — the same module that verifies tokens issues
them, so both read one `JwtSettings` and a service cannot mint a token it then
refuses:

```csharp
var issued = await jwt.GenerateTokenAsync(new UserClaims
{
    UserId = subject.ToString(),
    Email  = email,
    Acting = new Capacity.ForCompany(tenant)   // only after checking membership
});
```

A subject needs an identifier and an address, nothing more. Noelia does not ask
for a name in two parts: that refuses tokens to mononyms, to names that do not
split that way, and to service accounts.

### Query filtering

Multi-tenancy is opt-in **per entity**. An entity implementing `ITenantOwned`
gets a tenant query filter; everything else is untouched. Entities keyed by
`SubjectId` — profile, résumé, consent — deliberately do not implement it, so
they follow the person rather than the company.

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    => builder.AddNoeliaIdConverters();

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Job>().HasQueryFilter("SoftDeletion", j => !j.IsDeleted);
    modelBuilder.ApplyTenantFilters(() => CurrentTenant);
}

private TenantId CurrentTenant => _principal.Current?.TenantForQueryFilter ?? TenantId.None;
```

The filter is named, so it coexists with others on the same entity and can be
lifted on its own with `IgnoreQueryFilters(["NoeliaTenant"])`.

Noelia supplies the primitives and the model-builder extension. Each service
owns its own `DbContext`, as it does for the outbox and the processed-event
store.

## Permissions

Noelia ships no permissions of its own. An application declares which roles
grant what, and role inheritance comes from the same place:

```csharp
builder.Services.AddPermissionCatalog(c => c
    .Role("User", "profile:read_own")
    .Role("Admin", "users:view_all", "users:delete")
    .RoleInherits("Admin", "User"));

builder.Services.AddNoeliaAuthorization();   // after the catalogue
```

Every permission in the catalogue also becomes a policy of the same name, so
`[Authorize(Policy = "users:delete")]` works without declaring anything else.

An endpoint states its requirement with `[RequirePermission("users:delete")]`.
Applications that would rather keep the map in one place can register a policy
instead:

```csharp
builder.Services.AddEndpointAccessPolicy(p => p
    .Public("/health", "/swagger")
    .Require("users:view_all", "/admin/users", "GET")
    .Require("users:delete", "/admin/users", "DELETE"));
```

The attribute wins over the policy. `users:*` satisfies any permission in that
category, and `*` satisfies everything.

## Resources

Resource-based authorization needs to know which resource a request is about.
An application declares that once:

```csharp
builder.Services.AddResourceMap(m => m
    .RouteParameter("Job", "jobId")
    .PathSegment("Job", "jobs", "postings")
    .PathSegment("Company", "companies")
    .IdParameters("Job", "jobId"));
```

Resolution order is: an explicit `[ResourceAuthorize]`, then the route
parameters, then the path segments. **Ambiguity fails closed** — when two
distinct resource types can be inferred from one request, nothing is inferred
and the request is denied rather than guessed at.

Permissions per resource and action are registered with `IPermissionResolver`,
which likewise starts empty.

### Conditional permissions

A permission may depend on the resource itself — "the author may edit". What
such a condition *means* is the application's to say:

```csharp
builder.Services.AddPermissionConditions(c => c
    .Condition("user is the author", ctx =>
        ctx.ResourceData is Posting p && p.AuthorId == ctx.UserId));
```

An undeclared condition denies and logs. Noelia has no vocabulary of its own
here, and inventing one would mean granting access on a sentence nobody wrote.

## Sessions: two tokens, and only one of them can be taken back

A JWT is valid until it expires; there is nothing to delete. That is what makes
it fast — every service verifies it from the signature alone, with no round
trip — and it is also why signing out is not simply a matter of forgetting it.

Noelia answers that with two tokens whose properties are opposites:

| | Access token (JWT) | Refresh token |
|---|---|---|
| Who verifies it | **every** service, from the signature | only the issuing service |
| Lifetime | `JwtSettings:ExpireMinutes`, 60 by default | `TokenSessions:RefreshTokenLifetime`, 14 days |
| Stored | nowhere | one row, in **your** database |
| Taken back | only via a revocation store | by setting a column |

Signing out ends the session where the refresh token lives. Nothing else has to
be running for that to take effect, and the access token already issued stands
until it expires — a window you set as a number, not infrastructure you deploy.

```csharp
builder.Services.AddSharedInfrastructure(config, env, "identity", infra => infra
    .AddJwtAuthentication(o => { /* keys */ })
    .AddPasswordHashing()
    .AddTokenSessions());

builder.Services.AddInMemoryRefreshTokens();                       // day one
// builder.Services.AddEntityFrameworkRefreshTokens<AppDbContext>();  // in earnest
```

```csharp
var signIn = await sessions.SignInAsync(subject);       // session + first token
var again  = await sessions.RefreshAsync(presented);    // rotates, one token per use
await sessions.SignOutAsync(session);                   // this device
await sessions.SignOutEverywhereAsync(subject);         // all of them
var mine   = await sessions.ActiveSessionsAsync(subject);
```

### Rotation, and what it catches

Every refresh issues a new token and retires the old one. That is not
housekeeping: it is the only way a theft becomes visible.

Without rotation, a stolen refresh token works for its whole lifetime and
**nobody ever finds out** — attacker and owner use the same valid credential.
With rotation, whoever refreshes second presents a token that was already
consumed, which cannot happen honestly. Which of the two is the thief is
unknowable, so the whole session ends and the owner signs in again with a
password the attacker does not have.

The trap is the honest case that looks identical: two browser tabs both hit a
401 and both refresh. Treating that as theft signs out people who did nothing
wrong. `ReuseGracePeriod` (15 seconds) is the window in which a second
presentation is answered with a fresh token for the same session instead of an
alarm — reported as `RotatedWithinGrace`, so a run of them on one session is
still visible. It is a deliberate concession, and bounded: inside it a replay
genuinely cannot be told from a second tab.

`AbsoluteSessionLifetime` (30 days) is the ceiling. Without it, refreshing
forever keeps a session alive forever, which is exactly what an undetected
stolen token wants.

### Inside a transaction you already have

The Entity Framework store joins a transaction the caller has open and opens one
of its own only when there is none. So an application that writes an audit row in
the same transaction as the change that caused it can put a refresh in that
bracket like anything else:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);

var refreshed = await sessions.RefreshAsync(presented, ct);
db.AuditEvents.Add(AuditEvent.TokenRefresh(refreshed.Session));
await db.SaveChangesAsync(ct);

await transaction.CommitAsync(ct);   // the rotation and the record, or neither
```

Rolling back takes the rotation with it, and the presented token goes on working
— which is the point: an audit row that survives a rollback records something
that did not happen.

Rotation is still one atomic step whichever way it runs. The store never rolls a
caller's transaction back; where a concurrent refresh has already taken the row,
it says so and leaves the caller's work alone.

### Where the refresh token belongs in a browser

Not in `localStorage` and not in `sessionStorage`: script can read both, and a
refresh token is worth days where an access token is worth minutes. Send it as
an `HttpOnly`, `SameSite=Strict` cookie scoped to the refresh path, keep the
access token in memory, and fetch a new one on load. A cross-site scripting bug
then costs one short-lived token instead of the account.

### Cleaning up

`PurgeAsync(olderThan, batchSize)` removes rows that are finished. Noelia never
calls it: retention is policy, the table is yours, and a background loop in a
library owns a schedule in your process. Call it from whatever already runs
your scheduled work.

In batches, and that is not a detail — an unbounded delete competes with
`TryConsumeAsync` for the same pages, and on a single-writer database that
blocks the sign-in path.

## Passwords

**Noelia picks no algorithm.** It defines the port and ships three
implementations; which one writes is the deployment's decision, exactly like
which server its data lives on.

```csharp
infra.AddPasswordHashing();              // PBKDF2 unless something else registers
services.AddArgon2Passwords();           // ...or Argon2id writes
services.AddBCryptPasswords();           // ...or bcrypt
```

PBKDF2 is the fallback only because it needs no package and no licence. Argon2id
is OWASP's first choice and the reason is memory: PBKDF2 costs an attacker time,
which purpose-built hardware buys back cheaply, and Argon2id costs memory, which
it does not.

### Why a library does this at all

Because the alternative is that every service writes it, and there are five
standard ways to get it wrong — no work factor, no salt, a comparison that exits
early, a cost fixed in code with no way to raise it, and a construction someone
invented. None of that is domain knowledge; it is a pure function with
operational parameters, which is the same shape as everything else here.

What would **not** be acceptable is Noelia deciding for you. Hence the port, the
three implementations, and the next section.

### Changing your mind, and arriving with someone else's entries

A deployment that cannot change its algorithm without asking everyone to reset
has not chosen one — it was given one. So every format ever written stays
readable while exactly one writes:

```csharp
infra.AddPasswordHashing();              // PBKDF2 writes
services.AddBCryptPasswordReader();      // bcrypt entries still verify
```

A successful sign-in against any other format answers
`SuccessRehashNeeded`. Rewrite the entry from the password you were just handed
— the one moment it exists — and that person is across. Nobody is asked to reset
anything, and the old format leaves as people return.

The same mechanism covers all of it: a migration from another system, a switch
from PBKDF2 to Argon2id, a raised cost. Each algorithm ships as both a writer
and a reader (`AddArgon2Passwords` / `AddArgon2PasswordReader`), and several
readers can be registered at once, which a long-lived system will need.

### Adding one

Implement three methods. `Hash` writes, `CanRead` says which entries are yours,
`Verify` answers `Success`, `SuccessRehashNeeded` or `Failed`:

```csharp
public sealed class ScryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => /* … */;
    public bool CanRead(string encoded) => encoded.StartsWith("$scrypt$");
    public PasswordVerification Verify(string password, string? encoded) => /* … */;
}

services.AddKeyedSingleton<IPasswordHasher>(PasswordHashing.PrimaryKey, new ScryptPasswordHasher());
```

Two rules the shipped ones follow and yours should. `encoded` may be **null** —
that means no such account, and the implementation still spends the work before
answering `Failed`, or how long an answer takes says whether someone has an
account here. And a damaged entry returns `Failed` rather than throwing: one bad
row must not break signing in for everyone.

## Token revocation

A JWT is valid until it expires; there is nothing to delete. A revocation list
is what makes "sign out" take effect before then.

```csharp
builder.Services
    .AddRedisConnection(connectionString, "identity")
    .AddRedisTokenRevocation(maxTokenLifetime: TimeSpan.FromHours(24));

app.UseNoelia(builder.Environment, "identity", pipeline => pipeline
    .UseAuth()
    .UseTokenRevocation());     // after UseAuth, which establishes the claims
```

One registration serves both sides from one instance: the evaluator answers
from the state the writer records. `maxTokenLifetime` is how long a cutoff is
kept and must be at least the longest lifetime an access token can have, or a
cutoff expires while tokens it should refuse are still valid.

**This is the upgrade, not the entry price.** Ending a session is what
`AddTokenSessions()` does, in your own database, with no extra server. A
revocation store closes the remaining window — the access token already issued —
to zero. Add it when that window matters; most deployments shorten
`ExpireMinutes` instead.

`AddJwtAuthentication()` asks for no store, and a service without one issues and
verifies normally.

`UseTokenRevocation()` throws at startup when no evaluator is registered. There
is no silent default: a revocation check that always answers "not revoked" is
indistinguishable from one that works, and the difference only shows when
someone needs a token to stop working. Turning it off is explicit and requires
a stated reason:

```csharp
builder.Services.AddNoTokenRevocation(
    "access tokens live 15 minutes; revocation happens at the refresh path");
```

That also registers a writer, and the writer **throws**. A deployment that
declared it revokes nothing must not have a `RevokeTokenAsync` that quietly
succeeds: the caller believes it withdrew a token, and the path that tells
someone "signed out everywhere" cannot complete when nothing was withdrawn.

Three things can revoke a token:

```csharp
await writer.RevokeTokenAsync(jti, expiresAt, "user requested");
await writer.RevokeSessionAsync(subjectId, sessionId, expiresAt, "device lost");
await writer.RevokeSubjectBeforeAsync(subjectId, DateTimeOffset.UtcNow, "password changed");
```

`RevokeSubjectBeforeAsync` is the one that scales: it refuses every token
issued before a cutoff, including tokens the store has never seen. It is
idempotent and monotonic — two concurrent revocations cannot undo one another.

**A cutoff alone closes only the access-token window.** Whatever issues refresh
tokens has to revoke those in the same operation, or the holder simply
refreshes into a new access token issued after the cutoff.

### Behaviour during an outage

Neither answer is right on its own: refusing everything turns a brief store
outage into a full sign-out; honouring everything lifts every revocation for
as long as it lasts. The decorator keeps the last known verdict for a stated
span, then closes:

```csharp
new DegradingTokenRevocationEvaluator(inner, new RevocationDegradationOptions
{
    MaxStaleness = TimeSpan.FromSeconds(30),
    OnUnknown    = UnknownStatePolicy.Deny,
    Budget       = TimeSpan.FromMilliseconds(50)
}, logger);
```

A stale verdict is marked as such in `RevocationVerdict.IsStale` and logged —
a system running on stale revocation data works, but not as well as it looks.

### Sharing the store between languages

`TokenRevocationKeys` fixes the key layout so services written in different
languages can share one store: prefix `noelia:revocation:`, cutoffs as Unix
**seconds** in decimal ASCII, compared strictly less-than. During a migration a
Python service may write the cutoff that a .NET service reads, and
`1755264720` and `1755264720.0` are not the same string.

## Rate limiting

```csharp
builder.Services.AddDistributedRateLimiting(builder.Configuration);
builder.Services.AddRedisCache("identity");     // replaces it with a shared one

app.UseDistributedRateLimiting();
```

`AddDistributedRateLimiting` registers a counter that lives in this process, so
the rules work from the first line. The store is still a separate choice from
the rules, because it decides how much traffic actually gets through: with a
shared counter all instances draw from one budget, in process each counts for
itself, so the effective limit is multiplied by the replica count.

**Per-path limits start empty.** They used to arrive holding seven paths from
the application Noelia was extracted from — `/api/auth/login`, `/api/admin/*`
and the rest — and configuration adds to that dictionary rather than replacing
it, so they could not be removed from outside. Measured: a call to
`POST /api/auth/register` was refused at three per minute in an application that
has no such route.

**Loopback is exempt by default** (`WhitelistedIps`). It is not forgeable — the
origin comes from `Connection.RemoteIpAddress` and nothing else — but it applies
in exactly the place a rate limit gets tried out first, your own machine, where
it then looks as though nothing is counting. `Exempting(...)` replaces the list;
`Exempting()` empties it.

**The refusal is a problem document.** `application/problem+json`, carrying the
correlation id as well as the trace identifier — because the one answer a person
actually reports is *I am locked out*, and it used to be the one answer with no
id anybody could look up.

`IDistributedRateLimitStore.SlidingWindowIncrementAsync` counts and decides in
one indivisible step. Implementations that cannot guarantee that are not valid
implementations of the port — a conformance suite asserts it with fifty
concurrent callers racing for ten slots.

**A missing shared counter fails closed by default.** A Redis outage therefore
returns `503 Service Unavailable`; it is not reported as an exhausted budget and
does not silently remove the limit. Applications that deliberately prefer
availability can set
`DistributedRateLimiting:CircuitBreaker:FallbackBehavior` to `AllowAll`.
That choice is explicit because it temporarily turns every protected limit off.

## Messaging

```csharp
builder.Services.AddMessaging(builder.Configuration, typeof(Program).Assembly);
```

`IEventBus` has one method and is fire-and-forget by design. It promises that
the transport accepted the event — **not** a transactional outbox, and not that
the event survives a crash between the database commit and the publish.

A delivery guarantee that appeared or disappeared depending on the configured
transport would be worse than none, because callers would rely on it. So the
guarantee is a separate thing you compose, and it belongs to the service that
owns the transaction.

### The outbox

```csharp
noelia.UseDefaults().UseOutboxDispatcher();
builder.Services.AddEntityFrameworkOutbox<OrderContext>();
```

```csharp
protected override void OnModelCreating(ModelBuilder model) =>
    model.MapNoeliaOutbox();          // then generate a migration
```

`IOutbox.RecordAsync` writes the intent through the same `DbContext` as the
change that caused it and **deliberately does not save**. Your save decides
whether both happened, or neither:

```csharp
context.Orders.Add(order);
await outbox.RecordAsync(new OrderPlaced(order.Id));
await context.SaveChangesAsync();     // both, or neither
```

`UseOutboxDispatcher()` runs the loop that delivers what was recorded. One
service in a deployment needs it, not every one — several dispatchers against
one store are safe, because claiming a batch is a conditional update with a
five-minute lease, but each one is another connection holding messages.

**Delivery is at-least-once and the dispatcher does not pretend otherwise.** It
publishes first and marks second: a crash in between delivers a message twice,
where the other order loses it. `OutboxMessage.Id` travels with the message so a
consumer can decide. A message that has failed `AttemptsBeforeAlarm` times is
logged as stuck and left in the table — never dropped, because a payload nothing
will accept is a decision for an operator.

Turning a recorded payload back into an event is yours: `IOutboxPayloadReader`
is a port, because deserialising an arbitrary named type out of a database row
is how a row becomes code execution.

## Persistence

```csharp
builder.Services.AddDatabaseContext<AppDbContext>(
    builder.Configuration, "identity",
    (options, connectionString) => options.UseNpgsql(connectionString));

builder.Services.AddEntityFrameworkExceptionMapping();
```

Noelia resolves *where* — `ConnectionStrings__identity` from the environment,
then `ConnectionStrings:identity`, then `ConnectionStrings:DefaultConnection`,
and it throws when none is set. The application binds the provider, because the
provider package is the application's dependency.

`AddEntityFrameworkExceptionMapping()` teaches the exception handler about EF's
persistence failures. Without it a concurrency conflict surfaces as a plain 500
instead of a 409.

## Health checks

```csharp
builder.Services.AddNoeliaHealthChecks();

app.UseHealthCheckEndpoints();   // /health, /health/live, /health/ready
```

`/health/live` answers 200 even when a dependency is down: a liveness probe
tied to the database makes the orchestrator restart the process because the
*database* is gone, which lengthens the outage instead of ending it.
`/health/ready` answers 503, because a pod that cannot reach its store should
not take traffic.

Provider packages contribute their own checks; the aggregator names no driver.

## Security checks

Security checks answer a different question from health checks: not “can this
process take traffic?”, but “does the active composition still enforce its
declared security boundary?”. `AddNoelia` runs them once at startup. An operator
can run the same bounded checks explicitly and read the latest completed report:

```csharp
builder.Services.Configure<SecurityCheckOptions>(options =>
    options.Timeout = TimeSpan.FromSeconds(3));

// after the host has started, from explicitly authorised operator code
var runner = app.Services.GetRequiredService<ISecurityCheckRunner>();
var results = await runner.RunAsync();
var latest = app.Services.GetRequiredService<ISecurityCheckReport>().Latest;
```

| Stable id | Active module | What it proves |
|---|---|---|
| `noelia.composition.providers` | composition (always) | every active `Requires` has a registered provider |
| `noelia.jwt.key-separation` | `Jwt` | verification does not grant token-issuing power, or follows an HTTPS authority |
| `noelia.headers.browser-baseline` | `SecurityHeaders` | CSP, middleware and production HSTS are active |
| `noelia.sessions.refresh-cookie` | `TokenSessions` | cookies are HttpOnly, TLS-only and same-site constrained |
| `noelia.cors.credentialed-origins` | `Cors` | browser origins are explicit rather than wildcard |
| `noelia.secrets.provider` | `SecretManagement` | a deliberate, non-ephemeral provider backs secrets |
| `noelia.encryption.aead` | `Encryption` | authenticated encryption and a correctly shaped master key are active |
| `noelia.ratelimit.degradation` | `RateLimiting` | the limiter is enabled and never fails open |
| `noelia.revocation.degradation` | `Jwt` | revocation is present and was not explicitly disabled |
| `noelia.dashboard.operator-access` | `Dashboard` | an explicit operator policy and audit trail protect the page |

Only checks belonging to `NoeliaComposition.Included` run; composition itself is
always checked. Results use `Pass`, `Warning`, `Fail` or `NotApplicable` and
contain fixed summaries and remediation, never configuration values, keys,
tokens, connection strings or raw exceptions. One check timing out cannot hang
startup. A failed security check is reported but does not redefine liveness.

Noelia intentionally exposes no `/security` endpoint. `Noelia.Dashboard` is the
only built-in HTTP view of these results, and it requires an explicit operator
decision described below.

## Operator dashboard

`Noelia.Dashboard` is a separate, optional package with zero restored NuGet
dependencies of its own. It uses server-rendered HTML and embedded CSS/vanilla
JavaScript; it adds no server, npm bundle, CDN or Blazor runtime.

```csharp
builder.Services.AddNoelia(
    builder.Configuration, builder.Environment, "identity-service",
    noelia => noelia
        .UseDefaults()
        .UseDashboard(dashboard => dashboard
            .At("/noelia")
            .VisibleTo(context => context.User.IsInRole("Operations"))
            .InspectConfiguration("Jwt", "DistributedRateLimiting")));
```

Without `VisibleTo(...)`, and whenever that predicate rejects the request, the
page answers an empty `404` rather than a revealing `403`. In Production the
composition refuses to build until the exposure is made explicit with a reason:

```csharp
.UseDashboard(dashboard => dashboard
    .VisibleTo(context => context.User.IsInRole("Operations"))
    .InProduction("operations access is restricted to the private cluster network"))
```

The dashboard authenticates the configured default ASP.NET scheme before it
evaluates `VisibleTo`, so role and claim policies work even though the dashboard
startup filter is registered ahead of application middleware. Authentication
errors fail closed as the same empty `404`.

The reason itself is not rendered; only the fact and its character count are.
Every authorized page or asset request is written to `IAuditTrailService` when
one is registered. If that registered trail cannot accept the event, the
dashboard fails closed with `503`. A missing audit trail remains visible as a
security warning rather than silently pretending access was recorded.

The page is GET/HEAD-only and sends `Cache-Control: no-store` plus a restrictive
CSP. It identifies the service, environment and answering machine, because
audit chains, observed sessions and in-process counters are per instance. It
shows:

- active modules and verbatim reasons recorded by `Without`;
- each module's requirements, provider remedies, provisions and active readers;
- configuration keys only as `set` or `no explicit value`, without value lengths;
- security results only for the composition plus its universal composition check;
- sovereignty findings and declared egress hosts;
- a state-free audit view, token-free session observations, HMAC-fingerprinted
  rate-limit counters and health results without descriptions, data or exceptions.

The default audit view can prove only that this process produced a valid chain
at write time. It says “persisted sink not verified” because the write-only sink
port cannot detect later store tampering; a provider must explicitly supply such
a read model before the page may claim persisted verification.

A module whose contract has neither a requirement nor a provision is called out
as “running, but no declared effect”. That is intentional: a registration that
quietly does nothing must be visible rather than counted as success.

For an isolated host that deliberately references no `Noelia.Infrastructure`,
the package also exposes `AddNoeliaDashboard(...)`. It creates a composition
containing exactly `NoeliaModule.Dashboard` and its own access check. An
application that already uses `AddNoelia` must use `UseDashboard(...)` inside
that existing composition instead.

## Error messages, in your language and not Noelia's

`IErrorMessageService` turns an error code into something a user can read.
Noelia decides the **structure** — which code it is, whether it may be shown to
a user at all, which help page explains it, which actions might resolve it. The
**wording** is yours: it is your language, your tone, your audience, and in a
regulated domain sometimes your legal department's.

```csharp
builder.Services.AddSingleton<IErrorTextProvider, GermanErrorText>();
```

```csharp
public sealed class GermanErrorText : IErrorTextProvider
{
    public string? Message(string errorCode) => errorCode switch
    {
        ErrorCodes.ResourceNotFound => "Das gibt es hier nicht.",
        _ => null                       // untranslated stays readable
    };

    public string[]? SuggestedActions(string errorCode) => null;
}
```

Returning `null` for a code you have not translated is the point: a
half-finished translation should leave the untranslated half readable rather
than blank. Register nothing and the built-in English wording applies, which is
the language of this package and no claim about the user's.

`GetSuggestedActions` returns **keys** from `ErrorActions` — `check-input`,
`contact-support` — not sentences. A key is something to look up; a sentence in
a library is something somebody has to override. If you render those actions
directly, a rendered key is obvious immediately; a sentence in a language the
user did not choose never is, which is how the German wording survived four
major versions unnoticed.

## Reading a service with a program

The dashboard is a page for a person. The same reading is available as data at
`GET {dashboard path}/report.json`, deserialising into `OperatorReport` from
`Noelia.Abstractions.Operator`.

```bash
curl -s https://ops.internal/noelia/report.json | jq '.sovereignty.dependencies'
```

Both come from one collection, so they cannot disagree: the page renders the
report, and the endpoint serialises it. It sits in the same middleware, behind
the same authentication, the same visibility rule and the same audit entry — a
report path that were easier to reach than the page would be a way around the
access rule rather than a second view of it. Configuration values, tokens and
keys are absent from both.

Each section carries one of four states rather than an empty list —
`Absent`, `Unavailable`, `Faulted`, `Present`. A service with no rate-limit
store and a service whose store stopped answering are not the same finding: one
is a composition decision and the other is an outage.

Set `Dashboard:Fleet` to say which deployment a service belongs to. Noelia
never invents one; a guessed name would group unrelated services and look
authoritative doing it.

### Verifying the audit chain

`IAuditTrailService` chains every entry to its predecessor's hash. Reading that
chain back and recomputing it is a separate request:

```bash
curl -s https://ops.internal/noelia/audit-chain.json
```

```json
{ "isSupported": true, "isIntact": true, "entriesVerified": 1284402,
  "head": "aU5niZ…", "firstBreak": null }
```

Two things are checked per entry, and they catch different edits. Recomputing
the entry's own hash catches a record rewritten in place. Comparing its
`previousHash` against the entry before it catches a record removed, inserted or
moved — where every record is individually intact and only the sequence is a
lie. A break is **named**, not merely counted: a verdict of "invalid" without a
location sends an investigator back to the manual work they wanted a machine
for.

It is deliberately not part of the report. Recomputing millions of entries on
every page load would make opening the dashboard an attack on the store it
reports about, so the report carries only `audit.canBeVerified`.

A sink says it can be read back by implementing `IReadableSovereignAuditSink`;
`Noelia.Redis` and the in-process sink do. One that only writes is still a valid
sink — an append-only log, or a foreign system you write into and not out of —
and the verifier then answers `isSupported: false` with what would make it
possible. That is not a failed verification, and a report that showed the two
alike would let "we never checked" read as "we checked and it was fine".

## Telemetry

```csharp
builder.Services.AddTelemetry("identity-service", "1.0.0", t => t
    .AddTracing(tracing => tracing.AddNoeliaEntityFrameworkInstrumentation())
    .AddMetrics()
    .AddLogging());
```

Noelia emits **OTLP** and nothing else — the neutral protocol, aimed at a
self-hostable OpenTelemetry Collector that fans out to whatever you run. A
backend-specific exporter is the application's choice and goes through the
`configure` callback.

### Instrumentation Noelia does not ship

`OpenTelemetry.Instrumentation.EntityFrameworkCore` and
`.Process` have never had a stable release, and a stable package must not drag
a prerelease into every consumer's tree. An application that wants them
installs the package and adds them through the same callback:

```csharp
builder.Services.AddTelemetry("identity-service", "1.0.0", t => t
    .AddTracing(tracing => tracing.AddEntityFrameworkCoreInstrumentation(o =>
    {
        // A SQL statement carries table names and parameter values, and spans
        // travel to wherever telemetry is collected. The instrumentation has no
        // option for this, so the tag is cleared after it is set.
        o.EnrichWithIDbCommand = (activity, _) =>
        {
            activity.SetTag("db.statement", null);
            activity.SetTag("db.query.text", null);
        };
    }))
    .AddMetrics(metrics => metrics.AddProcessInstrumentation()));
```

### What never reaches a log

**Noelia logs no values.** Not sanitised values, not redacted values — none.
Two paths could write data into a log, the CQRS behaviour running a command and
the HTTP middleware handling a request, and both write the *shape*:

```
Shape of CreateTodoCommand: {Title: string(41), Note: string(26)}
Incoming Request: … Body = {displayName: string(12), email: string(15), amount: number}
```

Field names and value sizes. That is what a person debugging actually needs —
which fields arrived and whether they were empty — and it cannot leak, because
there is nothing in it to leak.

This replaced redaction by field name, which does not work and looked as though
it did. Redaction is enumeration: it removes what somebody thought of. A case
reference, a note to a doctor, the name of a company someone is leaving, a
title reading *"Termin bei Dr. Weber wegen der Kündigung"* — none of it is on
any list, however long the list gets.

What still is removed rather than described, because it is a credential in a
place everything logs: `Authorization`, `Cookie` and `Set-Cookie` headers, and
token-bearing query parameters, which is how a verification link becomes a log
entry.

**Request and response bodies are not logged at all by default**
(`Observability:EnableDetailedHttpLogging`). Turning it on gives you shapes, not
contents.

The diagnostic handle that survives everywhere is the pseudonymous id: the
logging scope carries `UserId`, which identifies a person to your database and
to nobody reading the log.

### If you log a payload yourself

Noelia still ships `ILogSanitizer` and the vocabulary behind it —
`SensitiveFieldNames` (about a hundred names, matched exactly so `RequestName`
survives) and `SensitiveValuePatterns` (email, card number, IBAN, national
identifier, by shape wherever they appear). Noelia no longer uses either
internally, and that is deliberate.

```csharp
logger.LogDebug("{@Payload}", sanitizer.Sanitize(payload));
```

Use it if you decide to log a payload anyway. Know what you are getting: it is
a net, not a guarantee, and the paragraph above says why.

### Logging

```csharp
LoggingConfiguration.ConfigureSerilog(configuration, environment, "identity-service");
```

Enrichment, filtering and exception shaping are Noelia's. **Where the logs go is
the application's**: declare `Serilog:WriteTo` in configuration and install the
sink package alongside naming it. Declaring even one sink replaces the built-in
console and file sinks completely, so list every destination you want.

### Security headers

`AddSecurityHeaders()` plus `UseSecurityHeaders()` set the headers and then
check what actually went out. Three rules keep that check worth reading:

- **`X-XSS-Protection` is neither sent nor demanded.** It controlled a browser
  XSS auditor that was itself exploitable; browsers removed it and OWASP
  advises against sending it.
- **`frame-ancestors` counts as framing protection.** A CSP carrying it is not
  missing `X-Frame-Options` — that header is what it replaced.
- **Over plain HTTP, a missing HSTS header is not a finding.** RFC 6797 §8.1
  says a user agent must ignore an HSTS header received over a non-secure
  transport, so asking for one asks for something discarded.
- **A JSON response drops `X-Frame-Options` and states `frame-ancestors 'none'`
  instead.** Dropping the legacy header alone would leave nothing:
  `frame-ancestors` does not fall back to `default-src`.

Headers that do not apply are left out of the analysis entirely, not merely out
of the findings list — the score is a weighted average over that set, and
scoring against one set while reporting another produced the worst of both: a
warning naming nothing.

Each distinct finding is logged **once per process**, not once per response. A
misconfiguration is constant; a warning repeated on every request buries
everything else in the log and gets the whole check switched off.

## Digital sovereignty

### What Noelia calls out to

Noelia itself opens no connection you did not configure. Every outbound call has
a named cause:

| What calls out | When | Where to |
|---|---|---|
| `ServiceCommunicationManager` | `Communication`, on every `GetAsync` / `SendRequestAsync` | the services in `ServiceEndpoints`, or the gateway |
| `ServiceTokenProvider` | `Communication`, to get a machine token | the token endpoint in `ServiceCommunication:M2M` |
| `OpenBaoSecretProvider` | the OpenBao secret provider only | the address configured for it |
| `Noelia.Redis` | whenever a Redis provider is registered | the connection string you passed |
| `Noelia.Messaging.MassTransit` | `AddMessaging` | the broker you configured |
| OpenID Connect discovery | `UseJwt(jwt => jwt.From(authority))` only | the authority's published key set |

There is no telemetry to Noelia, no licence check, no update ping. A service
with none of the above configured makes no outbound call because Noelia is in it.

### Limiting it

Outbound destinations are declared, and undeclared calls fail:

```csharp
builder.Services.AddNoeliaEgressPolicy(p => p
    .Allow("openbao.internal")
    .AllowSubdomainsOf("example.eu")
    .AllowLoopback());
```

With nothing declared everything is allowed — adding the package must not
change behaviour on its own. Once anything is declared, the policy is
enforcing, and it applies to every `HttpClient` the factory builds, including
the ones Noelia's own modules use.

A refused call throws where it was made, naming the host and the policy. That is
deliberate: a call that silently returned nothing would look like an empty
answer, and an empty answer is something an application acts on.

### Reading the report

```csharp
builder.Services.AddNoeliaSovereigntyReport();

// ...

var report = app.Services.GetRequiredService<ISovereigntyReport>().Assess();
```

The report reads the running configuration and says what it points at. Each
finding is one dependency:

```
Database   db.internal              SelfHosted     private network
Secrets    openbao.internal         SelfHosted     name reserved for internal use
Cache      cache.example.eu         Undetermined   public name; operator not known from the name
Storage    bucket.s3.amazonaws.com  ThirdCountry   provider subject to the US CLOUD Act
```

Three verdicts, and the middle one is the important one:

- **`SelfHosted`** — loopback, a private network, or a name reserved for internal
  use. Infrastructure the operator controls.
- **`Undetermined`** — a public name whose operator cannot be told from the name.
  Most third-party and most European providers land here. **This is not a pass**;
  it is the report saying the question is still open and an operator has to
  answer it.
- **`ThirdCountry`** — a domain belonging to a provider subject to third-country
  access laws. Recognised by name, not by contract: a region in Frankfurt does
  not change who operates a service or which law reaches it, so `*.amazonaws.com`
  is a third-country provider whatever the endpoint says.

Not matching the list is reported as *undetermined*, never as sovereign — the
difference between a report and a rubber stamp. A host name cannot prove a
jurisdiction, and no library can; what this does is make every configured
destination visible in one place, so the ones that need an answer can be seen.

Credentials never reach the report: it is something people paste into tickets.

### Personal data, the audit trail, and one call for both

Three paths carry values into a log — the CQRS behaviour, the HTTP middleware,
and Serilog's own properties — and all three read `SensitiveFieldNames` and write
`[REDACTED]`. Matched exactly, never as a substring, which is what keeps
`SecretName` and `TokenId` readable: a name is not a value.

`IAuditTrailService` chains every entry to the previous one with SHA-256, so an
edit anywhere breaks every hash after it. Where the entries are stored is your
decision — `ISovereignAuditSink` is the port.

The chain is advanced **in the process** unless the sink owns the head. Two
replicas then each start a chain of their own, and a verifier reading the store
back finds a break on a system where nothing was tampered with. A sink that
implements `IChainedSovereignAuditSink` — `UseRedisSovereignAudit()` does, with
a compare-and-set that makes the append atomic — gives one chain for every
replica writing to that server. The dashboard says which of the two you have,
so this is not something you have to remember to check.

Both, plus the egress boundary and the report, come in one call:

```csharp
noelia.UseDefaults()
      .AddSovereignPlatform(sovereign => sovereign
          .WithoutPrivateNetworks()
          .Allow("openbao.internal")
          .DeclareDependency("Secrets", config["OpenBao:Address"]));
```

It is a module like any other: it shows in `NoeliaComposition`, and a service
that deliberately calls outward drops it with a reason — and then nothing of it
is set up.

Full detail in [SOVEREIGNTY.md](SOVEREIGNTY.md).

## Secrets and keys

```csharp
builder.Services.AddRedisSecretProvider(builder.Configuration, builder.Environment);
builder.Services.AddNoelia(
    builder.Configuration, builder.Environment, "identity-service",
    noelia => noelia.UseDefaults().Use(NoeliaModule.SecretManagement));
```

Noelia never generates a master key — a key it invented would be a key nobody
chose to trust. Supply one:

```csharp
builder.Services.AddConfiguredMasterKey();    // from Encryption:MasterKey
builder.Services
    .AddOpenBaoSecretProvider(builder.Configuration)
    .AddSecretStoreMasterKey();               // from noelia/master-key in OpenBao
```

### The ASP.NET key ring

```csharp
noelia.UseRedisCache("identity")
      .UseRedisEncryption()
      .UseDataProtection("identity-service");
```

Without this, ASP.NET creates its key ring itself and puts it in one container's
filesystem in the clear — so every cookie and antiforgery token protected with
it stops verifying the moment that container is replaced, and ASP.NET writes two
warnings at every start that most deployments learn to ignore.

`UseDataProtection(applicationName)` stores the ring through the registered
cache provider and encrypts each element with a key derived from the registered
master key (HKDF-SHA256, then AES-256-GCM with the envelope metadata as
associated data). All three parts are required and each is a port: a master key
opens the encryption provider, the encryption provider protects the ring, the
cache provider keeps it where the next container can read it. In-process
providers satisfy them too, which is what lets a development stage protect its
ring instead of reporting a failure forever.

`ISecretProvider` is the one secret-store seam; `IVersionedSecretProvider` adds
history where a provider supports it. Redis and InMemory implement both. The old
duplicate `ISecretManager` contract and the unregistered provider-switching
implementation are gone in 5.0.

The built-in remote provider targets OpenBao (MPL-2.0, Linux Foundation)
rather than Vault (BSL since 2023). Applications can register an implementation
of their own; `AddSecretStoreMasterKey()` declares that requirement at startup,
so a missing provider does not wait for the first encryption request to fail.

`SecretManager` stores new values as format 2: AES-256-GCM authenticates the
ciphertext together with the requested/stored name, version, creation and
expiry times, active state and creator. A modified, renamed or artificially
unexpired record is refused rather than returned as plaintext. The configured
key must decode to exactly 32 bytes and generated development keys never reach
a log.

Records written by 4.4.2 or earlier used unauthenticated AES-CBC. They cannot be
made trustworthy after the fact and are therefore not read as format 2. Export
needed values in a controlled environment with the old version before upgrading,
then write them again with the patched version. Investigate stores to which an
untrusted party had write access; migration cannot prove an old record was not
already changed.

`IDataEncryptionService.HashAsync` is for salted checksums of non-password data.
Its PBKDF2 default is 600,000 iterations and every result carries the actual
count; pre-patch 30,000-iteration results still verify. User credentials belong
behind `IPasswordHasher`, which also reports when a successful login should
replace an old hash.

`DataEncryption:MaxDataSize` is enforced against the UTF-8 byte count before a
key lookup or encryption allocation. `DefaultAlgorithm`,
`DefaultHashingAlgorithm` and `CompressionThreshold` likewise control the
running service; they are not documentation-only builder switches.

## Testing against the contracts

Ports carry promises their interfaces cannot express — that a revocation takes
effect on the next check, that counting and deciding are indivisible, that a
cutoff never moves backwards. Those live in conformance suites that every
implementation inherits:

```csharp
public class MyStoreConformanceTests : TokenRevocationEvaluatorConformance
{
    protected override (ITokenRevocationEvaluator, ITokenRevocationWriter) CreateStore()
        => /* your implementation */;
}
```

The Redis suites run against a real server in a container and **fail** rather
than skip when no container runtime is reachable: a silently skipped
integration suite is indistinguishable from a passing one.

## Roadmap

- **Two `SecurityHeadersMiddleware` classes** — resolved. There used to be one
  in `Noelia.Infrastructure.Middleware` (config-driven) and one in
  `Noelia.Infrastructure.Security.Headers` (service-driven, with CSP scoring and
  separate script/style nonces). The builder pipeline silently used the first,
  so `AddSecurityHeaders()` registered nothing the running middleware needed and
  the maintained implementation was unreachable. The richer one survives.
- **Three ways to require a permission.** `PermissionMiddleware`,
  `PermissionPolicyProvider` and the per-permission policies
  `AddNoeliaAuthorization` registers all answer the same question, and not
  alike: the first two consult the role catalogue, the third only the
  `permission` claim, so a user holding a role but no explicit claim is allowed
  by two of them and refused by the third. One attribute now drives the first
  two; the third has no attribute and is reachable only as
  `[Authorize(Policy = "users:read")]`. Which one survives is still open.
- **Duplicate type names** — resolved in 6.0.0, and two of the three had
  already gone. `RateLimitResult` and `IDomainEvent` existed once by the time
  anyone looked. `CacheStatistics` was real and dangerous: same name, and one
  reported a ratio of 1 where the other reported a percentage of 100. The
  second audit system was worse — `SecurityEventSeverity` put `Critical` at 3
  on one scale and 4 on the other, so a stored value changed meaning depending
  on who read it. One of each survives; see MIGRATION.md.
- **German error messages** — done in 5.3.0. The shipped wording is English,
  which is the language of this package and no claim about the user's, and
  `IErrorTextProvider` replaces every sentence of it.
- **`ISecretProvider` implementations still sit in `Noelia.Infrastructure`.**
  The port moved to `Noelia.Abstractions`, the OpenBao and file-based
  implementations did not. They belong in `Noelia.Secrets.*` packages.
- **Password entries from another system.** `IPasswordHasher` is a port and
  entries say what they are, so a reader for bcrypt or Argon2id can be layered
  in front — but Noelia ships neither, and a migration that has to re-hash
  everyone on first sign-in is the state today.
- **`DataProtectionSecretProvider` has no consumer** — resolved differently in
  5.2.0. `UseDataProtection(applicationName)` keeps the key ring in the
  registered cache provider and encrypts it at the master key, so the provider
  is no longer the answer to that question. Whether it is still worth shipping
  at all is open.
- **`ILogSanitizer` has no consumer inside Noelia** since logging moved to
  shapes. It stays as a tool for an application that logs a payload of its own,
  and whether that is enough reason to keep it is an open question.
- **Transactional outbox** — done in 5.3.0. `IOutbox` records the intent in the
  transaction that caused it; `UseOutboxDispatcher()` runs the loop that
  delivers it. Delivery is at-least-once and says so.

## Security notice — 4.4.2: encryption envelope metadata was not authenticated

**Affected:** Redis package from the **4.4.1 predecessor line**. **Fixed in 4.4.2.**

**Who is affected.** Anyone who stored values returned by the 4.4.1 predecessor's
`IDataEncryptionService`. Earlier versions have the separate, more severe
4.4.1 notice below: they did not encrypt the payload at all.

**What was wrong.** AES-GCM covered the ciphertext and caller-supplied
`AdditionalData`, but not the envelope fields surrounding them. In particular,
`Metadata["compressed"]` decides whether authenticated plaintext bytes are
decompressed after GCM succeeds. Changing that flag did not change the tag, so
decryption returned `Success = true` and `IntegrityVerified = true` while
returning different data. Timestamp, key id and key-version metadata were also
outside the integrity boundary.

This is not a remote entry point by itself: an attacker first needs write
access to the stored envelope. It is nevertheless an integrity failure, and
storage write access is exactly the adversary authenticated encryption is
supposed to detect.

**What to do.**

1. Upgrade to 4.4.2 before writing more encrypted values.
2. Re-encrypt retained 4.4.1 values. Envelope version `"2.0"` cannot be made
   fully trustworthy after the fact; 4.4.2 refuses it rather than claiming
   complete integrity. Read it with 4.4.1 in a controlled migration and write
   it with 4.4.2, which produces version `"2.1"`.
3. Investigate any store in which an untrusted party could modify 4.4.1
   envelopes. Re-encryption prevents future changes; it cannot prove that an
   old envelope was never altered.

**What changed.** The 2.1 GCM tag authenticates a canonical, length-prefixed
representation of envelope version, key id, algorithm, IV, caller AAD,
timestamp, integrity field and every metadata entry, with keys sorted
ordinally. Ciphertext remains covered directly by GCM. Changing, adding or
removing any semantic envelope field now fails decryption and leaves
`IntegrityVerified = false`.

Counter-checks alter the compression flag, key-version metadata, timestamp,
key id and caller AAD independently. A separate check proves that 2.0 is
refused with migration guidance.

## Security notice — 4.4.1: `AddEncryption` did not encrypt

**Affected:** `Noelia.Redis`, every version up to and including **4.4.0**.
**Fixed in 4.4.1.** Advisory: `GHSA-276v-hjxx-vrmw`.

**Who is affected.** Anyone who called `AddEncryption()` — which requires
`AddRedisEncryption()`, because `Noelia.Redis` ships the only implementation of
`IDataEncryptionService` — and stored what
`EncryptionResult.EncryptedData` returned.

Nobody else. `SecretManager`, `KeyManagementService` and `FileBasedProvider`
each do their own encryption and are not affected by this. If `AddEncryption`
is not in your composition root, nothing here applies to you.

**What was wrong.** `DataEncryptionService.EncryptAesGcmAsync` copied the
plaintext into the result buffer under a comment reading *"simplified - in
production use proper GCM implementation"*, wrote an authentication tag of
sixteen zero bytes, drew an IV and never used it, and wrapped all of it in a
JSON envelope declaring `"Algorithm":"AES256GCM"`. Beside it, in
`IntegrityHash`, sat a SHA-256 **of the plaintext** — a guessing oracle for
anyone who could read the store. A key belonging to someone else decrypted the
same envelope and reported `Success = true`, `IntegrityVerified = true`.

`EncryptionResult.Success` was `true` throughout. Nothing in the API said
otherwise.

**What it means.** Every value you stored through this API is plaintext,
wherever you put it: a column, a cache, a backup, an export. Confidentiality
and integrity were both absent, not merely weakened. There was no privilege
escalation and no remote attack vector — the defect did not let anyone in, it
failed to protect what was already reachable.

**What to do.**

1. **Upgrade to 4.4.2.** No API changed shape; it includes the 4.4.1 cipher fix
   and authenticates the full envelope.
2. **Rotate the values.** Treat anything stored this way as disclosed to
   everyone who had read access to that store and to every copy of it. Issue
   new keys, new tokens, new credentials. Encrypting an exposed value again
   does not un-expose it.
3. **Re-encrypt what you keep.** 4.4.2 refuses a pre-4.4.1 envelope — envelope
   version `"1.0"` — and says so in the error, rather than returning data it
   cannot authenticate. Read those values with 4.4.0, write them back under
   4.4.2.
4. **Re-examine anything you decided on `IntegrityVerified`.** Before 4.4.1 it
   was `true` for a foreign key and for altered ciphertext, and `DecryptionResult`
   defaulted it to `true` on every failure path. It now defaults to `false`.

**What changed in the code.** Real AES-256-GCM through
`System.Security.Cryptography.AesGcm` — no new dependency. A nonce drawn per
operation from the OS CSPRNG and stored; the authentication tag produced by the
cipher; no digest of the plaintext anywhere; a full 16-byte tag required on
read, so a truncated one is refused. `EncryptionOptions.AdditionalData`, read by
nothing until now, is bound into the tag. `CompressBeforeEncryption` compresses
instead of returning its input while recording `compressed=true`.

**Algorithms Noelia does not implement are now refused rather than
substituted.** `ChaCha20Poly1305`, `XChaCha20Poly1305`, `AES256CBC` and
`AES128CBC` used to fall through to the AES branch and come back stamped
`AES256GCM`. They now return `Success = false` naming the algorithm — the same
way `HashAsync` has refused the memory-hard hashes it does not ship since 4.3.

**Why the tests did not catch it**, which is the part worth keeping:
`DataEncryptionServiceTests` asserted `Success`, the error strings and a round
trip, and the only thing it ever claimed about the ciphertext was
`EncryptedData.Should().NotBeNullOrEmpty()`. **A round trip is trivially green
when nothing happens to the data on the way there and back.** 4.4.1 adds
`DataEncryptionServiceCipherTests`, which asserts against the ciphertext
instead: a foreign key must fail, one flipped bit must be caught, the same
plaintext twice must give different ciphertexts, and the plaintext must not
occur as a byte subsequence anywhere in the raw result. The last of those is
the one that would have found this.

## Upgrading to 4.2.3

`Shape.Of` and `LoggingBehavior` no longer fail a request they cannot describe.
A command carrying `ReadOnlyMemory<byte>` used to 500 before the handler ran:
reflection cannot invoke a getter that returns a ref struct (`Span`,
`ReadOnlySpan`), and the catch only covered `TargetInvocationException`. Those
getters are now named, not invoked, and a shape that still fails is omitted —
the handler still runs.

## Upgrading to 4.2.2

`X-RateLimit-Limit` and `X-RateLimit-Remaining` are now on the **429** as well.
They used to go on the allowed answer only, so the one response where a caller
most needs to read the limit — and see that nothing is left — was the one
without them.

## Upgrading to 4.2.1

Two defects that only showed up when an application stopped rebuilding the
limiter and actually used it.

**Nothing is exempt by default any more.** `WhitelistedIps` held loopback and
`WhitelistedEndpoints` held the health paths, and **neither could be removed from
configuration**: the .NET binder adds to a collection and never replaces it, and
an empty JSON array is indistinguishable from an absent key. Measured — with
`"WhitelistedIps": ["9.9.9.9"]` the bound value was `127.0.0.1, ::1, 9.9.9.9`. An
operator who wrote the list out deliberately still got the exemption, and nothing
in their own configuration would have told them. A default nobody can remove is a
trap, and this one hid in the place a rate limit is first tried out: your own
machine, where it then looks as though nothing counts.

**Health checks now run before rate limiting in the default chain.** They used to
run after, so the liveness probe went through the limiter and was saved only by
that unremovable whitelist entry. A braked liveness probe takes the container out
of the load balancer, which makes the limiter itself the outage. Order is the
honest place for that, not a path string.

If you relied on either default, name it yourself — and check that your own
chain puts the health endpoints first.

## Upgrading to 4.2

Nothing to rewrite. One new package and two things that were always possible and
never said.

### `Noelia.Http` — take the pipeline without the engine

`Noelia.Infrastructure` carries **44** transitive packages: Swashbuckle,
OpenTelemetry, nine Serilog packages, JWT bearer, TOTP, FluentValidation. Right
for a service running the whole default set; wrong for a gateway that only
routes and wants a correlation id.

`Noelia.Http` carries **none**. It holds `CorrelationIdMiddleware`,
`DistributedRateLimitingMiddleware`, `ClientAddress`, `InProcessRateLimitStore`
and their options, and depends on the shared framework and `Noelia.Abstractions`
and nothing else.

**The namespaces did not change.** They are still `Noelia.Infrastructure.*`,
which reads oddly in a package called `Noelia.Http` and is deliberate: moving
types between assemblies keeps every `using` compiling, renaming the namespace
would break the source of every caller. `Noelia.Infrastructure` references it, so
`AddNoelia` and `UseNoelia` are unchanged and nobody has to do anything.

This exists because a gateway looked at the cost of taking Noelia's two
middlewares and wrote its own instead. A library whose own use is the expensive
path has failed at the thing it is for.

### Rate limiting: brake a named set of paths and nothing else

Always possible, never documented, so it got rebuilt by hand. A limit of `0`
writes no counter, and a request counted against nothing is allowed:

```json
"DistributedRateLimiting": {
  "RequestsPerMinute": 0, "RequestsPerHour": 0, "RequestsPerDay": 0,
  "EndpointSpecificLimits": {
    "/auth/login":    { "RequestsPerMinute": 20 },
    "/auth/register": { "RequestsPerMinute": 5 }
  }
}
```

Only the two named paths count. That is the shape a gateway needs — the whole
user interface travels through it, and a default over everything would count
each asset fetch. It is now held by tests, which is what makes it an offer
rather than an accident.

`LimitMultiplier` lifts every ceiling for an environment where a test suite
hammers itself. A factor and not a switch: the limiter still counts, still keys
per subject, still answers with its headers. A limiter switched off is invisible
in the one environment that runs continuously. A limit of `0` stays off — zero
times anything is still off, not a small limit.

The 429 now carries `X-Content-Type-Options` and `X-Frame-Options` itself. It
writes the response and returns, so nothing further down the chain reached it.

### One over-match fixed

`$(` no longer counts as command substitution on its own — it has to name a
command. Measured against realistic text on a job board, the old rule refused
`$(document).ready()`, and a portfolio is exactly where people describe the code
they wrote.

## Upgrading to 4.1

Nothing to rewrite. Six behaviours change, each because the old one was wrong in
a way you could not see from outside.

| Was | Is |
|---|---|
| `UseSharedInfrastructure(env, name)` unconditionally called every step, so `UseDefaults()` plus the default chain **died at startup** | the chain skips a step whose module was left out. `Without(module, reason)` decides once, on the service side |
| `Authorization` was one module, so a service with any public surface had to choose between no policy provider and a blanket 401 | two: `Authorization` (the policy provider) and `PermissionEnforcement` (the pipeline step). Both in the default set |
| `RateLimiting` was in the default set and registered no counter | it brings an in-process one; `AddRedisCache`/`AddInMemoryCache` replaces it |
| `EndpointSpecificLimits` arrived holding seven paths from another application, and configuration could only add to them | empty |
| the 429 was `application/json` with a `traceId` | `application/problem+json`, naming the correlation id (and still the trace id) |
| input sanitization matched bare SQL keywords and single punctuation marks, treated `Referer` as input, and never looked in JSON bodies | matches injection syntax; ignores `Referer`; inspects JSON string values and refuses over them (`InspectJsonBodies`), leaving accepted bodies untouched |
| its refusal was `application/json` with an `error` string and no id | `application/problem+json`, naming the correlation id |
| `LdapInjection` had a name in the enum and no expression — it was caught by the rule that matched every parenthesis | a real filter-breakout detector |
| `UserClaims.EmailVerified`/`AccountStatus` defaulted to `false`/`"Active"`, so `EmailVerified` refused everyone and `ActiveAccount` admitted everyone | `bool?`/`string?` with no default. Unset means the claim is not written, and both policies refuse for want of an answer |
| a list-valued custom claim was impossible | `UserClaims.CustomClaimArrays` writes one, as a JSON array |
| `ClientAddress.Of` gave `10.0.0.1` and `::ffff:10.0.0.1` separate buckets | IPv4-mapped addresses are normalised |

**The one to look at before upgrading** is `AccountStatus`. If your composition
root never set it, every token you issued said `"Active"` — so a policy on
`ActiveAccount` was letting suspended and deleted accounts through, and it will
start refusing them. That is the fix, not a regression; set the field where you
know the answer.

## Upgrading to 4.4

| Was | Is |
|---|---|
| masking matched names as a substring | exact match against `SensitiveFieldNames`, the one shared list |
| `SecretName`, `TokenId`, `TokenEndpoint` masked | visible — a name is not a value |
| `Username`, `Email`, `City` visible in logs | masked, because the shared list says they are personal data |
| two masks: `***` and `[REDACTED]` | one: `[REDACTED]` |
| audit hash joined fields with `|` | each field written with its length, so shifting content changes the hash |
| chain advanced under a lock, sink written outside | both in one serialised turn |
| `AddSovereignPlatform` always allowed loopback and RFC1918 | `WithoutLoopback()`, `WithoutPrivateNetworks()` |
| `AddSovereignPlatform` registered past the composition | `NoeliaModule.SovereignPlatform`, droppable with a reason |

Full detail, and every version back to 4.0, in [MIGRATION.md](MIGRATION.md).

## Upgrading to 4.0

| Was | Is |
|---|---|
| `AddSharedInfrastructure(config, env, name)` | `AddNoelia(config, env, name, g => g.UseDefaults())` |
| `AddSharedInfrastructure(…, infra => …)` — **replaced** the default | `AddNoelia(…, g => g.UseDefaults().Without(module, reason))` |
| `UseSharedInfrastructure(env, name[, pipeline])` | `UseNoelia(env, name[, pipeline])` — the old name forwards, with an `[Obsolete]` |
| `X-Forwarded-For` believed by default | believed only from `TrustForwardedHeadersFrom(...)` |
| `X-Real-IP` read | not read; set `ForwardedForHeaderName` if a proxy sends only that |
| three rate limiters, two of which did not brake | one |
| `IRateLimitService`, `ConfigureRateLimitRules` | removed; counting goes through the cache |
| `EnableIpRateLimiting` / `EnableUserRateLimiting` / `ClientIdStrategy` | `RateLimitSubject`, read on every request |
| `GetAsync<T>` returned `T?`, non-2xx became `null` | returns `ServiceResponse<T>` with status and body |
| every non-success retried three times | only `408`, `429`, `5xx` except `501` |
| correlation id only via the manager or MassTransit | on every `HttpClient` the factory builds |

Full detail, with the two entry points side by side, in
[MIGRATION.md](MIGRATION.md).

## Upgrading to 3.0

Five changes, all in [MIGRATION.md](MIGRATION.md).

| | |
|---|---|
| `AddCQRS()` | registers the cache behaviours only where something implements `ICacheableQuery` or `ICacheInvalidatingCommand`, and requires a cache where it does. A query marked cacheable with no cache registered was never cached; it now refuses to start instead |
| `CacheInvalidationBehavior` | no longer takes `IETagGenerator`, so a command pipeline no longer requires `AddHttpResponseCaching()`. ETags are cleared through the cache it already holds |
| `IETagGenerator`, `ProviderRequirement`, `ProviderRequirements` | moved. `IETagGenerator` to `Noelia.Infrastructure.Caching.Http`; the other two to `Noelia.Abstractions.Hosting`. `InfrastructureBuilder.RequiresProvider<T>()` is unchanged |
| `AddHttpResponseCaching()` | requires an `IDistributedCacheService` and says so at startup. `ETagGenerator` takes one as a mandatory constructor parameter — an ETag store with nowhere to store is not one |
| `Noelia.InMemory` pattern invalidation | applies the store's key prefix to the pattern. With a prefix configured — and `AddInMemoryCache` always configures one — it previously matched nothing and removed no keys |

## Upgrading to 2.0

Three removals and one changed default; everything else is unchanged.
[MIGRATION.md](MIGRATION.md) has the before and after for each.

| | |
|---|---|
| `JwtSettings.ExpireMinutes` | 60 → **15** minutes. Set it back in configuration if you want the old window |
| `UserClaims.FirstName` / `.LastName` | removed. Never written into a token, and demanding them refused one to anyone whose name does not split in two |
| `IJwtService.GenerateRefreshTokenAsync()` | removed, with `TokenResult.RefreshToken`. It produced a token stored nowhere and validated by nothing |

## Package licensing

MediatR, MassTransit and FluentAssertions are pinned to their last Apache-2.0
releases. Later versions are commercially licensed, so upgrading them is a
licensing decision rather than a routine version bump. See
`Directory.Packages.props`.

Four OpenTelemetry contrib instrumentation packages have no stable release and
are pinned to prereleases.

## Consuming Noelia

5.0.0 was the first stable release under the Noelia identity; the current
version is 6.3.0. Consumers install anonymously from NuGet.org:

```bash
dotnet add package Noelia.Infrastructure --version 6.3.0
```

Reference only what the service actually runs:

```xml
  <PackageReference Include="Noelia.Infrastructure" Version="6.3.0" />
  <PackageReference Include="Noelia.Redis" Version="6.3.0" />
  <PackageReference Include="Noelia.Data.EntityFrameworkCore" Version="6.3.0" />
```

A service that speaks to no broker leaves out `Noelia.Messaging.MassTransit`
and never sees MassTransit. That is the point of the split.

The complete local acceptance evidence for all thirteen packages, both
architectures, dependency audits and the secret-canary scan is in
[RELEASE-GATE-5.0.md](docs/RELEASE-GATE-5.0.md). Publication still requires a
merged commit and green GitHub CI; a local pass is not a release.

### Releasing

Publishing runs from a GitHub release, or manually via **Actions → Publish**
with a version. Either way the workflow builds and **runs the full test suite
before pushing** — a release tag points at a commit, not at a green run.

`5.0.0` means the ports are settled: a breaking change to any of them raises
the major version. Symbols and Source Link are included, so a debugger steps
into Noelia source at the exact commit a package was built from.
