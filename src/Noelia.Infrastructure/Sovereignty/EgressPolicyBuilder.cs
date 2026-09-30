using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Noelia.Abstractions.Sovereignty;

namespace Noelia.Infrastructure.Sovereignty;

/// <summary>
/// Declares the hosts an application is allowed to call.
/// </summary>
/// <example>
/// <code>
/// services.AddNoeliaEgressPolicy(p => p
///     .AllowLoopback()
///     .AllowPrivateNetworks()
///     .Allow("openbao.internal")
///     .AllowSubdomainsOf("example.eu"));
/// </code>
/// </example>
public sealed class EgressPolicyBuilder
{
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suffixes = new(StringComparer.OrdinalIgnoreCase);
    private bool _loopback;
    private bool _privateNetworks;

    /// <summary>Allows calls to these exact hosts.</summary>
    public EgressPolicyBuilder Allow(params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        foreach (var host in hosts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);

            if (host.StartsWith('*'))
            {
                throw new ArgumentException(
                    $"Use AllowSubdomainsOf for wildcards instead of '{host}'.", nameof(hosts));
            }

            _hosts.Add(host);
        }

        return this;
    }

    /// <summary>
    /// Allows calls to any subdomain of each entry in <paramref name="domains"/>,
    /// but not to the domain itself unless it is also allowed explicitly.
    /// </summary>
    /// <param name="domains">Domains whose subdomains may be reached.</param>
    public EgressPolicyBuilder AllowSubdomainsOf(params string[] domains)
    {
        ArgumentNullException.ThrowIfNull(domains);

        foreach (var domain in domains)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(domain);
            _suffixes.Add(domain.StartsWith('.') ? domain : $".{domain}");
        }

        return this;
    }

    /// <summary>Allows calls to localhost — sidecars, local collectors.</summary>
    public EgressPolicyBuilder AllowLoopback()
    {
        _loopback = true;
        return this;
    }

    /// <summary>
    /// Allows calls into RFC 1918 and RFC 4193 ranges — the cluster and the
    /// machines beside it.
    /// </summary>
    public EgressPolicyBuilder AllowPrivateNetworks()
    {
        _privateNetworks = true;
        return this;
    }

    public IEgressPolicy Build() => new EgressPolicy(_hosts, _suffixes, _loopback, _privateNetworks);
}

public static class EgressPolicyExtensions
{
    /// <summary>
    /// Registers the egress policy and applies it to every client created through
    /// <c>IHttpClientFactory</c>.
    /// </summary>
    /// <remarks>
    /// Clients constructed with <c>new HttpClient()</c> bypass the factory and
    /// therefore this guard. <strong>That is not detected</strong> — a process
    /// cannot see a socket somebody opened without asking it — so it is a
    /// code-review matter, and <c>noelia.egress.guard</c> says so rather than
    /// implying a guarantee the guard cannot give.
    /// When enforcing, the supported primary transports are
    /// <see cref="HttpClientHandler"/> and <see cref="SocketsHttpHandler"/>.
    /// Automatic redirects are disabled: callers receive the redirect response
    /// and must make a new policy-checked request without forwarding credentials
    /// or private content to a different origin. Unknown primary transports are
    /// refused because their redirect behaviour cannot be controlled here.
    /// </remarks>
    public static IServiceCollection AddNoeliaEgressPolicy(
        this IServiceCollection services,
        Action<EgressPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new EgressPolicyBuilder();
        configure(builder);

        services.AddSingleton(builder.Build());
        services.AddTransient<EgressGuardHandler>();
        services.AddSingleton<IHttpEgressPolicyReport, HttpEgressPolicyReport>();

        // Post-configuration runs after named clients choose their transport,
        // regardless of whether the policy was registered before or after them.
        services.PostConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
            {
                if (handlerBuilder.Services.GetRequiredService<IEgressPolicy>().IsEnforcing)
                {
                    switch (handlerBuilder.PrimaryHandler)
                    {
                        case HttpClientHandler handler:
                            handler.AllowAutoRedirect = false;
                            break;
                        case SocketsHttpHandler handler:
                            handler.AllowAutoRedirect = false;
                            break;
                        default:
                            throw new InvalidOperationException(
                                "An enforcing Noelia egress policy requires HttpClientHandler or "
                                + "SocketsHttpHandler as the primary transport so automatic redirects "
                                + "cannot bypass the guard. Custom primary transports are not supported.");
                    }
                }
                handlerBuilder.AdditionalHandlers.Add(
                    handlerBuilder.Services.GetRequiredService<EgressGuardHandler>());
            }));

        return services;
    }
}
