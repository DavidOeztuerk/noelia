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
/// <para><strong>No new dependency.</strong> The ring is stored through
/// <see cref="IDistributedCacheService"/> and encrypted through
/// <see cref="IDataEncryptionService"/> — both Noelia ports. Which server
/// that turns out to be is the operator's decision, exactly as it is for every
/// other piece of state.</para>
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
                builder.Services.AddSingleton<IXmlEncryptor, NoeliaXmlEncryptor>();
                builder.Services.AddSingleton<IXmlDecryptor, NoeliaXmlDecryptor>();

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
                .Requires<IDataEncryptionService>(
                    new NoeliaProviderHint("Noelia.Redis", "UseRedisEncryption()"))
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

/// <summary>Encrypts key ring elements with the registered encryption provider.</summary>
internal sealed class NoeliaXmlEncryptor(IDataEncryptionService encryption) : IXmlEncryptor
{
    private static readonly EncryptionContext Context = new()
    {
        Classification = DataClassification.Restricted,
        Purpose = EncryptionPurpose.Storage
    };

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var plaintext = plaintextElement.ToString(SaveOptions.DisableFormatting);
        var result = encryption.EncryptAsync(plaintext, Context).GetAwaiter().GetResult();

        // A fresh installation has no active data-encryption key, and
        // EncryptAsync says so rather than making one: which keys exist is the
        // key manager's business, not the caller's. The key ring is the first
        // thing a service encrypts, so it is also the first thing to find the
        // drawer empty — it opens it once and tries again.
        if (!result.Success && result.ErrorMessage?.Contains("key", StringComparison.OrdinalIgnoreCase) == true)
        {
            var generated = encryption.GenerateKeyAsync(
                KeyType.Symmetric,
                new KeyGenerationOptions { Purpose = KeyPurpose.DataEncryption, KeySize = 256 })
                .GetAwaiter().GetResult();

            if (generated.Success)
            {
                result = encryption.EncryptAsync(plaintext, Context).GetAwaiter().GetResult();
            }
        }

        // Checked, not assumed. Until this was here the provider's refusal
        // produced an empty <noeliaProtectedKey/>, which ASP.NET stored happily
        // and then failed to read back with "Root element is missing" — three
        // frames and one restart away from the sentence that explained it.
        if (!result.Success || string.IsNullOrWhiteSpace(result.EncryptedData))
        {
            throw new InvalidOperationException(
                "The data protection key ring could not be encrypted: "
                + (result.ErrorMessage ?? "the encryption provider returned nothing")
                + ".");
        }

        return new EncryptedXmlInfo(
            new XElement("noeliaProtectedKey", result.EncryptedData),
            typeof(NoeliaXmlDecryptor));
    }
}

/// <summary>The other half of <see cref="NoeliaXmlEncryptor"/>.</summary>
internal sealed class NoeliaXmlDecryptor(IServiceProvider provider) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var encryption = provider.GetRequiredService<IDataEncryptionService>();

        var result = encryption.DecryptAsync(
            encryptedElement.Value,
            new EncryptionContext
            {
                Classification = DataClassification.Restricted,
                Purpose = EncryptionPurpose.Storage
            }).GetAwaiter().GetResult();

        // Checked, not assumed. IDataEncryptionService reports a refusal in
        // Success and returns an empty string; handing that to XElement.Parse
        // turns a failed decryption into "Root element is missing" three frames
        // away from the cause.
        if (!result.Success || string.IsNullOrWhiteSpace(result.Data))
        {
            throw new InvalidOperationException(
                "The data protection key ring could not be decrypted: "
                + (result.ErrorMessage ?? "the encryption provider returned nothing")
                + ". The master key or the encryption provider is not the one the ring was "
                + "written with.");
        }

        return XElement.Parse(result.Data);
    }
}
