using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security;
using Noelia.Abstractions.Security.Checks;
using Noelia.Abstractions.Security.Encryption;
using Noelia.Abstractions.Security.Secrets;
using Noelia.Infrastructure.Models;
using Noelia.Infrastructure.Security.Headers;
using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Security.Checks;

internal abstract class SecurityCheckBase : ISecurityCheck
{
    public abstract string Id { get; }
    public abstract NoeliaModule Module { get; }
    public abstract SecurityCheckCategory Category { get; }
    public abstract SecurityCheckSeverity Severity { get; }
    public abstract string Remediation { get; }
    public abstract Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default);

    protected SecurityCheckResult Result(
        SecurityCheckStatus status,
        string summary,
        SecurityCheckSeverity? severity = null) =>
        SecurityCheckResultFactory.Result(this, status, summary, severity);
}

internal sealed class CompositionSecurityCheck(
    IServiceProvider services,
    NoeliaComposition composition) : SecurityCheckBase
{
    public override string Id => "noelia.composition.providers";
    public override NoeliaModule Module => NoeliaModule.Composition;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.Critical;
    public override string Remediation =>
        "Install and register one of the provider packages named by each unmet module requirement.";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var probe = services.GetService<IServiceProviderIsService>();
        if (probe is null)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Warning,
                "The container cannot prove whether declared providers are registered.",
                SecurityCheckSeverity.High));
        }

        var unmet = composition.Contracts.Values
            .SelectMany(contract => contract.Requirements)
            .Where(requirement => !probe.IsService(requirement.ServiceType))
            .Select(requirement => requirement.ServiceType)
            .Distinct()
            .Count();

        return Task.FromResult(unmet == 0
            ? Result(SecurityCheckStatus.Pass, "Every active module requirement has a registered provider.")
            : Result(SecurityCheckStatus.Fail, "One or more active module requirements have no provider."));
    }
}

internal sealed class ReadinessCoverageSecurityCheck(
    IServiceProvider services,
    NoeliaComposition composition) : SecurityCheckBase
{
    public override string Id => "noelia.health.readiness-coverage";

    /// <summary>
    /// Reported against the composition, not against HealthChecks.
    /// </summary>
    /// <remarks>
    /// The finding is "this composition exposes readiness and registers nothing
    /// for it", which is a statement about the whole arrangement and has to
    /// reach an operator whether or not the module is in it. A check filed
    /// under a module the service does not run would be filtered out of the
    /// dashboard by the very absence it is reporting.
    /// </remarks>
    public override NoeliaModule Module => NoeliaModule.Composition;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "Register a readiness check for every backing service this one cannot serve without: "
        + "AddDatabase<TContext>(...) from Noelia.Data.EntityFrameworkCore, "
        + "AddRedisConnection(...) from Noelia.Redis, "
        + "UseMassTransitMessaging(...) from Noelia.Messaging.MassTransit, "
        + "or your own through the Checks builder.";

    /// <summary>
    /// Reports a readiness endpoint that answers for nothing.
    /// </summary>
    /// <remarks>
    /// <c>/health/ready</c> filters the registered checks by the <c>ready</c>
    /// tag. With none registered the filtered set is empty, an empty report is
    /// <see cref="HealthStatus.Healthy"/>, and the endpoint answers 200 — over
    /// an unreachable database, an unreachable cache and an unreachable broker
    /// alike. Nothing about that response distinguishes "everything this
    /// service depends on is up" from "this service checks nothing", and an
    /// orchestrator routing traffic on it cannot tell either.
    /// </remarks>
    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        // Composition, not resolvability: IOptions<T> resolves to an empty
        // default whether or not anyone asked for health checks, so asking the
        // container would report an uncovered readiness endpoint for a service
        // that exposes none.
        if (!composition.Included.Contains(NoeliaModule.HealthChecks))
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.NotApplicable,
                "This composition exposes no readiness endpoint."));
        }

        // Not GetRequiredService: AddHealthChecks() without a single AddCheck
        // leaves IOptions<HealthCheckServiceOptions> unregistered, and that is
        // exactly the composition this check was written for. Demanding the
        // service would turn the finding into a crash inside the check that
        // reports it.
        var options = services.GetService<IOptions<HealthCheckServiceOptions>>();

        var ready = options?.Value.Registrations
            .Count(registration => registration.Tags.Contains("ready")) ?? 0;

        return Task.FromResult(ready == 0
            ? Result(
                SecurityCheckStatus.Fail,
                "Readiness answers 200 without checking anything; no registration carries the 'ready' tag.")
            : Result(
                SecurityCheckStatus.Pass,
                $"Readiness covers {ready} registered check(s)."));
    }
}

internal sealed class DataProtectionKeyRingSecurityCheck(
    IServiceProvider services) : SecurityCheckBase
{
    public override string Id => "noelia.dataprotection.key-ring";
    public override NoeliaModule Module => NoeliaModule.Composition;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.Medium;
    public override string Remediation =>
        "Call UseDataProtection(applicationName) to keep the key ring in the registered "
        + "cache provider and encrypt it with the registered encryption provider.";

    /// <summary>
    /// Reports a key ring that does not outlive the process or is stored in the
    /// clear.
    /// </summary>
    /// <remarks>
    /// ASP.NET writes two warnings about this at every start and then carries
    /// on, which is the shape of a problem nobody acts on. What it costs is
    /// invisible until it is not: anything protected with the ring — an
    /// authentication cookie, an antiforgery token, a reset link — stops
    /// verifying when the container is replaced, and a second replica never
    /// verifies what the first one issued.
    /// </remarks>
    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var probe = services.GetService<IServiceProviderIsService>();
        if (probe?.IsService(typeof(IDataProtectionProvider)) != true)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.NotApplicable,
                "This composition has no data protection key ring."));
        }

        var options = services
            .GetService<IOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>>();

        var persisted = options?.Value.XmlRepository is not null;
        var encrypted = options?.Value.XmlEncryptor is not null;

        return Task.FromResult((persisted, encrypted) switch
        {
            // "through the registered provider", not "outside the process":
            // whether that provider is durable is the provider's property and
            // is already visible in the composition. Claiming durability here
            // would pass an in-process cache off as a shared store.
            (true, true) => Result(
                SecurityCheckStatus.Pass,
                "The key ring is stored through the registered cache provider and encrypted "
                + "with the registered encryption provider."),
            (false, _) => Result(
                SecurityCheckStatus.Fail,
                "The key ring is written to this container's filesystem; anything protected "
                + "with it stops verifying when the container is replaced."),
            _ => Result(
                SecurityCheckStatus.Fail,
                "The key ring is stored through a provider but is not encrypted at rest.")
        });
    }
}

internal sealed class AuditChainScopeSecurityCheck(
    IServiceProvider services) : SecurityCheckBase
{
    public override string Id => "noelia.audit.chain-scope";
    public override NoeliaModule Module => NoeliaModule.Composition;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.Medium;
    public override string Remediation =>
        "Run one replica, or treat each replica's chain as its own sequence when verifying. "
        + "A shared sovereign chain across replicas is not available yet.";

    /// <summary>
    /// Says out loud that the sovereign audit chain belongs to this process.
    /// </summary>
    /// <remarks>
    /// <c>AuditTrailService</c> advances its chain from a field
    /// it holds itself. That is correct for one replica and silently wrong for
    /// two: both start from their own head, and a verifier reading the store
    /// back finds a broken chain on a system where nothing was tampered with.
    /// <para>
    /// The security audit trail in <c>Noelia.Redis</c> already solves this with
    /// a compare-and-set on a shared head, so the shape of the answer is known.
    /// Until the sovereign trail has the same, an operator should be told which
    /// of the two they are running rather than discovering it during an
    /// investigation.
    /// </para>
    /// </remarks>
    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var probe = services.GetService<IServiceProviderIsService>();
        if (probe?.IsService(typeof(Noelia.Abstractions.Audit.IAuditTrailService)) != true)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.NotApplicable,
                "This composition keeps no sovereign audit trail."));
        }

        return Task.FromResult(Result(
            SecurityCheckStatus.Warning,
            "The sovereign audit chain is advanced in this process, so each replica keeps "
            + "a chain of its own."));
    }
}

internal sealed class JwtSecurityCheck(
    IServiceProvider services,
    IHostEnvironment environment) : SecurityCheckBase
{
    public override string Id => "noelia.jwt.key-separation";
    public override NoeliaModule Module => NoeliaModule.Jwt;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Authentication;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "Use VerifyOnly(publicKey, keyId), Issue(privateKey, keyId), or an HTTPS OpenID Connect authority.";

    public override async Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var ring = services.GetService<KeyRing>();
        if (ring is not null)
        {
            if (ring.ValidationKeys.Any(key => !key.SeparatesIssuingFromVerifying))
            {
                return environment.IsDevelopment()
                    ? Result(SecurityCheckStatus.Warning,
                        "A shared verification secret also grants token-issuing power.")
                    : Result(SecurityCheckStatus.Fail,
                        "A shared verification secret also grants token-issuing power.");
            }

            if (ring.ValidationKeys.Any(key => string.IsNullOrWhiteSpace(key.Kid)))
            {
                return Result(
                    SecurityCheckStatus.Warning,
                    "A verification key has no rotation identifier.",
                    SecurityCheckSeverity.Medium);
            }

            return Result(SecurityCheckStatus.Pass, "Token verification uses separated asymmetric key material.");
        }

        var schemeProvider = services.GetService<IAuthenticationSchemeProvider>();
        var options = services.GetService<IOptionsMonitor<JwtBearerOptions>>();
        if (schemeProvider is null || options is null)
        {
            return Result(SecurityCheckStatus.Fail, "No token verification source is registered.");
        }

        var jwtSchemes = (await schemeProvider.GetAllSchemesAsync())
            .Where(scheme => scheme.HandlerType == typeof(JwtBearerHandler))
            .ToArray();

        if (jwtSchemes.Length == 0)
        {
            return Result(SecurityCheckStatus.Fail, "No JWT bearer scheme is registered.");
        }

        var authorities = jwtSchemes
            .Select(scheme => options.Get(scheme.Name).Authority)
            .Where(authority => !string.IsNullOrWhiteSpace(authority))
            .ToArray();

        if (authorities.Length == 0)
        {
            return Result(SecurityCheckStatus.Fail, "JWT bearer authentication has no verifiable key source.");
        }

        var insecureAuthority = authorities.Any(authority =>
            !Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps);

        return insecureAuthority && !environment.IsDevelopment()
            ? Result(SecurityCheckStatus.Fail, "The OpenID Connect authority is not an HTTPS origin.")
            : Result(SecurityCheckStatus.Pass, "JWT bearer authentication follows a declared key authority.");
    }
}

internal sealed class SecurityHeadersSecurityCheck(
    IOptions<SecurityHeadersOptions> headers,
    IOptions<SecurityHeadersMiddlewareOptions> middleware,
    IHostEnvironment environment) : SecurityCheckBase
{
    public override string Id => "noelia.headers.browser-baseline";
    public override NoeliaModule Module => NoeliaModule.SecurityHeaders;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Browser;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "Enable the security-headers middleware, default CSP, and production HSTS.";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!middleware.Value.EnableSecurityHeaders || !headers.Value.EnableDefaultCsp)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Fail,
                "The browser security-header baseline is disabled."));
        }

        if (!environment.IsDevelopment()
            && (!headers.Value.EnableHsts || headers.Value.HstsMaxAge < 15_552_000))
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Fail,
                "Production HSTS is disabled or shorter than six months."));
        }

        return Task.FromResult(Result(
            SecurityCheckStatus.Pass,
            "CSP and the environment-appropriate security-header baseline are enabled."));
    }
}

internal sealed class RefreshCookieSecurityCheck(IServiceProvider services) : SecurityCheckBase
{
    public override string Id => "noelia.sessions.refresh-cookie";
    public override NoeliaModule Module => NoeliaModule.TokenSessions;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Session;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "Set refresh cookies to HttpOnly, SecurePolicy.Always, and Lax or Strict SameSite where the client flow permits it.";

    public override async Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var schemes = services.GetService<IAuthenticationSchemeProvider>();
        var options = services.GetService<IOptionsMonitor<CookieAuthenticationOptions>>();
        if (schemes is null || options is null)
        {
            return Result(
                SecurityCheckStatus.NotApplicable,
                "No cookie authentication scheme is registered.",
                SecurityCheckSeverity.Informational);
        }

        var cookieSchemes = (await schemes.GetAllSchemesAsync())
            .Where(scheme => scheme.HandlerType == typeof(CookieAuthenticationHandler))
            .ToArray();
        if (cookieSchemes.Length == 0)
        {
            return Result(
                SecurityCheckStatus.NotApplicable,
                "No cookie authentication scheme is registered.",
                SecurityCheckSeverity.Informational);
        }

        var cookies = cookieSchemes.Select(scheme => options.Get(scheme.Name).Cookie).ToArray();
        if (cookies.Any(cookie => !cookie.HttpOnly || cookie.SecurePolicy != CookieSecurePolicy.Always))
        {
            return Result(SecurityCheckStatus.Fail, "A session cookie is readable by script or may travel without TLS.");
        }

        if (cookies.Any(cookie => cookie.SameSite == SameSiteMode.None))
        {
            return Result(
                SecurityCheckStatus.Warning,
                "A session cookie permits cross-site sending.",
                SecurityCheckSeverity.Medium);
        }

        return Result(SecurityCheckStatus.Pass, "Session cookies are HttpOnly, TLS-only, and same-site constrained.");
    }
}

internal sealed class CorsSecurityCheck(
    IOptions<CorsOptions> options,
    IHostEnvironment environment) : SecurityCheckBase
{
    public override string Id => "noelia.cors.credentialed-origins";
    public override NoeliaModule Module => NoeliaModule.Cors;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Browser;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "List exact trusted HTTPS origins and never combine credentials with a wildcard origin.";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var policy = options.Value.GetPolicy(options.Value.DefaultPolicyName);
        if (policy is null)
        {
            return Task.FromResult(Result(SecurityCheckStatus.Fail, "No default CORS policy is registered."));
        }

        if (policy.Origins.Contains("*", StringComparer.Ordinal)
            || (policy.SupportsCredentials && policy.AllowAnyOrigin))
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Fail,
                "The CORS policy permits a wildcard origin for browser credentials."));
        }

        var localOrigin = policy.Origins.Any(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback);
        if (localOrigin && !environment.IsDevelopment())
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Warning,
                "A production CORS policy includes a loopback origin.",
                SecurityCheckSeverity.Medium));
        }

        return Task.FromResult(Result(SecurityCheckStatus.Pass, "CORS is constrained to explicit origins."));
    }
}

internal sealed class SecretProviderSecurityCheck(
    IServiceProvider services,
    IHostEnvironment environment) : SecurityCheckBase
{
    public override string Id => "noelia.secrets.provider";
    public override NoeliaModule Module => NoeliaModule.SecretManagement;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Secrets;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "Register a durable secret provider such as OpenBao; keep in-memory providers for tests only.";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var provider = services.GetService<ISecretProvider>();
        if (provider is null)
        {
            return Task.FromResult(Result(SecurityCheckStatus.Fail, "No secret provider is registered."));
        }

        var inMemory = provider.GetType().Name.Contains("InMemory", StringComparison.OrdinalIgnoreCase);
        if (inMemory)
        {
            return Task.FromResult(environment.IsDevelopment()
                ? Result(SecurityCheckStatus.Warning, "Secrets exist only for the lifetime of this process.")
                : Result(SecurityCheckStatus.Fail, "Secrets exist only for the lifetime of this process."));
        }

        return Task.FromResult(Result(SecurityCheckStatus.Pass, "A non-ephemeral secret provider is registered."));
    }
}

internal sealed class EncryptionSecurityCheck(
    IServiceProvider services,
    IOptions<DataEncryptionOptions> options) : SecurityCheckBase
{
    public override string Id => "noelia.encryption.aead";
    public override NoeliaModule Module => NoeliaModule.Encryption;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Encryption;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.Critical;
    public override string Remediation =>
        "Register IDataEncryptionService and a 32-byte master key, and select an authenticated encryption algorithm.";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (services.GetService<IDataEncryptionService>() is null
            || services.GetService<IMasterKeyProvider>() is not { } masterKey)
        {
            return Task.FromResult(Result(SecurityCheckStatus.Fail, "Encryption or its master-key provider is missing."));
        }

        var authenticated = options.Value.DefaultAlgorithm is EncryptionAlgorithm.AES128GCM
            or EncryptionAlgorithm.AES256GCM
            or EncryptionAlgorithm.ChaCha20Poly1305
            or EncryptionAlgorithm.XChaCha20Poly1305;
        if (!authenticated)
        {
            return Task.FromResult(Result(SecurityCheckStatus.Fail, "The configured encryption algorithm is not authenticated."));
        }

        if (masterKey.GetMasterKey().Length != 32)
        {
            return Task.FromResult(Result(SecurityCheckStatus.Fail, "The master key does not have the required shape."));
        }

        return Task.FromResult(Result(SecurityCheckStatus.Pass, "Authenticated encryption and a correctly shaped master key are active."));
    }
}

internal sealed class RateLimitDegradationSecurityCheck(
    IServiceProvider services,
    IOptions<DistributedRateLimitingOptions> options,
    IHostEnvironment environment) : SecurityCheckBase
{
    public override string Id => "noelia.ratelimit.degradation";
    public override NoeliaModule Module => NoeliaModule.RateLimiting;
    public override SecurityCheckCategory Category => SecurityCheckCategory.AbusePrevention;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "Keep rate limiting enabled, use DenyAll on store failure, and use a shared provider for multi-instance production.";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled
            || options.Value.CircuitBreaker.FallbackBehavior == CircuitBreakerFallback.AllowAll)
        {
            return Task.FromResult(Result(SecurityCheckStatus.Fail, "Rate limiting is disabled or fails open."));
        }

        var store = services.GetService<IDistributedRateLimitStore>();
        if (store is null)
        {
            return Task.FromResult(Result(SecurityCheckStatus.Fail, "No rate-limit counter is registered."));
        }

        if (!environment.IsDevelopment()
            && store.GetType().Name.Contains("InProcess", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Warning,
                "Rate-limit counters are local to this process.",
                SecurityCheckSeverity.Medium));
        }

        return Task.FromResult(Result(SecurityCheckStatus.Pass, "Rate limiting is enabled and does not fail open."));
    }
}

internal sealed class RevocationDegradationSecurityCheck(
    IServiceProvider services,
    IHostEnvironment environment) : SecurityCheckBase
{
    public override string Id => "noelia.revocation.degradation";
    public override NoeliaModule Module => NoeliaModule.Jwt;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Revocation;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "Register an in-memory or Redis revocation store and keep unknown-state handling fail closed.";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var evaluator = services.GetService<ITokenRevocationEvaluator>();
        if (evaluator is null)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Warning,
                "Token revocation is not registered.",
                SecurityCheckSeverity.Medium));
        }

        if (evaluator is NoTokenRevocationEvaluator)
        {
            return Task.FromResult(environment.IsDevelopment()
                ? Result(SecurityCheckStatus.Warning, "Token revocation was explicitly disabled.")
                : Result(SecurityCheckStatus.Fail, "Token revocation was explicitly disabled."));
        }

        return Task.FromResult(Result(SecurityCheckStatus.Pass, "A token-revocation evaluator is registered."));
    }
}
