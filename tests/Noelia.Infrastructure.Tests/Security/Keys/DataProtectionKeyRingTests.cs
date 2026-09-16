using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Security.Encryption;
using Noelia.InMemory.Caching;
using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Tests.Security.Keys;

/// <summary>
/// The key ring has to survive the container and stay unreadable in the store.
/// </summary>
/// <remarks>
/// Until 5.1.0 ASP.NET wrote it to a directory inside the container and warned
/// about both facts at every start. Anything protected with that ring — an
/// authentication cookie, an antiforgery token, a reset link — stopped
/// verifying when the container was replaced, and a second replica never
/// verified what the first had issued.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class DataProtectionKeyRingTests
{
    private const string Canary = "NOELIA-KEYRING-CANARY-9f2c41";

    [Fact]
    public void An_element_written_by_one_process_is_read_back_by_the_next()
    {
        var cache = new RecordingCache();

        // Two repositories over one store: the second is the replica that never
        // saw the first one write.
        new NoeliaXmlRepository(cache.Service, NullLogger<NoeliaXmlRepository>.Instance)
            .StoreElement(new XElement("key", new XAttribute("id", "one")), "one");

        var read = new NoeliaXmlRepository(cache.Service, NullLogger<NoeliaXmlRepository>.Instance)
            .GetAllElements();

        read.Should().HaveCount(1);
        read.Single().Attribute("id")!.Value.Should().Be("one");
    }

    [Fact]
    public void Two_elements_do_not_overwrite_each_other()
    {
        var cache = new RecordingCache();
        var repository = new NoeliaXmlRepository(cache.Service, NullLogger<NoeliaXmlRepository>.Instance);

        repository.StoreElement(new XElement("key", new XAttribute("id", "one")), "one");
        repository.StoreElement(new XElement("key", new XAttribute("id", "two")), "two");

        repository.GetAllElements()
            .Select(element => element.Attribute("id")!.Value)
            .Should().BeEquivalentTo(["one", "two"],
                "a key ring stored as one document loses a key whenever two replicas "
                + "create one at the same moment");
    }

    [Fact]
    public void An_indexed_element_that_is_gone_does_not_stop_the_service()
    {
        var cache = new RecordingCache();
        var repository = new NoeliaXmlRepository(cache.Service, NullLogger<NoeliaXmlRepository>.Instance);

        repository.StoreElement(new XElement("key", new XAttribute("id", "one")), "one");
        repository.StoreElement(new XElement("key", new XAttribute("id", "two")), "two");
        cache.Forget("noelia:dataprotection:key:one");

        var read = repository.GetAllElements();

        read.Should().HaveCount(1, "a key that is gone is already unusable; refusing to "
            + "start would take the service down for it as well");
    }

    [Fact]
    public void What_reaches_the_store_is_not_the_key()
    {
        var encryptor = new NoeliaXmlEncryptor(new ReversingEncryption());

        var info = encryptor.Encrypt(new XElement("key", Canary));

        info.EncryptedElement.ToString().Should().NotContain(Canary);
        info.DecryptorType.Should().Be(typeof(NoeliaXmlDecryptor));
    }

    [Fact]
    public void And_it_comes_back_out_again()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDataEncryptionService>(new ReversingEncryption());
        using var provider = services.BuildServiceProvider();

        var original = new XElement("key", Canary);
        var info = new NoeliaXmlEncryptor(new ReversingEncryption()).Encrypt(original);

        var round = new NoeliaXmlDecryptor(provider).Decrypt(info.EncryptedElement);

        round.ToString().Should().Be(original.ToString());
    }

    /// <summary>
    /// Through a real cache provider, which means through a real serializer.
    /// </summary>
    /// <remarks>
    /// Written after the substitute above missed a defect it could not see. A
    /// substitute hands the same object back; a provider writes it out and
    /// reads it in. The stored shapes were positional records with a second
    /// parameterless constructor, which System.Text.Json deserialises to empty
    /// strings without complaining — and the first sign of it was ASP.NET
    /// reporting "Root element is missing" while parsing a key it had just
    /// written.
    /// </remarks>
    [Fact]
    public async Task An_element_survives_a_provider_that_serialises_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache();
        services.AddInMemoryCache("keyring-probe");
        await using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<IDistributedCacheService>();
        var repository = new NoeliaXmlRepository(cache, NullLogger<NoeliaXmlRepository>.Instance);

        repository.StoreElement(
            new XElement("key", new XAttribute("id", "one"), new XElement("payload", Canary)),
            "one");

        var read = new NoeliaXmlRepository(cache, NullLogger<NoeliaXmlRepository>.Instance)
            .GetAllElements();

        read.Should().HaveCount(1);
        read.Single().Element("payload")!.Value.Should().Be(Canary);
    }

    [Fact]
    public void A_refused_decryption_is_not_parsed_as_xml()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDataEncryptionService>(new RefusingEncryption());
        using var provider = services.BuildServiceProvider();

        var decrypt = () => new NoeliaXmlDecryptor(provider)
            .Decrypt(new XElement("noeliaProtectedKey", "not-the-right-ciphertext"));

        decrypt.Should().Throw<InvalidOperationException>()
            .WithMessage("*master key*",
                "handing an empty result to XElement.Parse reports 'Root element is missing' "
                + "three frames from the cause");
    }

    /// <summary>Refuses, the way the shipped provider refuses: by saying so.</summary>
    private sealed class RefusingEncryption : ReversingEncryption
    {
        public override Task<DecryptionResult> DecryptAsync(
            string encryptedData, EncryptionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DecryptionResult { Success = false, Data = string.Empty });
    }

    /// <summary>
    /// Stands in for a provider. Reversing is not encryption, and that is the
    /// point: these tests assert that the repository and the encryptor are
    /// wired to each other, not that AES works.
    /// </summary>
    private class ReversingEncryption : IDataEncryptionService
    {
        public Task<EncryptionResult> EncryptAsync(
            string data, EncryptionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EncryptionResult { EncryptedData = Reverse(data) });

        public virtual Task<DecryptionResult> DecryptAsync(
            string encryptedData, EncryptionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DecryptionResult { Success = true, Data = Reverse(encryptedData) });

        private static string Reverse(string value)
        {
            var characters = value.ToCharArray();
            Array.Reverse(characters);
            return new string(characters);
        }

        public Task<EncryptionResult> EncryptWithKeyAsync(string data, string keyId, EncryptionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DecryptionResult> DecryptWithKeyAsync(string encryptedData, string keyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HashResult> HashAsync(string data, HashingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> VerifyHashAsync(string data, string hashedData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KeyGenerationResult> GenerateKeyAsync(KeyType keyType, KeyGenerationOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KeyRotationResult> RotateKeyAsync(string keyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EncryptionResult> ReEncryptAsync(string encryptedData, string oldKeyId, string newKeyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SecureDeleteAsync(string keyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// A cache that keeps what it was given, so the test can look.
    /// </summary>
    /// <remarks>
    /// A substitute rather than a hand-written class: <c>IDistributedCacheService</c>
    /// carries a dozen members this repository never touches, and stubbing each
    /// one to throw would be a page of noise around three lines that matter.
    /// </remarks>
    private sealed class RecordingCache
    {
        private readonly Dictionary<string, object> _entries = new(StringComparer.Ordinal);

        public IDistributedCacheService Service { get; }

        public RecordingCache()
        {
            Service = Substitute.For<IDistributedCacheService>();

            Service.GetAsync<NoeliaXmlRepository.KeyRingIndex>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Read<NoeliaXmlRepository.KeyRingIndex>(call.Arg<string>()));

            Service.GetAsync<NoeliaXmlRepository.StoredElement>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Read<NoeliaXmlRepository.StoredElement>(call.Arg<string>()));

            Service.SetAsync(
                    Arg.Any<string>(),
                    Arg.Any<object>(),
                    Arg.Any<TimeSpan?>(),
                    Arg.Any<CacheOptions?>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    _entries[call.ArgAt<string>(0)] = call.ArgAt<object>(1);
                    return Task.CompletedTask;
                });
        }

        public void Forget(string key) => _entries.Remove(key);

        private T? Read<T>(string key) where T : class =>
            _entries.TryGetValue(key, out var value) ? (T?)value : null;
    }
}
