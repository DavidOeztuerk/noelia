namespace Noelia.Infrastructure.Security.M2M;

/// <summary>
/// Hands out the token this service presents to one other service, chosen by that
/// service's audience.
/// </summary>
/// <remarks>
/// A separate port from <see cref="IServiceTokenProvider"/>, which has one token and no
/// audience and stays as it is. This one either returns a token or throws: a caller that
/// received nothing and went on to send the request anyway is the failure it exists to
/// rule out.
/// </remarks>
public interface IServiceTokenSource
{
    /// <summary>Gets a valid token for <paramref name="audience"/>.</summary>
    /// <param name="audience">The audience of the target service, in identifier form.</param>
    /// <param name="cancellationToken">Cancels the wait and, if this call fetches, the fetch.</param>
    /// <exception cref="ServiceTokenUnavailableException">No token could be had.</exception>
    /// <exception cref="ArgumentException">The audience is not an identifier.</exception>
    Task<string> GetTokenAsync(string audience, CancellationToken cancellationToken = default);
}

/// <summary>
/// No service token could be obtained: the endpoint refused, did not answer, or answered
/// with something that is not a token.
/// </summary>
/// <remarks>
/// The message names the cause by kind only. It never carries a secret, a token or a
/// response body.
/// </remarks>
public sealed class ServiceTokenUnavailableException : Exception
{
    /// <summary>Creates the exception with a message free of values.</summary>
    public ServiceTokenUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the failure that caused it.</summary>
    public ServiceTokenUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
