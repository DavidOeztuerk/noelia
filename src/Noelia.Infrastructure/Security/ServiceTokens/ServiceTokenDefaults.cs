namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// The fixed names and limits of service-to-service tokens.
/// </summary>
/// <remarks>
/// Constants rather than options: the issuer, the person scheme that must refuse
/// these tokens and the receiving scheme that must require them all read the
/// same values, and a value that could be configured differently in two places
/// is how a token type ends up accepted where it was meant to be refused.
/// </remarks>
public static class ServiceTokenDefaults
{
    /// <summary>
    /// The <c>typ</c> header of a service token (RFC 8725 section 3.11), which
    /// keeps it from being read as a person's token and the other way round.
    /// </summary>
    public const string TokenType = "noelia-service+jwt";

    /// <summary>The claim naming the calling service.</summary>
    public const string CallerClaim = "client_id";

    /// <summary>The authentication scheme that accepts service tokens.</summary>
    public const string AuthenticationScheme = "NoeliaService";

    /// <summary>How long a service token lives when nothing else is said.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The shortest lifetime an issuer accepts.</summary>
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(1);

    /// <summary>The longest lifetime an issuer accepts.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(15);
}
