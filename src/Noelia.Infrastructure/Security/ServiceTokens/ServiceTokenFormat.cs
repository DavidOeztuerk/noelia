using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// The shapes a client id, a secret and a stored hash must have, in one place.
/// </summary>
/// <remarks>
/// The generator, the register that reads hashes at startup and the endpoint that
/// receives secrets all need the same definition of "well formed". Three copies
/// would drift, and the drift would be a secret one component accepts and another
/// refuses.
/// </remarks>
internal static partial class ServiceTokenFormat
{
    internal const int SecretLength = 43;
    internal const int DigestLength = 32;
    internal const int MaxIdentifierLength = 48;
    internal const string HashPrefix = "sha256:";
    internal const int HashLength = 50;

    // \z, not $: a trailing newline must not slip through an end anchor.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,47}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    /// <summary>Client ids and audiences share one form.</summary>
    internal static bool IsIdentifier(string? value) =>
        value is not null && IdentifierPattern().IsMatch(value);

    /// <summary>
    /// Exactly 43 base64url characters that decode to 32 bytes and re-encode to
    /// the same text. The last character carries only four of its six bits, so a
    /// lenient decoder accepts four spellings of every secret; refusing the
    /// non-canonical ones keeps one secret to one spelling.
    /// </summary>
    internal static bool IsCanonicalBase64Url32(ReadOnlySpan<char> text) =>
        TryDecode32(text, out _);

    internal static bool TryDecode32(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length != SecretLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        var buffer = new byte[DigestLength];
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(text, buffer, out written) || written != DigestLength)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            // Despite the Try name, the decoder throws on a last character whose unused
            // bits are set. That is the non-canonical case, and the answer is the same.
            return false;
        }

        if (!text.SequenceEqual(Base64Url.EncodeToString(buffer)))
        {
            return false;
        }

        bytes = buffer;
        return true;
    }

    internal static bool IsWellFormedSecret(string? secret) =>
        secret is not null && IsCanonicalBase64Url32(secret);

    /// <summary>The stored form: the prefix and the digest of the secret's ASCII characters.</summary>
    internal static string HashOfSecret(string secret) =>
        HashPrefix + Base64Url.EncodeToString(DigestOf(secret));

    internal static byte[] DigestOf(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    /// <summary>Reads a stored hash into its digest, or says it is not one.</summary>
    internal static bool TryParseHash(string? hash, out byte[] digest)
    {
        digest = [];
        return hash is not null
            && hash.Length == HashLength
            && hash.StartsWith(HashPrefix, StringComparison.Ordinal)
            && TryDecode32(hash.AsSpan(HashPrefix.Length), out digest);
    }

    /// <summary>
    /// The variable an operator sets for one client: upper case, hyphen to
    /// underscore. Unambiguous because an underscore never occurs in an id.
    /// </summary>
    internal static string HashVariable(string clientId, bool previous) =>
        "NOELIA_M2M_CLIENT_" + clientId.ToUpperInvariant().Replace('-', '_')
        + (previous ? "_SECRET_HASH_PREVIOUS" : "_SECRET_HASH");
}
