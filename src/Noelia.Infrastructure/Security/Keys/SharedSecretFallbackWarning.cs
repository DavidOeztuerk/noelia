using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Noelia.Infrastructure.Security.Keys;

/// <summary>
/// Says once, when the host starts, that the JWT key ring came from a shared
/// secret in configuration because nobody chose a key.
/// </summary>
/// <remarks>
/// Registered at the two call sites that fall back silently, never in shared
/// code: the explicit ways to the same ring (<c>FromSharedSecret()</c>, keys,
/// an authority) must stay quiet, so the call site is the only place that
/// knows whether the caller chose. <c>TryAddEnumerable</c> makes it one
/// service however many silent paths a container runs. The text names shape
/// and way only — not the value, not its length, and not whether
/// <c>JWT_SECRET</c> or <c>JwtSettings:Secret</c> supplied it, because a log
/// line outlives the configuration it describes.
/// </remarks>
internal sealed partial class SharedSecretFallbackWarning(ILoggerFactory loggers) : IHostedService
{
    // A string, not ILogger<T>: the category is what an operator filters on, and
    // it must not change when this type is renamed or moved.
    internal const string LoggerCategory = "Noelia.Infrastructure.Security.Keys";

    internal static IServiceCollection AddSharedSecretFallbackWarning(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, SharedSecretFallbackWarning>());
        return services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Warn(loggers.CreateLogger(LoggerCategory));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The JWT key ring comes from a shared secret in configuration because no keys were "
                  + "passed. Noelia 8.0 will refuse to start this way. Choose explicitly: "
                  + "UseJwt(jwt => jwt.Issue(...)) or VerifyOnly(...), or o.SigningKey / o.ValidationKeys "
                  + "with SigningKey.FromSharedSecret(...). To keep the shared secret on purpose, "
                  + "write UseJwt(jwt => jwt.FromSharedSecret()).")]
    private static partial void Warn(ILogger logger);
}
