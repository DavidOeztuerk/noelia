using Noelia.Abstractions.Security.Encryption;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Infrastructure.Logging;
using Noelia.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Resilience;
using Noelia.Infrastructure.Security.Encryption;
using Noelia.Infrastructure.Security.InputSanitization;
using Noelia.Infrastructure.HealthChecks;
using Noelia.Infrastructure.Caching;
using Noelia.Infrastructure.Communication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Noelia.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noelia.Core.Exceptions;
using Noelia.Infrastructure.Security.Monitoring;
using Noelia.Infrastructure.Caching.Http;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Builder.Modules;

namespace Noelia.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
  /// <summary>
  /// The fixed set of modules Noelia set up before <c>AddNoelia</c> existed.
  /// </summary>
  /// <remarks>
  /// Kept for services that have not moved yet. It decides thirteen modules and
  /// six further registrations on your behalf and gives no way to depart from
  /// them, which is what <c>AddNoelia</c> exists to fix:
  /// <code>
  /// services.AddNoelia(configuration, environment, name, noelia => noelia.UseDefaults());
  /// </code>
  /// The two are not identical. <c>UseDefaults()</c> leaves out the modules that
  /// cannot start on their own — response caching, communication and encryption
  /// each need a provider — so a service moving across adds the ones it uses
  /// with <c>Use(...)</c> and registers what they need.
  /// </remarks>
  public static IServiceCollection AddSharedInfrastructure(
      this IServiceCollection services,
      IConfiguration configuration,
      IHostEnvironment environment,
      string serviceName)
  {
    // ═══════════════════════════════════════════════════════════════
    // NON-MODULAR SETUP (not covered by builder modules)
    // ═══════════════════════════════════════════════════════════════

    LogConfigurationSources(configuration, environment, serviceName);

    // Core Security Services (service registrations, NOT the auth scheme)
    services.AddScoped<IJwtService, JwtService>();
    services.AddSingleton<ITotpService, TotpService>();

    // Error Handling Services
    services.AddSingleton<IErrorMessageService>(p =>
      new ErrorMessageService(text: p.GetService<IErrorTextProvider>()));

    // Token revocation is opt-in: register a store (Noelia.Redis, Noelia.InMemory)
    // or AddNoTokenRevocation(rationale). UseTokenRevocation() refuses to build
    // a pipeline without one.

    // Configure Serilog
    LoggingConfiguration.ConfigureSerilog(configuration, environment, serviceName);
    services.AddSerilog();

    // Swagger Documentation
    services.AddEndpointsApiExplorer();
    services.AddSwaggerDocumentation(serviceName);

    // ═══════════════════════════════════════════════════════════════
    // MODULAR SETUP (delegated to builder modules)
    // NOTE: Role-based authorization (AddNoeliaAuthorization) and
    // Compliance are activated separately by services that need them.
    // Resource-based authorization is included here because its handlers
    // and policies are dormant unless endpoints explicitly use resource
    // policies (ResourceRead, ResourceOwner, etc.).
    // ═══════════════════════════════════════════════════════════════

    services.AddSharedInfrastructure(configuration, environment, serviceName, infra =>
    {
      infra.AddSecurityMonitoring();
      infra.AddResilience();
      infra.AddSecretManagement();
      infra.AddAuditLogging();
      infra.AddEncryption();
      infra.AddInputSanitization();
      infra.AddDistributedRateLimiting();
      infra.AddHealthChecks();
      infra.AddCommunication();
      infra.AddCaching();
      infra.AddObservability();
      infra.AddSecurityHeaders();
      infra.AddResourceAuthorization();
    });

    // ═══════════════════════════════════════════════════════════════
    // REMAINING NON-MODULAR SETUP
    // ═══════════════════════════════════════════════════════════════

    // JSON serialization — camelCase for APIs
    services.ConfigureHttpJsonOptions(options =>
    {
      options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
      options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
      options.SerializerOptions.WriteIndented = environment.IsDevelopment();
    });
    services.Configure<JsonOptions>(options =>
    {
      options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
      options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
      options.SerializerOptions.WriteIndented = environment.IsDevelopment();
    });

    // HTTP context accessor for correlation ID
    services.AddHttpContextAccessor();

    return services;
  }

  /// <summary>
  /// Composes infrastructure modules directly, one at a time.
  /// </summary>
  /// <remarks>
  /// <strong>This replaces the default rather than adjusting it.</strong> What
  /// is not named here is not set up — not the modules the other overload adds,
  /// and not the things that sit outside the module system either: Serilog,
  /// Swagger, the CORS policy, the JSON conventions, the <c>HttpContext</c>
  /// accessor. A service that took this overload to drop one module dropped
  /// eighteen, and nothing said so.
  /// <para>
  /// If what you meant was "the usual set, minus one thing", that is
  /// <c>AddNoelia</c>:
  /// <code>
  /// services.AddNoelia(configuration, environment, name, noelia => noelia
  ///     .UseDefaults()
  ///     .Without(NoeliaModule.Communication, "no broker on this service"));
  /// </code>
  /// </para>
  /// <para>
  /// This overload remains as the layer below that: it composes
  /// <see cref="InfrastructureBuilder"/> modules and nothing else, which is what
  /// <c>AddNoelia</c>'s own catalogue is built from.
  /// </para>
  /// </remarks>
  public static IServiceCollection AddSharedInfrastructure(
      this IServiceCollection services,
      IConfiguration configuration,
      IHostEnvironment environment,
      string serviceName,
      Action<InfrastructureBuilder> configure)
  {
    var builder = new InfrastructureBuilder(services, configuration, environment, serviceName);
    configure(builder);
    return services;
  }

  /// <summary>
  /// The pipeline half of <c>AddNoelia</c>: every step, in Noelia's order, and
  /// only the ones this service asked for.
  /// </summary>
  /// <remarks>
  /// <para>The order is Noelia's, and that is the reason to call this rather
  /// than write the chain out. Steps read what earlier ones established — the
  /// principal needs authentication to have run, the audit trail needs the
  /// principal, the rate limiter has to sit outside authentication — and a
  /// composition root that lists them itself owns that order from then on.</para>
  ///
  /// <para><strong>A step whose module was left out is skipped.</strong>
  /// <c>Without(module, reason)</c> on the service side now takes effect here
  /// too, read from the <see cref="Noelia.Abstractions.Hosting.NoeliaComposition"/>
  /// the container carries. Before, leaving out <c>RateLimiting</c> and then
  /// calling this threw at startup — <c>UseRateLimiting() needs
  /// IDistributedRateLimitStore</c> — and the way out was to copy this chain
  /// into the caller's own root, minus a line. That copy is silently wrong the
  /// first time Noelia adds a step.</para>
  ///
  /// <para>Two steps are in the chain that the default module set does not ask
  /// for, and they cost nothing until it does:
  /// <see cref="Noelia.Abstractions.Hosting.NoeliaModule.HttpResponseCaching"/>
  /// and — for a service that leaves it out — permission enforcement.</para>
  /// </remarks>
  /// <param name="app">The application being built.</param>
  /// <param name="environment">Where it runs.</param>
  /// <param name="serviceName">What the service calls itself.</param>
  public static IApplicationBuilder UseNoelia(
      this IApplicationBuilder app,
      IHostEnvironment environment,
      string serviceName)
  {
    return app.UseNoelia(environment, serviceName, mw =>
    {
      // First: every later step that reads an address or a scheme reads the
      // values this one establishes.
      mw.UseForwardedHeaders()
        .UseSecurityHeaders()
        .UseCorrelationId()
        .UseRequestLogging()
        .UseTelemetry()
        .UseExceptionHandling()
        .UseInputSanitization()
        .UseSerilogLogging()
        .UseCors()
        .UseSwagger()
        // Health checks BEFORE rate limiting, and that order carries weight: a
        // braked liveness probe takes the container out of the load balancer,
        // which makes the limiter itself the outage. It used to be the other way
        // round, and the only thing keeping the probe alive was a whitelist entry
        // no caller could remove — see WhitelistedEndpoints.
        .UseHealthCheckEndpoints()
        .UseRateLimiting()
        .UseAuth()
        .UseSecurityAudit()
        .UsePermissions()
        .UseHttpCaching();
    });
  }

  /// <summary>
  /// The pipeline, written out step by step.
  /// </summary>
  /// <remarks>
  /// The order is the caller's here, and so is the responsibility for it. A step
  /// whose module was left out is still skipped: the decision was recorded once,
  /// on the service side, and naming it again cannot undo it.
  /// </remarks>
  /// <param name="app">The application being built.</param>
  /// <param name="environment">Where it runs.</param>
  /// <param name="serviceName">What the service calls itself.</param>
  /// <param name="configure">The steps, in the order they should run.</param>
  public static IApplicationBuilder UseNoelia(
      this IApplicationBuilder app,
      IHostEnvironment environment,
      string serviceName,
      Action<InfrastructureMiddlewareBuilder> configure)
  {
    ArgumentNullException.ThrowIfNull(configure);

    var builder = new InfrastructureMiddlewareBuilder(app, environment, serviceName);
    configure(builder);
    return app;
  }

  /// <summary>
  /// The old name for <see cref="UseNoelia(IApplicationBuilder, IHostEnvironment, string)"/>.
  /// </summary>
  /// <remarks>
  /// <c>AddX</c>/<c>UseX</c> is the pair a reader of an ASP.NET composition root
  /// looks for. This half was named before its counterpart became
  /// <c>AddNoelia</c>, so the pair stopped being one — and someone who saw
  /// <c>AddNoelia</c> and found no <c>UseNoelia</c> had to conclude either that
  /// nothing belongs in the pipeline or that they had missed something.
  /// </remarks>
  /// <param name="app">The application being built.</param>
  /// <param name="environment">Where it runs.</param>
  /// <param name="serviceName">What the service calls itself.</param>
  [Obsolete("Renamed to UseNoelia — the counterpart to AddNoelia. This forwards and will be removed in 5.0.")]
  public static IApplicationBuilder UseSharedInfrastructure(
      this IApplicationBuilder app,
      IHostEnvironment environment,
      string serviceName) => app.UseNoelia(environment, serviceName);

  /// <summary>
  /// The old name for
  /// <see cref="UseNoelia(IApplicationBuilder, IHostEnvironment, string, Action{InfrastructureMiddlewareBuilder})"/>.
  /// </summary>
  /// <param name="app">The application being built.</param>
  /// <param name="environment">Where it runs.</param>
  /// <param name="serviceName">What the service calls itself.</param>
  /// <param name="configure">The steps, in the order they should run.</param>
  [Obsolete("Renamed to UseNoelia — the counterpart to AddNoelia. This forwards and will be removed in 5.0.")]
  public static IApplicationBuilder UseSharedInfrastructure(
      this IApplicationBuilder app,
      IHostEnvironment environment,
      string serviceName,
      Action<InfrastructureMiddlewareBuilder> configure) =>
      app.UseNoelia(environment, serviceName, configure);

  /// <summary>
  /// Adds the in-process memory cache that rate limiting and other components use.
  /// </summary>
  /// <remarks>
  /// A distributed cache is not registered here. Add one from a provider
  /// package — <c>AddRedisConnection(...)</c> plus <c>AddRedisCache(...)</c>, or
  /// <c>AddInMemoryCache(...)</c>.
  /// </remarks>
  public static IServiceCollection AddCaching(this IServiceCollection services)
  {
    services.AddMemoryCache();
    return services;
  }


  /// <summary>
  /// Sets up the bearer scheme from a shared secret in configuration.
  /// </summary>
  /// <remarks>
  /// The path a service is on when it passes no keys of its own. It cannot
  /// separate issuing from verifying: every service holding the secret can mint
  /// a token for any subject. Pass a key pair to
  /// <c>AddJwtAuthentication(o => ...)</c> to separate the two.
  /// </remarks>
  public static IServiceCollection AddJwtAuthentication(
      this IServiceCollection services,
      IConfiguration configuration,
      IHostEnvironment environment)
  {
    var secret = Environment.GetEnvironmentVariable("JWT_SECRET")
        ?? configuration["JwtSettings:Secret"];

    if (string.IsNullOrWhiteSpace(secret) || secret.Contains("REPLACE_WITH_SECURE_SECRET_IN_PRODUCTION"))
    {
      throw new ConfigurationException("JWT_SECRET", "JwtSettings",
          environment.IsProduction()
              ? "JWT Secret not configured or using placeholder value. "
                + "Set JWT_SECRET environment variable to the SAME value for ALL services. "
                + "Generate a secure secret with: openssl rand -base64 32"
              : "JWT Secret not configured. Set JWT_SECRET environment variable to the SAME value for ALL services.");
    }

    var shared = SigningKey.FromSharedSecret(secret, kid: null);
    services.AddJwtAuthentication(new KeyRing([shared], shared), null, configuration, environment);

    // After the ring overload, so the resolved value wins over the configured
    // one: JWT_SECRET takes precedence over JwtSettings:Secret.
    services.Configure<JwtSettings>(opts => opts.Secret = secret);

    return services;
  }

  /// <summary>
  /// Sets up the bearer scheme from an explicit set of keys.
  /// </summary>
  /// <remarks>
  /// Which keys and which algorithms are accepted comes from
  /// <paramref name="keys"/>, never from a token header — see
  /// <see cref="KeyRing.ValidationParameters"/>.
  /// </remarks>
  /// <param name="services">The container.</param>
  /// <param name="keys">
  /// Verification keys, and the signing key if this service issues. Null when
  /// <paramref name="authority"/> supplies them.
  /// </param>
  /// <param name="authority">
  /// An OpenID Connect provider whose published key set verifies the tokens.
  /// Null for locally configured keys.
  /// </param>
  /// <param name="configuration">Supplies issuer, audience and lifetime.</param>
  /// <param name="environment">Decides whether metadata may travel over HTTP.</param>
  public static IServiceCollection AddJwtAuthentication(
      this IServiceCollection services,
      KeyRing? keys,
      string? authority,
      IConfiguration configuration,
      IHostEnvironment environment)
  {
    if (keys is null && authority is null)
    {
      throw new ConfigurationException("JWT_KEYS", "JwtSettings",
          "Configure keys, or an Authority whose published keys verify the tokens.");
    }

    if (keys is not null)
    {
      // Both, or neither. JwtService takes the ring as a constructor argument,
      // so registering the one without the other builds a container that fails
      // on the first request that reads a token. Every route into JWT setup runs
      // through here, which is the only place the pair cannot be split up again.
      services.AddSingleton(keys);
      services.AddScoped<IJwtService, JwtService>();
    }

    var issuer = Environment.GetEnvironmentVariable("JWT_ISSUER")
        ?? configuration["JwtSettings:Issuer"]
        ?? throw new ConfigurationException("JWT_ISSUER", "JwtSettings",
            "JWT Issuer not configured. Please set JWT_ISSUER environment variable or configure JwtSettings:Issuer");

    var audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE")
        ?? configuration["JwtSettings:Audience"]
        ?? throw new ConfigurationException("JWT_AUDIENCE", "JwtSettings",
            "JWT Audience not configured. Please set JWT_AUDIENCE environment variable or configure JwtSettings:Audience");

    var expireMinutes = int.TryParse(
        Environment.GetEnvironmentVariable("JwtSettings__ExpireMinutes") ?? configuration["JwtSettings:ExpireMinutes"],
        out var expire) ? expire : 15;

    services.Configure<JwtSettings>(opts =>
    {
      opts.Secret = configuration["JwtSettings:Secret"] ?? string.Empty;
      opts.Issuer = issuer;
      opts.Audience = audience;
      opts.ExpireMinutes = expireMinutes;
    });

    // A WebSocket handshake cannot carry an Authorization header, so the token
    // arrives in the query string — but only on the paths that actually speak
    // WebSocket. "/hubs" is SignalR's own convention; anything else is a route
    // of the application and has to be named in JwtSettings:WebSocketPaths.
    var webSocketPaths = configuration.GetSection("JwtSettings:WebSocketPaths").Get<string[]>()
        ?? ["/hubs"];

    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
          opts.RequireHttpsMetadata = !environment.IsDevelopment();
          opts.SaveToken = true;
          opts.MapInboundClaims = false;
          if (authority is not null)
          {
            // The provider publishes its keys and rotates them; discovery
            // follows both, which is why no key is configured here.
            opts.Authority = authority;
          }

          opts.TokenValidationParameters = keys is null
              ? new TokenValidationParameters
              {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero
              }
              : keys.ValidationParameters(issuer, audience);

          opts.TokenValidationParameters.NameClaimType = JwtRegisteredClaimNames.Sub;
          opts.TokenValidationParameters.RoleClaimType = System.Security.Claims.ClaimTypes.Role;

          opts.Events = new JwtBearerEvents
          {
            OnMessageReceived = context =>
                {
                  var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                  var path = context.HttpContext.Request.Path;

                  if (!string.IsNullOrEmpty(accessToken) &&
                      webSocketPaths.Any(p =>
                          path.StartsWithSegments(p) ||
                          path.Value?.Contains($"{p}/", StringComparison.OrdinalIgnoreCase) == true))
                  {
                    context.Token = accessToken;
                  }
                  return Task.CompletedTask;
                },
            // The revocation check lives in UseTokenRevocation(), not here.
            OnAuthenticationFailed = context =>
                {
                  if (context.Exception is SecurityTokenExpiredException)
                    context.Response.Headers.Append("Token-Expired", "true");
                  return Task.CompletedTask;
                },
            OnChallenge = async context =>
                {
                  context.HandleResponse();
                  context.Response.StatusCode = 401;
                  context.Response.ContentType = "application/json";
                  var result = System.Text.Json.JsonSerializer.Serialize(new
                  {
                    error = "unauthorized",
                    message = "You are not authorized to access this resource"
                  });
                  await context.Response.WriteAsync(result);
                }
          };
        });

    return services;
  }

  /// <summary>
  /// Configures health check endpoints with proper response formatting
  /// </summary>
  private static void ConfigureHealthCheckEndpoints(IApplicationBuilder app)
  {
    var healthCheckOptions = new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
      ResponseWriter = async (context, report) =>
      {
        context.Response.ContentType = "application/json";
        var response = new
        {
          status = report.Status.ToString(),
          timestamp = DateTime.UtcNow,
          durationMs = report.TotalDuration.TotalMilliseconds,
          checks = report.Entries.Select(e => new
          {
            name = e.Key,
            status = e.Value.Status.ToString(),
            durationMs = e.Value.Duration.TotalMilliseconds,
            tags = e.Value.Tags,
            error = e.Value.Exception?.Message
          })
        };
        await context.Response.WriteAsync(
                  System.Text.Json.JsonSerializer.Serialize(response,
                  new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
      }
    };

    // General health endpoint
    app.UseHealthChecks("/health", healthCheckOptions);

    // Liveness probe - only checks if the service is alive
    app.UseHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
      Predicate = r => r.Tags.Contains("live"),
      ResponseWriter = healthCheckOptions.ResponseWriter,
      ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status200OK
            }
    });

    // Readiness probe - checks if the service is ready to handle requests
    app.UseHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
      Predicate = r => r.Tags.Contains("ready"),
      ResponseWriter = healthCheckOptions.ResponseWriter,
      ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
    });
  }

  private static string[] ResolveAllowedOrigins(IConfiguration configuration, IHostEnvironment environment)
  {
    var configuredOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    var envOrigins = ParseOrigins(configuration["CORS_ORIGINS"] ?? Environment.GetEnvironmentVariable("CORS_ORIGINS"));
    var frontendOrigins = ParseOrigins(configuration["FRONTEND_URL"] ?? Environment.GetEnvironmentVariable("FRONTEND_URL"));

    var origins = configuredOrigins
        .Concat(envOrigins)
        .Concat(frontendOrigins)
        .Select(NormalizeOrigin)
        .Where(origin => !string.IsNullOrWhiteSpace(origin))
        .Select(origin => origin!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (origins.Count == 0 && environment.IsDevelopment())
    {
      origins.Add("http://localhost:3000");
    }

    return origins.ToArray();
  }

  private static IEnumerable<string> ParseOrigins(string? delimitedOrigins)
  {
    if (string.IsNullOrWhiteSpace(delimitedOrigins))
    {
      return Array.Empty<string>();
    }

    return delimitedOrigins
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(origin => origin.Trim());
  }

  private static string? NormalizeOrigin(string? origin)
  {
    if (string.IsNullOrWhiteSpace(origin))
    {
      return null;
    }

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
      return null;
    }

    return uri.GetLeftPart(UriPartial.Authority);
  }

  /// <summary>
  /// Logs which configuration sources are loaded and which critical secrets are present.
  /// Shows only source and length — no secret material is logged.
  /// </summary>
  private static void LogConfigurationSources(
      IConfiguration configuration,
      IHostEnvironment environment,
      string serviceName)
  {
    var separator = new string('=', 60);
    Console.WriteLine(separator);
    Console.WriteLine($"  CONFIG DIAGNOSTICS - {serviceName}");
    Console.WriteLine($"  Environment: {environment.EnvironmentName}");
    Console.WriteLine(separator);

    // Check critical secrets: name → (dockerEnvVar, configKey)
    // dockerEnvVar = the env var name as set by docker-compose environment: block
    // configKey = the .NET configuration key (reads from appsettings.json)
    var secrets = new Dictionary<string, (string dockerEnvVar, string configKey)>
    {
      ["JWT_SECRET"] = ("JwtSettings__Secret", "JwtSettings:Secret"),
      ["POSTGRES"] = ("ConnectionStrings__DefaultConnection", "ConnectionStrings:DefaultConnection"),
      ["RABBITMQ_PASSWORD"] = ("RabbitMQ__Password", "RabbitMQ:Password"),
      ["REDIS"] = ("ConnectionStrings__Redis", "ConnectionStrings:Redis"),
      ["M2M_SECRET"] = ("ServiceCommunication__M2M__ClientSecret", "ServiceCommunication:M2M:ClientSecret"),
      ["ENCRYPTION_KEY"] = ("SecretManager__EncryptionKey", "SecretManager:EncryptionKey"),
    };

    // Service-specific secrets
    if (serviceName.Contains("Notification", StringComparison.OrdinalIgnoreCase))
    {
      secrets["TWILIO_SID"] = ("Twilio__AccountSid", "Twilio:AccountSid");
      secrets["TWILIO_TOKEN"] = ("Twilio__AuthToken", "Twilio:AuthToken");
      secrets["SMTP_PASSWORD"] = ("Email__SmtpPassword", "Email:SmtpPassword");
      secrets["FIREBASE_KEY_ID"] = ("Firebase__PrivateKeyId", "Firebase:PrivateKeyId");
    }
    if (serviceName.Contains("Payment", StringComparison.OrdinalIgnoreCase))
    {
      secrets["STRIPE_SECRET"] = ("Stripe__SecretKey", "Stripe:SecretKey");
      secrets["STRIPE_WEBHOOK"] = ("Stripe__WebhookSecret", "Stripe:WebhookSecret");
    }
    if (serviceName.Contains("User", StringComparison.OrdinalIgnoreCase))
    {
      secrets["LINKEDIN_ID"] = ("OAuth__LinkedIn__ClientId", "OAuth:LinkedIn:ClientId");
      secrets["LINKEDIN_SECRET"] = ("OAuth__LinkedIn__ClientSecret", "OAuth:LinkedIn:ClientSecret");
    }

    foreach (var (label, (dockerEnvVar, configKey)) in secrets)
    {
      // Check if set via environment variable (docker-compose environment: block)
      var envValue = Environment.GetEnvironmentVariable(dockerEnvVar);
      // Check configuration (merges env vars + appsettings.json)
      var configValue = configuration[configKey];

      var value = configValue ?? envValue;
      // Determine source: if env var exists, it came from docker-compose (Infisical → shell → compose → container)
      var source = !string.IsNullOrEmpty(envValue) ? "ENV" : "CONFIG";

      if (value is null && envValue is null && configValue is null)
      {
        Console.WriteLine($"  {label,-20} : NOT SET");
      }
      else if (string.IsNullOrEmpty(value))
      {
        // Value is explicitly configured but empty (e.g. SMTP_PASSWORD for MailHog)
        Console.WriteLine($"  {label,-20} : SET (empty) [from {source}]");
      }
      else
      {
        Console.WriteLine($"  {label,-20} : SET ({value.Length} chars) [from {source}]");
      }
    }

    // Check if secrets came from Infisical (injected via docker-compose environment: block)
    // If JwtSettings__Secret exists as env var, it was set by docker-compose from ${JWT_SECRET}
    var jwtFromEnv = Environment.GetEnvironmentVariable("JwtSettings__Secret");
    var jwtFromConfig = configuration["JwtSettings:Secret"];
    var secretsFromEnvBlock = !string.IsNullOrEmpty(jwtFromEnv);
    Console.WriteLine($"  {"SOURCE",-20} : {(secretsFromEnvBlock ? "docker-compose environment: block (Infisical/shell)" : ".env.docker files + appsettings.json")}");

    Console.WriteLine(separator);
  }

  /// <summary>
  /// Cross-origin rules, from the origins this service was configured with.
  /// </summary>
  /// <remarks>
  /// Origins come from <c>Cors:AllowedOrigins</c>, <c>CORS_ORIGINS</c> and
  /// <c>FRONTEND_URL</c>. If none are configured, a development service falls
  /// back to <c>http://localhost:3000</c> and any other environment allows
  /// nothing — a wildcard would be the one setting nobody notices is wrong.
  /// <para>
  /// Without this call the framework applies no policy of its own, and a browser
  /// refuses cross-origin requests to this service.
  /// </para>
  /// </remarks>
  public static IServiceCollection AddNoeliaCors(
      this IServiceCollection services,
      IConfiguration configuration,
      IHostEnvironment environment)
  {
    services.AddCors(options =>
    {
      options.AddDefaultPolicy(policy =>
          {
            var allowedOrigins = ResolveAllowedOrigins(configuration, environment);

            policy.WithOrigins(allowedOrigins)
                      .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                      .WithHeaders(
                          "Content-Type",
                          "Authorization",
                          "X-Request-ID",
                          "X-Correlation-ID",
                          "X-Requested-With",
                          "X-SignalR-User-Agent",
                          "Accept",
                          "Accept-Language",
                          "Cache-Control",
                          "Pragma",
                          "baggage",
                          "sentry-trace"
                      )
                      .WithExposedHeaders(
                          "X-Request-ID",
                          "X-Correlation-ID",
                          "X-Pagination",
                          "Content-Disposition",
                          "baggage",
                          "sentry-trace"
                      )
                      .AllowCredentials()
                      .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
          });
    });


    return services;
  }
}
