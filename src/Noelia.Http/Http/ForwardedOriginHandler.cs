using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Noelia.Infrastructure.Http;

/// <summary>
/// Carries the caller's scheme and address on to the next hop.
/// </summary>
/// <remarks>
/// <para>For a service that is itself a proxy. <c>UseForwardedHeaders</c>
/// <em>consumes</em> what a proxy in front sent: it rewrites
/// <c>Request.Scheme</c> and <c>Connection.RemoteIpAddress</c> and removes the
/// headers, which is correct — leaving them in place would let a second pass
/// read them again. What it cannot know is that this service is about to make
/// a call of its own.</para>
///
/// <para>Without this handler a gateway ends the chain. TLS terminates at the
/// edge, the gateway learns the request was <c>https</c> and reaches the
/// service behind it over plain HTTP with nothing said — so that service sees
/// <c>http</c>, issues a cookie without <c>Secure</c> because it honestly
/// believes the transport was insecure, and counts the gateway's address
/// against every rate limit. The monolith in the same deployment does the right
/// thing, because nothing sits between it and the edge, which makes the fault
/// look like an application difference rather than a missing hop.</para>
///
/// <para>The receiving service still decides whether to believe any of it:
/// <c>TrustForwardedHeadersFrom</c> names the proxies whose word counts, and a
/// header from anywhere else changes nothing.</para>
/// </remarks>
public sealed class ForwardedOriginHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    private const string Proto = "X-Forwarded-Proto";
    private const string For = "X-Forwarded-For";
    private const string Host = "X-Forwarded-Host";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = accessor.HttpContext;
        if (context is null)
        {
            // A background call belongs to no caller, and inventing an origin
            // for it would put this process's own address in the header.
            return base.SendAsync(request, cancellationToken);
        }

        Add(request, Proto, context.Request.Scheme);
        Add(request, Host, context.Request.Host.Value);

        if (context.Connection.RemoteIpAddress is { } address)
        {
            Add(request, For, address.ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>Sets a header only where the caller left it alone.</summary>
    private static void Add(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value) && !request.Headers.Contains(name))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

/// <summary>Registers origin forwarding for every HTTP client.</summary>
public static class ForwardedOriginPropagationExtensions
{
    /// <summary>
    /// Adds <see cref="ForwardedOriginHandler"/> to every client the factory
    /// builds.
    /// </summary>
    /// <remarks>
    /// For services that call other services on a caller's behalf — a gateway,
    /// a facade, anything doing fan-out. A service that only answers does not
    /// need it, and adding it there sends headers nobody reads.
    /// </remarks>
    /// <param name="services">The container.</param>
    public static IServiceCollection AddForwardedOriginPropagation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddTransient<ForwardedOriginHandler>();
        services.ConfigureHttpClientDefaults(client =>
            client.AddHttpMessageHandler<ForwardedOriginHandler>());

        return services;
    }
}
