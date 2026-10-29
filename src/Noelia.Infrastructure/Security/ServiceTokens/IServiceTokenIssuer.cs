namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// Issues a service token to a caller that proves it holds a registered secret.
/// </summary>
public interface IServiceTokenIssuer
{
    /// <summary>
    /// Checks the credentials and, if they hold, signs a token for one audience.
    /// </summary>
    /// <returns>The token, or null for every kind of refusal.</returns>
    Task<ServiceToken?> IssueAsync(
        string clientId, string clientSecret, string audience, CancellationToken cancellationToken = default);
}

/// <summary>A signed service token and how long it lives.</summary>
/// <param name="AccessToken">The compact JWT.</param>
/// <param name="ExpiresIn">Time from issue to expiry.</param>
public sealed record ServiceToken(string AccessToken, TimeSpan ExpiresIn);
