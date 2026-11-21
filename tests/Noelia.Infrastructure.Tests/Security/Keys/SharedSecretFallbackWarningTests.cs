using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Tests.Security.Keys;

/// <summary>
/// A key ring that came from the shared secret without anyone choosing it says
/// so once, when the host starts (Q-0071, ADR-0008 decision 6).
/// </summary>
/// <remarks>
/// Two paths are silent: <c>AddJwtAuthentication()</c> on the infrastructure
/// builder without keys, and the direct
/// <c>services.AddJwtAuthentication(configuration, environment)</c>. Every
/// explicit way — keys, an authority, <c>FromSharedSecret()</c> — stays quiet,
/// or the warning would be noise nobody can act on. The secret comes from
/// in-memory configuration so no test depends on a real <c>JWT_SECRET</c>.
/// </remarks>
[Trait("Category", "Unit")]
public class SharedSecretFallbackWarningTests
{
    private const string Category = "Noelia.Infrastructure.Security.Keys";
    private const string Secret = "a-test-secret-long-enough-to-sign-with-hmac-sha256";

    [Fact]
    public async Task SharedSecretFallback_LogsWarningAtStart()
    {
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) => services.AddSharedInfrastructure(
            config, env, "test", infrastructure => infrastructure.AddJwtAuthentication()));

        var warning = logs.WarningsIn(Category).Should().ContainSingle().Subject;
        warning.Should().Contain("8.0").And.Contain("FromSharedSecret()");
        warning.Should().NotContain(Secret);
        warning.Should().NotContain("JWT_SECRET").And.NotContain("JwtSettings:Secret");
        Regex.IsMatch(warning, $@"\b{Secret.Length}\b").Should().BeFalse("the length of a secret is a hint");
    }

    [Fact]
    public async Task The_direct_call_without_keys_logs_one_warning_at_start()
    {
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) => services.AddJwtAuthentication(config, env));

        logs.WarningsIn(Category).Should().ContainSingle();
    }

    [Fact]
    public async Task Both_silent_paths_in_one_host_log_exactly_one_warning()
    {
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) =>
        {
            services.AddJwtAuthentication(config, env);
            services.AddSharedInfrastructure(
                config, env, "test", infrastructure => infrastructure.AddJwtAuthentication());
        });

        logs.WarningsIn(Category).Should().ContainSingle();
    }

    [Fact]
    public async Task FromSharedSecret_is_the_explicit_choice_and_logs_nothing()
    {
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) => services.AddNoelia(
            config, env, "test", n => n.UseJwt(jwt => jwt.FromSharedSecret())));

        logs.WarningsIn(Category).Should().BeEmpty();
    }

    [Fact]
    public async Task A_signing_key_from_a_shared_secret_passed_in_options_logs_nothing()
    {
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) => services.AddSharedInfrastructure(
            config, env, "test", infrastructure => infrastructure.AddJwtAuthentication(
                o => o.SigningKey = SigningKey.FromSharedSecret(Secret, kid: null))));

        logs.WarningsIn(Category).Should().BeEmpty();
    }

    [Fact]
    public async Task Issue_logs_nothing()
    {
        var pair = SigningKey.GenerateKeyPair("k1");
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) => services.AddNoelia(
            config, env, "test", n => n.UseJwt(jwt => jwt.Issue(pair.PrivateKey, pair.Kid))));

        logs.WarningsIn(Category).Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyOnly_logs_nothing()
    {
        var pair = SigningKey.GenerateKeyPair("k1");
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) => services.AddNoelia(
            config, env, "test", n => n.UseJwt(jwt => jwt.VerifyOnly(pair.PublicKey, pair.Kid))));

        logs.WarningsIn(Category).Should().BeEmpty();
    }

    [Fact]
    public async Task An_authority_in_options_logs_nothing()
    {
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) => services.AddSharedInfrastructure(
            config, env, "test", infrastructure => infrastructure.AddJwtAuthentication(
                o => o.Authority = "https://idp.example.test")));

        logs.WarningsIn(Category).Should().BeEmpty();
    }

    [Fact]
    public async Task An_authority_through_UseJwt_logs_nothing()
    {
        var logs = new CategoryLogs();

        await StartAsync(logs, (services, config, env) => services.AddNoelia(
            config, env, "test", n => n.UseJwt(jwt => jwt.From("https://idp.example.test"))));

        logs.WarningsIn(Category).Should().BeEmpty();
    }

    private static async Task StartAsync(
        CategoryLogs logs,
        Action<IServiceCollection, IConfiguration, IHostEnvironment> compose)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:Secret"] = Secret,
            ["JwtSettings:Issuer"] = "noelia-tests",
            ["JwtSettings:Audience"] = "noelia-tests"
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        compose(builder.Services, builder.Configuration, builder.Environment);

        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
    }

    private sealed class CategoryLogs : ILoggerProvider
    {
        private readonly List<(string Category, LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> WarningsIn(string category)
        {
            lock (_entries)
            {
                return _entries
                    .Where(e => e.Category == category && e.Level == LogLevel.Warning)
                    .Select(e => e.Message)
                    .ToArray();
            }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

        public void Dispose() { }

        private sealed class Sink(CategoryLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                {
                    owner._entries.Add((category, logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
