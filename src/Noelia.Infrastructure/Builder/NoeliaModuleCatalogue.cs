using System.Text.Json;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Encryption;
using Noelia.Core.Exceptions;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Logging;
using Noelia.Infrastructure.Middleware;
using Noelia.Infrastructure.Security;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Noelia.Infrastructure.Builder;

/// <summary>
/// What Noelia can set up, in the order it sets it up.
/// </summary>
/// <remarks>
/// The order is the catalogue's, not the caller's: modules read what earlier
/// ones registered, and two lines swapped in a composition root must not change
/// what a service does.
/// </remarks>
internal static class NoeliaModuleCatalogue
{
    /// <summary>Every built-in module, in registration order.</summary>
    internal static IReadOnlyList<NoeliaModuleRegistration> All { get; } =
    [
        Entry(NoeliaModule.Logging, noelia =>
        {
            LoggingConfiguration.ConfigureSerilog(noelia.Configuration, noelia.Environment, noelia.ServiceName);
            noelia.Services.AddSerilog();
        }, contract => contract.Provides<Microsoft.Extensions.Logging.ILoggerFactory>(
            "Noelia.Infrastructure", "Use(NoeliaModule.Logging)")),

        Entry(NoeliaModule.HttpContextAccess,
            noelia => noelia.Services.AddHttpContextAccessor(),
            contract => contract.Provides<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
                "Noelia.Infrastructure", "Use(NoeliaModule.HttpContextAccess)")),

        Entry(NoeliaModule.JsonOptions, noelia =>
        {
            var indented = noelia.Environment.IsDevelopment();

            noelia.Services.ConfigureHttpJsonOptions(options => Camel(options.SerializerOptions, indented));
            noelia.Services.Configure<JsonOptions>(options => Camel(options.SerializerOptions, indented));
        }, contract => contract.Provides<Microsoft.Extensions.Options.IConfigureOptions<JsonOptions>>(
            "Noelia.Infrastructure", "Use(NoeliaModule.JsonOptions)")),

        Entry(NoeliaModule.Jwt, noelia =>
        {
            // No IJwtService here: it takes a KeyRing, and where the keys come
            // from is what UseJwt(...) answers. Registering it unconditionally
            // would put a consumer in the container whose dependency nothing
            // supplies. These two need no key.
            noelia.Services.AddSingleton<ITotpService, TotpService>();
            noelia.Services.AddSingleton<IErrorMessageService, ErrorMessageService>();
        }, contract => contract
            .Provides<ITotpService>("Noelia.Infrastructure", "Use(NoeliaModule.Jwt)")
            .Provides<IErrorMessageService>("Noelia.Infrastructure", "Use(NoeliaModule.Jwt)")),

        Entry(NoeliaModule.SecurityMonitoring,
            noelia => Infrastructure(noelia).AddSecurityMonitoring(),
            contract => contract
                .Requires<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(
                    new("Noelia.InMemory", "UseInMemoryCache(prefix)"),
                    new("Noelia.Redis", "UseRedisCache(prefix)"))
                .Provides<Security.Monitoring.ISecurityAlertService>(
                    "Noelia.Infrastructure", "Use(NoeliaModule.SecurityMonitoring)")),
        Entry(NoeliaModule.Resilience,
            noelia => Infrastructure(noelia).AddResilience(),
            contract => contract
                .Provides<Resilience.ICircuitBreakerFactory>(
                    "Noelia.Infrastructure", "Use(NoeliaModule.Resilience)")
                .Provides<Resilience.IRetryPolicyFactory>(
                    "Noelia.Infrastructure", "Use(NoeliaModule.Resilience)")),
        Entry(NoeliaModule.SecretManagement,
            noelia => Infrastructure(noelia).AddSecretManagement(),
            contract => contract.Requires<Noelia.Abstractions.Security.Secrets.ISecretProvider>(
                new("Noelia.Infrastructure", "AddOpenBaoSecretProvider(...)"),
                new("Noelia.Redis", "AddRedisSecretProvider(...)"),
                new("Noelia.InMemory", "AddInMemorySecretProvider()"))),
        Entry(NoeliaModule.Audit,
            noelia => Infrastructure(noelia).AddAuditLogging(),
            contract => contract.Provides<ISecurityAuditLogger>(
                "Noelia.Infrastructure", "Use(NoeliaModule.Audit)")),
        Entry(NoeliaModule.InputSanitization,
            noelia => Infrastructure(noelia).AddInputSanitization(),
            contract => contract.Provides<Security.InputSanitization.IInputSanitizer>(
                "Noelia.Infrastructure", "Use(NoeliaModule.InputSanitization)")),
        Entry(NoeliaModule.RateLimiting,
            noelia => Infrastructure(noelia).AddDistributedRateLimiting(),
            contract => contract.Provides<Noelia.Abstractions.Caching.IDistributedRateLimitStore>(
                "Noelia.Infrastructure", "Use(NoeliaModule.RateLimiting)")),
        Entry(NoeliaModule.HealthChecks,
            noelia => Infrastructure(noelia).AddHealthChecks(),
            contract => contract.Provides<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>(
                "Noelia.Infrastructure", "Use(NoeliaModule.HealthChecks)")),
        Entry(NoeliaModule.Caching, noelia =>
        {
            // Both in-process, and neither needs a decision from anyone: the
            // memory cache several modules read, and the framework's own
            // IDistributedCache. Registering Redis later replaces the latter,
            // because the last registration of a service is the one that wins.
            noelia.Services.AddMemoryCache();
            noelia.Services.AddDistributedMemoryCache();
        }, contract => contract.Provides<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(
            "Noelia.Infrastructure", "Use(NoeliaModule.Caching)")),
        Entry(NoeliaModule.Observability,
            noelia => Infrastructure(noelia).AddObservability(),
            contract => contract.Provides<Observability.IPerformanceMetrics>(
                "Noelia.Infrastructure", "Use(NoeliaModule.Observability)")),
        Entry(NoeliaModule.SecurityHeaders,
            noelia => Infrastructure(noelia).AddSecurityHeaders(),
            contract => contract.Provides<Security.Headers.ISecurityHeadersService>(
                "Noelia.Infrastructure", "Use(NoeliaModule.SecurityHeaders)")),
        // [RequirePermission] names a "Permission:" policy that only
        // PermissionPolicyProvider answers. Needs nothing else, so it belongs in
        // the default set: a module named Authorization that set up the resource
        // half instead would take that machinery away without saying so.
        Entry(NoeliaModule.Authorization,
            noelia => Infrastructure(noelia).AddAuthorization(),
            contract => contract.Provides<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>(
                "Noelia.Infrastructure", "Use(NoeliaModule.Authorization)")),

        // Registers nothing, on purpose. It is the name under which the pipeline
        // step `UsePermissions()` can be left out — without taking the policy
        // provider above with it. A service with any public surface at all needs
        // exactly that separation: the provider answers [RequirePermission] on the
        // endpoints that carry it, the middleware refuses everything it was not
        // told about.
        Entry(NoeliaModule.PermissionEnforcement, _ => { },
            contract => contract.RegistersNothing(
                "it is the pipeline step UsePermissions() and nothing else; the policies it "
                + "enforces come from Authorization")),

        Entry(NoeliaModule.CorrelationPropagation,
            noelia => noelia.Services.AddCorrelationIdPropagation(),
            contract => contract.Provides<CorrelationIdHandler>(
                "Noelia.Infrastructure", "Use(NoeliaModule.CorrelationPropagation)")),

        Entry(NoeliaModule.ApiDocumentation, noelia =>
        {
            noelia.Services.AddEndpointsApiExplorer();
            noelia.Services.AddSwaggerDocumentation(noelia.ServiceName);
        }, contract => contract.Provides<Swashbuckle.AspNetCore.Swagger.ISwaggerProvider>(
            "Noelia.Infrastructure", "Use(NoeliaModule.ApiDocumentation)")),

        Entry(NoeliaModule.Cors,
            noelia => noelia.Services.AddNoeliaCors(noelia.Configuration, noelia.Environment),
            contract => contract.Provides<Microsoft.AspNetCore.Cors.Infrastructure.ICorsService>(
                "Noelia.Infrastructure", "Use(NoeliaModule.Cors)")),

        // Below the default line: each needs something the service must supply,
        // and a default that refuses to start is not a default.
        Entry(NoeliaModule.ResourceAuthorization,
            noelia => Infrastructure(noelia).AddResourceAuthorization(),
            contract => contract
                .Requires<Noelia.Abstractions.Security.Authorization.IResourceAuthorizationService>(
                    new("Noelia.InMemory", "AddInMemoryResourceAuthorization()"),
                    new("Noelia.Redis", "AddRedisResourceAuthorization()"))),
        Entry(NoeliaModule.HttpResponseCaching,
            noelia => Infrastructure(noelia).AddCaching(),
            contract => contract
                .Requires<Noelia.Abstractions.Caching.IDistributedCacheService>(
                    new("Noelia.InMemory", "UseInMemoryCache(prefix)"),
                    new("Noelia.Redis", "UseRedisCache(prefix)"))
                .Provides<Caching.Http.ICachePolicyProvider>(
                    "Noelia.Infrastructure", "Use(NoeliaModule.HttpResponseCaching)")),
        Entry(NoeliaModule.Communication,
            noelia => Infrastructure(noelia).AddCommunication(),
            contract => contract
                .Requires<Noelia.Abstractions.Messaging.IEventBus>(
                    new NoeliaProviderHint(
                        "Noelia.Messaging.MassTransit", "AddMessaging(configuration, assemblies)"))
                .Requires<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(
                    new("Noelia.Infrastructure", "Use(NoeliaModule.Caching)"),
                    new("Noelia.Redis", "AddRedisConnection(...)"))
                .Provides<Communication.IServiceCommunicationManager>(
                    "Noelia.Infrastructure", "Use(NoeliaModule.Communication)")),
        Entry(NoeliaModule.Encryption,
            noelia => Infrastructure(noelia).AddEncryption(),
            contract => contract
                .Requires<Noelia.Abstractions.Security.Encryption.IDataEncryptionService>(
                    new NoeliaProviderHint("Noelia.Redis", "AddRedisEncryption()"))
                .Requires<Noelia.Abstractions.Security.Encryption.IMasterKeyProvider>(
                    new("Noelia.Infrastructure", "AddConfiguredMasterKey()"),
                    new("Noelia.Infrastructure", "AddSecretStoreMasterKey()"))
                .Provides<Noelia.Abstractions.Security.Encryption.IFieldEncryptionService>(
                    "Noelia.Infrastructure", "Use(NoeliaModule.Encryption)")),
        Entry(NoeliaModule.PasswordHashing,
            noelia => Infrastructure(noelia).AddPasswordHashing(),
            contract => contract.Provides<Noelia.Abstractions.Security.Passwords.IPasswordHasher>(
                "Noelia.Infrastructure", "Use(NoeliaModule.PasswordHashing)")),
        Entry(NoeliaModule.TokenSessions,
            noelia => Infrastructure(noelia).AddTokenSessions(),
            contract => contract
                .Requires<Noelia.Abstractions.Security.Sessions.IRefreshTokenStore>(
                    new("Noelia.InMemory", "UseInMemoryRefreshTokens()"),
                    new("Noelia.Data.EntityFrameworkCore", "AddEntityFrameworkRefreshTokens<TContext>()"))
                .Provides<Noelia.Abstractions.Security.Sessions.ITokenSessionService>(
                    "Noelia.Infrastructure", "Use(NoeliaModule.TokenSessions)")),
        Entry(NoeliaModule.Principal,
            noelia => Infrastructure(noelia).AddPrincipal(),
            contract => contract.Provides<Security.Identity.IPrincipalFactory>(
                "Noelia.Infrastructure", "Use(NoeliaModule.Principal)"))
    ];

    /// <summary>
    /// What <see cref="NoeliaBuilder.UseDefaults"/> asks for.
    /// </summary>
    /// <remarks>
    /// Everything that is safe without further configuration. Provider-backed
    /// modules are deliberately absent because each needs a decision Noelia
    /// must not make on anyone's behalf: a cache, broker, secret store, master
    /// key, hashing implementation, refresh-token store or authorization store.
    /// Including them would mean a default set that refuses to start, which is
    /// not a default.
    /// </remarks>
    internal static IReadOnlyList<NoeliaModule> Defaults { get; } =
    [
        NoeliaModule.Logging,
        NoeliaModule.HttpContextAccess,
        NoeliaModule.JsonOptions,
        NoeliaModule.Jwt,
        NoeliaModule.SecurityMonitoring,
        NoeliaModule.Resilience,
        NoeliaModule.Audit,
        NoeliaModule.InputSanitization,
        NoeliaModule.RateLimiting,
        NoeliaModule.HealthChecks,
        NoeliaModule.Caching,
        NoeliaModule.Observability,
        NoeliaModule.SecurityHeaders,
        NoeliaModule.Authorization,
        NoeliaModule.PermissionEnforcement,
        NoeliaModule.CorrelationPropagation,
        NoeliaModule.ApiDocumentation,
        NoeliaModule.Cors
    ];

    private static NoeliaModuleRegistration Entry(
        NoeliaModule module,
        Action<NoeliaBuilder> register,
        Action<NoeliaModuleContractBuilder>? configureContract = null)
    {
        var contract = new NoeliaModuleContractBuilder(module);
        configureContract?.Invoke(contract);
        return new NoeliaModuleRegistration(module, register, contract.Build());
    }

    /// <summary>
    /// A view of the same container for the module extensions that predate this
    /// builder.
    /// </summary>
    private static InfrastructureBuilder Infrastructure(NoeliaBuilder noelia) =>
        new(noelia.Services, noelia.Configuration, noelia.Environment, noelia.ServiceName);

    private static void Camel(JsonSerializerOptions options, bool indented)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.WriteIndented = indented;
    }
}
