using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>Endpoint conventions for doors that only named services may open.</summary>
public static class ServiceCallerEndpointExtensions
{
    /// <summary>
    /// Lets only the named services call this endpoint, and answers everyone else
    /// with the 404 of a door that is not there.
    /// </summary>
    /// <remarks>
    /// Needs <c>UseServiceCallers</c> in the composition. Put a
    /// <see cref="MapServiceFallback"/> on the same group: a refused request is a 404
    /// with no body, and only a fallback makes a path with no door answer the same.
    /// Rate limits, output caching and CORS belong on the group, never on one door,
    /// for the same reason.
    /// </remarks>
    /// <param name="builder">The endpoint or group.</param>
    /// <param name="callers">Client ids as the issuer knows them. At least one; no wildcard.</param>
    /// <exception cref="ArgumentException">The list is empty or an id is malformed. Thrown here, at the mapping.</exception>
    public static TBuilder RequireServiceCaller<TBuilder>(this TBuilder builder, params string[] callers)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Built now, not when the endpoint is: the exception belongs to the line that wrote the list.
        var attribute = new ServiceCallerAttribute(callers);
        builder.Add(endpoint => endpoint.Metadata.Add(attribute));
        return builder;
    }

    /// <summary>
    /// Maps a catch-all that answers every method and every path under it with the
    /// same 404 a refused door gives.
    /// </summary>
    /// <remarks>
    /// It carries a requirement nobody can meet, so a request to it takes the same way
    /// through the same scheme and the same result handler as a request to a door the
    /// caller may not open. A wrong method on an existing door lands here instead of
    /// in a 405, because the router prefers the catch-all that accepts every method
    /// to no endpoint at all.
    /// <para>
    /// On a group (<c>app.MapGroup("/internal")</c>) it covers the group. On the
    /// application, as for controller doors, name the prefix: a catch-all at the root
    /// would take every path of the service.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The group or the application.</param>
    /// <param name="prefix">
    /// Required unless <paramref name="endpoints"/> is a group: the path the doors live under.
    /// </param>
    /// <exception cref="ArgumentException">
    /// No prefix on something that is not a group, or a prefix that is not a path.
    /// </exception>
    public static IEndpointConventionBuilder MapServiceFallback(
        this IEndpointRouteBuilder endpoints,
        string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        string pattern;
        if (prefix is null)
        {
            if (endpoints is not RouteGroupBuilder)
            {
                throw new ArgumentException(
                    "Without a group, MapServiceFallback needs the prefix the doors live under, "
                    + "for example \"/internal\": a catch-all at the root would take every path.",
                    nameof(prefix));
            }

            pattern = "{**rest}";
        }
        else
        {
            var trimmed = prefix.Trim('/');
            if (trimmed.Length == 0 || trimmed.Contains('{', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The prefix is the literal path the doors live under, for example \"/internal\".",
                    nameof(prefix));
            }

            pattern = "/" + trimmed + "/{**rest}";
        }

        // The delegate never runs for a request: the authorization middleware ends it first,
        // since nobody can meet the requirement. It still answers 404 should that ever change.
        var fallback = endpoints.Map(pattern, static context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        fallback.WithMetadata(ServiceFallbackMarker.Instance, ServiceFallbackAuthorizeData.Instance);
        fallback.ExcludeFromDescription();
        return fallback;
    }
}
