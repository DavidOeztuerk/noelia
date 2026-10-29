namespace Noelia.Infrastructure.Communication.Configuration;

/// <summary>
/// Which wire protocol a service uses to get its token for calls to other services.
/// </summary>
public enum ServiceTokenProtocol
{
    /// <summary>
    /// The format of the original Skillswap token service: a JSON body with
    /// <c>serviceName</c> and <c>servicePassword</c>, one token per client, kept in the
    /// distributed cache. It stays so existing deployments keep working; it is not
    /// the choice for a new service, and it is not marked obsolete because it is still
    /// what an unconfigured service speaks.
    /// </summary>
    Legacy,

    /// <summary>
    /// OAuth 2.0 client credentials against the Noelia token endpoint: a form body, one
    /// short-lived token per audience, kept in the process only and never in a shared
    /// cache. A token that cannot be had is an error; nothing falls back to another
    /// credential.
    /// </summary>
    ClientCredentials
}

/// <summary>
/// Machine-to-Machine authentication configuration
/// </summary>
public class M2MConfiguration
{
    /// <summary>
    /// Enable M2M authentication
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The wire protocol. <see cref="ServiceTokenProtocol.Legacy"/> unless
    /// <c>UseServiceTokenClient()</c> or the configuration says otherwise.
    /// </summary>
    public ServiceTokenProtocol Protocol { get; set; } = ServiceTokenProtocol.Legacy;

    /// <summary>
    /// The audience <c>GetTokenAsync()</c> asks for when called without one, in
    /// <see cref="ServiceTokenProtocol.ClientCredentials"/> mode. Calls that name an
    /// audience, such as <c>AddServiceToken("identity")</c>, ignore it.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Token endpoint for client credentials flow
    /// </summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// Client ID for this service
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Client secret for this service
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Scopes to request
    /// </summary>
    public HashSet<string> Scopes { get; set; } = new() { "api.read", "api.write" };

    /// <summary>
    /// Token lifetime
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Refresh token before expiry
    /// </summary>
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Enable token caching
    /// </summary>
    public bool EnableTokenCaching { get; set; } = true;

    /// <summary>
    /// Token cache key prefix
    /// </summary>
    public string TokenCacheKeyPrefix { get; set; } = "m2m:token:";

    /// <summary>
    /// Use mutual TLS for token requests
    /// </summary>
    public bool UseMutualTLS { get; set; } = false;

    /// <summary>
    /// Client certificate path (for mTLS)
    /// </summary>
    public string? ClientCertificatePath { get; set; }

    /// <summary>
    /// Client certificate password (for mTLS)
    /// </summary>
    public string? ClientCertificatePassword { get; set; }
}
