using Microsoft.Extensions.DependencyInjection;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Security.M2M;

/// <summary>Puts a service token on the calls of a named or typed <c>HttpClient</c>.</summary>
public static class ServiceTokenHttpClientExtensions
{
    /// <summary>
    /// Makes every request of this client carry the service token for
    /// <paramref name="audience"/>.
    /// </summary>
    /// <remarks>
    /// Needs <c>UseServiceTokenClient()</c> (or any other <see cref="IServiceTokenSource"/>).
    /// The handler never overwrites: a request that already carries an
    /// <c>Authorization</c> header is an error, because replacing a person's token
    /// silently, or sending it on to another service, is worse than failing. A token
    /// that cannot be had fails the call before anything is sent.
    /// </remarks>
    /// <param name="builder">The client's builder.</param>
    /// <param name="audience">
    /// The audience of the service this client calls, in identifier form: lower case,
    /// starting with a letter, up to 48 letters, digits and hyphens.
    /// </param>
    /// <exception cref="ArgumentException">The audience is not an identifier.</exception>
    public static IHttpClientBuilder AddServiceToken(this IHttpClientBuilder builder, string audience)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!ServiceTokenFormat.IsIdentifier(audience))
        {
            throw new ArgumentException(
                "The audience starts with a lower-case letter and has up to 48 lower-case letters, "
                + "digits and hyphens.",
                nameof(audience));
        }

        return builder.AddHttpMessageHandler(provider =>
            new ServiceTokenHandler(audience, provider.GetRequiredService<IServiceTokenSource>()));
    }

    private sealed class ServiceTokenHandler(string audience, IServiceTokenSource source) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Contains("Authorization"))
            {
                throw new InvalidOperationException(
                    "The request already carries an Authorization header. A client with a service "
                    + "token adds its own and does not replace or forward another.");
            }

            var token = await source.GetTokenAsync(audience, cancellationToken);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
