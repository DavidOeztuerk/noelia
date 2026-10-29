using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// The log says who asked and what came of it, and never what was presented.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceTokenIssuerLoggingTests
{
    [Fact]
    public async Task A_refusal_logs_the_known_client_and_never_the_secret_hash_or_token()
    {
        var sink = new Sink();
        var issuer = Issuer(sink);

        await issuer.IssueAsync("notification", OtherVector, "identity");
        var issued = await issuer.IssueAsync("notification", KnownVector, "identity");

        var log = sink.Text;
        log.Should().Contain("notification").And.Contain("identity");
        log.Should().NotContain(KnownVector).And.NotContain(OtherVector);
        log.Should().NotContain(KnownHash).And.NotContain(OtherHash);
        log.Should().NotContain(KnownHash[7..14], "not even a prefix of the hash");
        log.Should().NotContain(issued!.AccessToken);
    }

    [Fact]
    public async Task An_unknown_client_is_logged_as_unknown_and_its_sent_value_is_not_logged()
    {
        var sink = new Sink();
        var issuer = Issuer(sink);

        await issuer.IssueAsync("attacker-chosen-id", KnownVector, "attacker-chosen-audience");

        sink.Text.Should().Contain("unknown client");
        sink.Text.Should().NotContain("attacker-chosen-id").And.NotContain("attacker-chosen-audience");
        sink.Text.Should().NotContain(KnownVector);
    }

    private static IServiceTokenIssuer Issuer(Sink sink)
    {
        var config = StandardConfig();
        var services = Compose(config, n => Standard(n, config));
        services.AddSingleton<ILoggerProvider>(sink);
        return services.BuildServiceProvider().GetRequiredService<IServiceTokenIssuer>();
    }

    private sealed class Sink : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = [];

        public string Text
        {
            get { lock (_lines) { return string.Join("\n", _lines); } }
        }

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }
}
