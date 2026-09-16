using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using AspNetKeyManagementOptions = Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Encryption;

namespace Noelia.Infrastructure.Security.Keys;

/// <summary>
/// Keeps ASP.NET's data protection key ring where a second replica can read it,
/// and encrypted while it is there.
/// </summary>
/// <remarks>
/// <para>Left alone, ASP.NET writes the key ring to a directory inside the
/// container and says so twice at startup:</para>
/// <code>
/// No XML encryptor configured. Key {id} may be persisted to storage in unencrypted form.
/// Storing keys in a directory '…' that may not be persisted outside of the container.
/// </code>
/// <para>Both are true and neither is harmless. Anything protected by that
/// ring — an authentication cookie, an antiforgery token, a password-reset
/// link — stops verifying the moment the container is replaced, and a second
/// replica never verifies what the first one issued at all. Until 5.1.0 Noelia
/// left the warnings to the application: <c>DataProtectionSecretProvider</c>
/// existed and nothing registered it, so the ring was created by the framework
/// and used by nobody.</para>
///
/// <para><strong>A master key, not an encryption service.</strong> The ring is
/// stored through <see cref="IDistributedCacheService"/> and encrypted under the
/// key <see cref="IMasterKeyProvider"/> holds. Requiring a whole
/// <c>IDataEncryptionService</c> would have tied the key ring to a provider
/// package — in practice to Redis — and left every stage without one unable to
/// protect it at all. A key the operator holds is the only thing this needs,
/// and it is the same property SOVEREIGNTY.md calls customer key
/// sovereignty.</para>
/// </remarks>
public static class DataProtectionKeyRing
{
    /// <summary>The module id, so the composition report names it.</summary>
    public static NoeliaModule Module => new("DataProtection");

    /// <summary>
    /// Persists and protects the key ring through the registered providers.
    /// </summary>
    /// <param name="noelia">The composition.</param>
    /// <param name="applicationName">
    /// Separates one application's ring from another's on a shared store, and
    /// ties the purposes derived from it. Two deployments that should verify
    /// each other's tokens share this name; two that should not, must not.
    /// </param>
    public static NoeliaBuilder UseDataProtection(
        this NoeliaBuilder noelia,
        string applicationName)
    {
        ArgumentNullException.ThrowIfNull(noelia);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        return noelia.Use(
            Module,
            builder =>
            {
                builder.Services.AddSingleton<IXmlRepository, NoeliaXmlRepository>();
                builder.Services.AddSingleton<IXmlEncryptor, MasterKeyXmlEncryptor>();
                builder.Services.AddSingleton<IXmlDecryptor, MasterKeyXmlDecryptor>();

                builder.Services.AddDataProtection()
                    .SetApplicationName(applicationName);

                // Resolved from the container rather than handed to the builder:
                // the builder's PersistKeysTo*/ProtectKeysWith* overloads take
                // instances, and these two need services that are registered
                // after this call runs.
                builder.Services.AddSingleton<IConfigureOptions<AspNetKeyManagementOptions>>(
                    provider => new ConfigureKeyManagement(provider));
            },
            contract => contract
                .Requires<IDistributedCacheService>(
                    new NoeliaProviderHint("Noelia.Redis", "UseRedisCache(prefix)"),
                    new NoeliaProviderHint("Noelia.InMemory", "UseInMemoryCache(prefix)"))
                .Requires<IMasterKeyProvider>(
                    new NoeliaProviderHint("Noelia.Infrastructure", "AddConfiguredMasterKey()"),
                    new NoeliaProviderHint("Noelia.Infrastructure", "AddSecretStoreMasterKey()"))
                .Provides<IDataProtectionProvider>(
                    "Noelia.Infrastructure", "UseDataProtection(applicationName)"));
    }

    private sealed class ConfigureKeyManagement(IServiceProvider provider)
        : IConfigureOptions<AspNetKeyManagementOptions>
    {
        public void Configure(AspNetKeyManagementOptions options)
        {
            options.XmlRepository = provider.GetRequiredService<IXmlRepository>();
            options.XmlEncryptor = provider.GetRequiredService<IXmlEncryptor>();
        }
    }
}

/// <summary>Stores the key ring through whichever cache provider is registered.</summary>
internal sealed class NoeliaXmlRepository(
    IDistributedCacheService cache,
    ILogger<NoeliaXmlRepository> logger) : IXmlRepository
{
    /// <summary>
    /// One key per element plus an index, rather than one document.
    /// </summary>
    /// <remarks>
    /// Two replicas creating a key at the same moment would otherwise
    /// read-modify-write the same document and one of them would lose its key —
    /// which surfaces much later as a token that verifies on one instance and
    /// not on another.
    /// </remarks>
    private const string IndexKey = "noelia:dataprotection:index";

    private static string ElementKey(string id) => $"noelia:dataprotection:key:{id}";

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var index = cache.GetAsync<KeyRingIndex>(IndexKey).GetAwaiter().GetResult();
        if (index is null || index.Ids.Count == 0)
        {
            return [];
        }

        var elements = new List<XElement>(index.Ids.Count);
        foreach (var id in index.Ids)
        {
            var stored = cache.GetAsync<StoredElement>(ElementKey(id)).GetAwaiter().GetResult();
            if (stored is null)
            {
                // The index outlived the element. Losing a key is worth a line
                // in the log; refusing to start over it would take the service
                // down for a key it can no longer use anyway.
                logger.LogWarning("Data protection key {KeyId} is indexed but missing", id);
                continue;
            }

            if (string.IsNullOrWhiteSpace(stored.Xml))
            {
                // An entry that came back empty is a store or serializer
                // problem, not a key. Parsing it would fail with "Root element
                // is missing", which names the symptom and not the cause.
                logger.LogError("Data protection key {KeyId} came back empty from the store", id);
                continue;
            }

            elements.Add(XElement.Parse(stored.Xml));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        var id = string.IsNullOrWhiteSpace(friendlyName)
            ? Guid.NewGuid().ToString("N")
            : friendlyName;

        // No expiry. A key ring entry outlives everything protected with it, and
        // an eviction here is a fleet-wide sign-out nobody asked for.
        cache.SetAsync(
                ElementKey(id),
                new StoredElement { Xml = element.ToString(SaveOptions.DisableFormatting) })
            .GetAwaiter().GetResult();

        var index = cache.GetAsync<KeyRingIndex>(IndexKey).GetAwaiter().GetResult()
                    ?? new KeyRingIndex();

        if (!index.Ids.Contains(id, StringComparer.Ordinal))
        {
            index.Ids.Add(id);
            cache.SetAsync(IndexKey, index).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Plain classes with settable properties, not records.
    /// </summary>
    /// <remarks>
    /// These cross a cache provider, which means they cross a serializer. A
    /// positional record with a second parameterless constructor deserialises
    /// to empty strings on System.Text.Json without complaining, and the first
    /// sign of it is ASP.NET reporting "Root element is missing" while parsing
    /// a key it just wrote. Shapes that travel have to be shapes a serializer
    /// cannot misread.
    /// </remarks>
    internal sealed class KeyRingIndex
    {
        public List<string> Ids { get; set; } = [];
    }

    internal sealed class StoredElement
    {
        public string Xml { get; set; } = string.Empty;
    }
}

/// <summary>Encrypts key ring elements under the operator's master key.</summary>
/// <remarks>
/// <para>AES-256-GCM under a key derived per element:
/// <c>HKDF-SHA256(master, salt, "noelia.dataprotection.keyring.v1")</c> with a
/// fresh 16-byte salt each time. Deriving rather than storing means any replica
/// holding the master key can read what another wrote, with nothing shared
/// between them but the key the operator already has.</para>
///
/// <para><strong>The envelope is inside the tag.</strong> Version, salt and
/// nonce are passed to GCM as associated data, so changing any of them makes
/// decryption fail rather than succeed against different bytes. That is the
/// 4.4.2 finding — metadata outside the tag let an attacker with write access
/// change how authenticated bytes were read while integrity still reported
/// fine — and it is not going to be repeated here.</para>
/// </remarks>
internal sealed class MasterKeyXmlEncryptor(IMasterKeyProvider keys) : IXmlEncryptor
{
    internal const string Element = "noeliaProtectedKey";
    internal const byte Version = 1;
    internal static readonly byte[] Info = "noelia.dataprotection.keyring.v1"u8.ToArray();

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];

        var derived = KeyRingKey.Derive(keys, salt);
        try
        {
            using var aes = new AesGcm(derived, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, KeyRingKey.AssociatedData(salt, nonce));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var envelope = new byte[1 + salt.Length + nonce.Length + tag.Length + ciphertext.Length];
        envelope[0] = Version;
        salt.CopyTo(envelope, 1);
        nonce.CopyTo(envelope, 1 + salt.Length);
        tag.CopyTo(envelope, 1 + salt.Length + nonce.Length);
        ciphertext.CopyTo(envelope, 1 + salt.Length + nonce.Length + tag.Length);

        return new EncryptedXmlInfo(
            new XElement(Element, Convert.ToBase64String(envelope)),
            typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>The other half of <see cref="MasterKeyXmlEncryptor"/>.</summary>
internal sealed class MasterKeyXmlDecryptor(IServiceProvider provider) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var keys = provider.GetRequiredService<IMasterKeyProvider>();
        var envelope = Convert.FromBase64String(encryptedElement.Value);

        const int saltLength = 16;
        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        var header = 1 + saltLength + nonceLength + tagLength;

        if (envelope.Length < header || envelope[0] != MasterKeyXmlEncryptor.Version)
        {
            throw new InvalidOperationException(
                "The data protection key ring is not in a shape this version wrote.");
        }

        var salt = envelope.AsSpan(1, saltLength).ToArray();
        var nonce = envelope.AsSpan(1 + saltLength, nonceLength).ToArray();
        var tag = envelope.AsSpan(1 + saltLength + nonceLength, tagLength).ToArray();
        var ciphertext = envelope.AsSpan(header).ToArray();
        var plaintext = new byte[ciphertext.Length];

        var derived = KeyRingKey.Derive(keys, salt);
        try
        {
            using var aes = new AesGcm(derived, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, KeyRingKey.AssociatedData(salt, nonce));
        }
        catch (CryptographicException)
        {
            // The message says what to check and names no value. A ring written
            // under another master key is the overwhelmingly likely cause, and
            // the alternative — that someone changed the bytes — is the reason
            // this throws instead of returning something.
            throw new InvalidOperationException(
                "The data protection key ring did not authenticate. It was written under a "
                + "different master key, or its stored bytes were changed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }

        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>The derivation both halves share.</summary>
internal static class KeyRingKey
{
    internal static byte[] Derive(IMasterKeyProvider keys, byte[] salt)
    {
        var master = keys.GetMasterKey();
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, MasterKeyXmlEncryptor.Info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <summary>
    /// Everything about the envelope that is not the ciphertext, authenticated.
    /// </summary>
    internal static byte[] AssociatedData(byte[] salt, byte[] nonce)
    {
        var data = new byte[1 + salt.Length + nonce.Length];
        data[0] = MasterKeyXmlEncryptor.Version;
        salt.CopyTo(data, 1);
        nonce.CopyTo(data, 1 + salt.Length);
        return data;
    }
}
