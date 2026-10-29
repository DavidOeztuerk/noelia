using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// The token endpoint of the service-token issuer.
/// </summary>
public static class ServiceTokenEndpoint
{
    private const int MaxBodyBytes = 4096;
    private const string FormMediaType = "application/x-www-form-urlencoded";
    private const string ClientCredentials = "client_credentials";

    // Written byte for byte rather than serialised, so every refusal is the same
    // bytes by construction and nothing in a JSON options object can change that.
    private static readonly byte[] Refusal = "{\"error\":\"invalid_client\"}"u8.ToArray();

    /// <summary>
    /// Maps the OAuth 2.0 client-credentials endpoint at <paramref name="pattern"/>.
    /// </summary>
    /// <remarks>
    /// The service decides where it lives and what stands in front of it; the
    /// builder this returns takes <c>RequireRateLimiting</c> and the like. It reads
    /// the form body itself rather than binding it, because binding would bring
    /// antiforgery and its own 400 and 415 answers ahead of any check, and this
    /// endpoint has exactly one failure answer.
    /// <para>
    /// Success is <c>200</c> with <c>access_token</c>, <c>token_type</c> and
    /// <c>expires_in</c>. Every refusal, whatever the cause, is <c>401</c> with
    /// <c>{"error":"invalid_client"}</c>. Both are marked as not to be cached.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The path. Defaults to <c>/m2m/token</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="IServiceTokenIssuer"/> is registered. Thrown here, at mapping,
    /// and not by the first request.
    /// </exception>
    public static RouteHandlerBuilder MapServiceTokenEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/m2m/token")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (endpoints.ServiceProvider.GetService<IServiceTokenIssuer>() is null)
        {
            throw new InvalidOperationException(
                "MapServiceTokenEndpoint needs the issuer: call UseServiceTokenIssuer(...) on the "
                + "Noelia composition of this service. Mapped without it, every request would fail.");
        }

        // Cast: a bare method group binds to the RequestDelegate overload, which returns the
        // general convention builder; the service chains RequireRateLimiting on the specific one.
        var route = endpoints.MapPost(pattern, (Delegate)HandleAsync);

        route.AllowAnonymous();
        route.DisableAntiforgery();
        return route;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var issuer = context.RequestServices.GetRequiredService<IServiceTokenIssuer>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Noelia.Infrastructure.Security.ServiceTokens.ServiceTokenEndpoint");

        ServiceToken? token = null;
        try
        {
            var form = await ReadFormAsync(context.Request, context.RequestAborted);
            if (form is not null
                && form.TryGetValue("grant_type", out var grant) && grant == ClientCredentials
                && form.TryGetValue("client_id", out var clientId)
                && form.TryGetValue("client_secret", out var secret)
                && form.TryGetValue("audience", out var audience))
            {
                token = await issuer.IssueAsync(clientId, secret, audience, context.RequestAborted);
            }
            else
            {
                logger.LogWarning("Service token request refused: the request was not a valid client-credentials form");
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // Any exception is a refusal, and its text stays out of the log: it can
            // quote what was read.
            logger.LogWarning(
                "Service token request refused: {Exception} while reading or issuing",
                exception.GetType().Name);
        }

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.ContentType = "application/json";

        if (token is null)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            response.ContentLength = Refusal.Length;
            await response.Body.WriteAsync(Refusal, context.RequestAborted);
            return;
        }

        var body = Encoding.UTF8.GetBytes(
            $"{{\"access_token\":\"{token.AccessToken}\",\"token_type\":\"Bearer\","
            + $"\"expires_in\":{(int)token.ExpiresIn.TotalSeconds}}}");

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>
    /// Reads the four fields, each exactly once and within its length limit, or
    /// returns null when the request is not shaped like that.
    /// </summary>
    private static async Task<Dictionary<string, string>?> ReadFormAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        // Only the media type counts: a charset parameter is allowed, and a body
        // declared as anything else is not parsed at all.
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, FormMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        // One byte past the limit is enough to know it was exceeded; the rest of the
        // stream is never read, so an endless body costs a few kilobytes.
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxBodyBytes)
        {
            return null;
        }

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer, 0, total);
        var parsed = QueryHelpers.ParseQuery(text);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, limit) in Limits)
        {
            if (!parsed.TryGetValue(name, out var values) || values.Count != 1)
            {
                // Absent, or present more than once: a duplicate is not resolved by
                // picking one, since the first and the last would disagree about who is asking.
                return null;
            }

            var value = values[0];
            if (value is null || value.Length > limit)
            {
                return null;
            }

            fields[name] = value;
        }

        return fields;
    }

    private static readonly (string Name, int Limit)[] Limits =
    [
        ("grant_type", 32),
        ("client_id", ServiceTokenFormat.MaxIdentifierLength),
        ("client_secret", ServiceTokenFormat.SecretLength),
        ("audience", ServiceTokenFormat.MaxIdentifierLength)
    ];
}
