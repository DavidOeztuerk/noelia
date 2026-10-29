using System.Buffers.Text;
using System.Security.Cryptography;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// A client secret for one calling service, with the hash its issuer stores.
/// </summary>
/// <remarks>
/// The counterpart of <c>SigningKey.GenerateKeyPair</c> for service tokens: run
/// it once per service, give <see cref="Secret"/> to that service alone and
/// <see cref="Hash"/> to the issuer alone. <see cref="ToEnvironment"/> prints
/// both as the environment lines the two sides read.
/// <para>
/// The secret is 32 random bytes, and that is the entire security argument for
/// storing an unsalted SHA-256 of it: there is nothing to guess, so there is
/// nothing a slow hash would slow down. A secret a person chose does not have
/// that property, which is why the accepted form is narrow and why anything
/// that is not this shape is refused before it is hashed.
/// </para>
/// </remarks>
public sealed class ServiceClientSecret
{
    private ServiceClientSecret(string clientId, string secret)
    {
        ClientId = clientId;
        Secret = secret;
        Hash = HashOf(secret);
    }

    /// <summary>The client this secret belongs to.</summary>
    public string ClientId { get; }

    /// <summary>
    /// The secret, 43 base64url characters. Belongs to the calling service only;
    /// never to the issuer and never to a log.
    /// </summary>
    public string Secret { get; }

    /// <summary>The value the issuer stores: <c>sha256:</c> and 43 characters.</summary>
    public string Hash { get; }

    /// <summary>Draws a new secret from the system's random number generator.</summary>
    /// <param name="clientId">
    /// The calling service's id, lower case, starting with a letter, up to 48
    /// characters of letters, digits and hyphens.
    /// </param>
    /// <exception cref="ArgumentException">The id is not in that form.</exception>
    public static ServiceClientSecret Generate(string clientId)
    {
        if (!ServiceTokenFormat.IsIdentifier(clientId))
        {
            throw new ArgumentException(
                "A client id starts with a lower-case letter and has up to 48 lower-case letters, "
                + "digits and hyphens.",
                nameof(clientId));
        }

        return new ServiceClientSecret(
            clientId, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ServiceTokenFormat.DigestLength)));
    }

    /// <summary>
    /// The two environment lines: the secret for the caller, the hash for the
    /// issuer. They go to different places and are printed together only because
    /// they are made together.
    /// </summary>
    public string ToEnvironment() =>
        $"NOELIA_M2M_CLIENT_SECRET={Secret}\n"
        + $"{ServiceTokenFormat.HashVariable(ClientId, previous: false)}={Hash}";

    /// <summary>
    /// The stored form of a secret: <c>sha256:</c> and the base64url digest of
    /// its 43 ASCII characters. Matches what the shell recipe in the design note
    /// prints, so a secret made with <c>openssl</c> works the same.
    /// </summary>
    /// <exception cref="ArgumentException">The secret is not in the accepted form.</exception>
    public static string HashOf(string secret)
    {
        if (!IsWellFormed(secret))
        {
            throw new ArgumentException(
                "A client secret is exactly 43 base64url characters that decode to 32 bytes.",
                nameof(secret));
        }

        return ServiceTokenFormat.HashOfSecret(secret);
    }

    /// <summary>
    /// Whether a value has the shape of a client secret: 43 base64url characters
    /// that decode canonically to 32 bytes. The shape does not prove the bytes
    /// were random; it only refuses what could not have come from a generator.
    /// </summary>
    public static bool IsWellFormed(string? secret) => ServiceTokenFormat.IsWellFormedSecret(secret);
}
