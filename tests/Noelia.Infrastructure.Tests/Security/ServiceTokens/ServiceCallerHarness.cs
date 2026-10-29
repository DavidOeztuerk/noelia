using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// Every HTTP test of the receiving side shares one collection: the audit counter is
/// process-wide, and a test that reads it must not see another host's requests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServiceCallerHttpCollection
{
    public const string Name = "ServiceCallerHttp";
}

internal sealed record NoteBody(string Text);

/// <summary>The receiving service of the tests: the full Noelia chain over a test server.</summary>
internal sealed class ServiceCallerHost : IAsyncDisposable
{
    public const string ThisAudience = "identity";
    public const string Garbage = "not-a-token";

    private readonly IHost _host;

    private ServiceCallerHost(IHost host, CapturingLogger logs)
    {
        _host = host;
        Logs = logs;
        Client = host.GetTestClient();
    }

    public HttpClient Client { get; }

    public CapturingLogger Logs { get; }

    public IServiceProvider Services => _host.Services;

    public sealed class Settings
    {
        public bool StatusCodePages { get; set; }
        public bool Controllers { get; set; }
        public IConfiguration? Configuration { get; set; }
        public Action<NoeliaBuilder>? Compose { get; set; }
        public Action<IServiceCollection>? Services { get; set; }
        public Action<IEndpointRouteBuilder>? Map { get; set; }
    }

    public static async Task<ServiceCallerHost> StartAsync(Action<Settings>? configure = null)
    {
        var settings = new Settings();
        configure?.Invoke(settings);
        var logs = new CapturingLogger();
        var configuration = settings.Configuration ?? Config();

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.AddProvider(logs));
                    services.AddRouting();
                    services.AddNoelia(configuration, new Env(), "billing", noelia =>
                    {
                        // Serilog replaces the logger factory; the audit is read from the test's own provider.
                        noelia.UseDefaults().Without(NoeliaModule.Logging, "the test captures log events itself");
                        if (settings.Compose is null)
                        {
                            noelia.UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
                                .UseServiceCallers(ThisAudience);
                        }
                        else
                        {
                            settings.Compose(noelia);
                        }
                    });
                    services.AddAuthorizationBuilder()
                        .AddPolicy("Never", policy => policy.RequireAssertion(_ => false));
                    if (settings.Controllers)
                    {
                        services.AddControllers().ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            manager.ApplicationParts.Add(new TypesPart(typeof(MvcDoorController)));
                        });
                    }

                    settings.Services?.Invoke(services);
                })
                .Configure(app =>
                {
                    if (settings.StatusCodePages)
                    {
                        app.UseStatusCodePages();
                    }

                    app.UseRouting();
                    app.UseNoelia(new Env(), "billing");
                    app.UseEndpoints(endpoints =>
                    {
                        if (settings.Map is not null)
                        {
                            settings.Map(endpoints);
                            return;
                        }

                        MapStandard(endpoints);
                        if (settings.Controllers)
                        {
                            endpoints.MapControllers();
                            endpoints.MapServiceFallback("/internal-mvc");
                        }
                    });
                }))
            .StartAsync();

        return new ServiceCallerHost(host, logs);
    }

    /// <summary>The doors the matrix runs against.</summary>
    public static void MapStandard(IEndpointRouteBuilder endpoints)
    {
        var doors = endpoints.MapGroup("/internal");
        doors.MapGet("/account/{id}", (string id) => Results.Text("account " + id))
            .RequireServiceCaller("advisor", "transfer");
        doors.MapPost("/account/{id}/note", (string id, NoteBody body) => Results.Text("note " + body.Text))
            .RequireServiceCaller("notification");
        doors.MapGet("/both", () => Results.Text("both"))
            .RequireServiceCaller("advisor")
            .RequireAuthorization();
        doors.MapGet("/other", () => Results.Text("other"))
            .RequireServiceCaller("advisor")
            .RequireAuthorization("Never");
        doors.MapServiceFallback();

        endpoints.MapGet("/people/me", () => Results.Text("person")).RequireAuthorization();
    }

    public static IConfiguration Config() => ServiceTokenFixture.Config();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed class TypesPart(params Type[] types) : ApplicationPart, IApplicationPartTypeProvider
    {
        public override string Name => "service-caller-test-controllers";
        public IEnumerable<TypeInfo> Types => types.Select(type => type.GetTypeInfo());
    }
}

/// <summary>Builds the requests and tokens the tests send.</summary>
internal static class Wire
{
    public static HttpRequestMessage Request(
        HttpMethod method, string path, string? token = null, string? body = null, string? contentType = "application/json")
    {
        var request = new HttpRequestMessage(method, new Uri("http://localhost" + path));
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body);
            request.Content.Headers.Remove("Content-Type");
            if (contentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        return request;
    }

    /// <summary>Status, headers without the two that differ per request, and body bytes.</summary>
    public static async Task<string> Snapshot(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => !string.Equals(header.Key, "Date", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(header.Key, "X-Correlation-ID", StringComparison.OrdinalIgnoreCase)
                             // The limiter counts every request, so these two differ by construction between
                             // any two requests; the limit itself (X-RateLimit-Limit) stays compared.
                             && !string.Equals(header.Key, "X-RateLimit-Remaining", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(header.Key, "X-RateLimit-Reset", StringComparison.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => $"{header.Key}: {string.Join(",", header.Value)}");
        var body = Convert.ToHexString(await response.Content.ReadAsByteArrayAsync());
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\nbody={body}";
    }

    /// <summary>A real token from the issuer, in-process.</summary>
    public static async Task<string> Issued(string client, string audience = ServiceCallerHost.ThisAudience)
    {
        var config = StandardConfig();
        await using var provider = (ServiceProvider)Compose(config, n => Standard(n, config)).BuildServiceProvider();
        var secret = client == "advisor" ? OtherVector : KnownVector;
        var token = await provider.GetRequiredService<IServiceTokenIssuer>().IssueAsync(client, secret, audience);
        return token!.AccessToken;
    }

    /// <summary>A service token built by hand with the issuer's key, for the cases the issuer would never produce.</summary>
    public static string Service(
        string client = "advisor",
        string audience = ServiceCallerHost.ThisAudience,
        string type = ServiceTokenDefaults.TokenType,
        TimeSpan? lifetime = null,
        string issuer = Issuer)
    {
        var now = DateTime.UtcNow;
        var expires = lifetime ?? TimeSpan.FromMinutes(5);
        return Sign(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = expires < TimeSpan.Zero ? now + expires - TimeSpan.FromMinutes(5) : now,
            IssuedAt = expires < TimeSpan.Zero ? now + expires - TimeSpan.FromMinutes(5) : now,
            Expires = now + expires,
            TokenType = type,
            Claims = new Dictionary<string, object> { ["client_id"] = client }
        });
    }

    /// <summary>A person's token as the person scheme accepts it, optionally with a self-set client_id.</summary>
    public static string Person(string? clientIdClaim = null, TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        var expires = lifetime ?? TimeSpan.FromMinutes(5);
        var claims = new Dictionary<string, object> { ["sub"] = "person-1" };
        if (clientIdClaim is not null)
        {
            claims["client_id"] = clientIdClaim;
        }

        return Sign(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = PersonAudience,
            NotBefore = expires < TimeSpan.Zero ? now + expires - TimeSpan.FromMinutes(5) : now,
            IssuedAt = expires < TimeSpan.Zero ? now + expires - TimeSpan.FromMinutes(5) : now,
            Expires = now + expires,
            Claims = claims
        });
    }

    private static string Sign(SecurityTokenDescriptor descriptor)
    {
        descriptor.SigningCredentials =
            SigningKey.FromEcdsaPrivateKey(KeyPair.PrivateKey, KeyPair.Kid).SigningCredentials();
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static string RandomSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}

/// <summary>Keeps what the audit wrote, structured, so a test reads the fields and not a rendering.</summary>
internal sealed class CapturingLogger : ILoggerProvider
{
    private readonly List<Entry> _entries = [];

    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, string?> Fields);

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public IReadOnlyList<Entry> Audit => Entries
        .Where(entry => entry.Category.EndsWith("ServiceCallerAudit", StringComparison.Ordinal))
        .ToArray();

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Sink(CapturingLogger owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    fields[key] = value?.ToString();
                }
            }

            lock (owner._entries)
            {
                owner._entries.Add(new Entry(category, logLevel, formatter(state, exception), fields));
            }
        }
    }
}
