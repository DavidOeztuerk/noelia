using System.Security.Cryptography;
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
        var info = new MasterKeyXmlEncryptor(Key()).Encrypt(new XElement("key", Canary));

        info.EncryptedElement.ToString().Should().NotContain(Canary);
        info.DecryptorType.Should().Be(typeof(MasterKeyXmlDecryptor));
    }

    [Fact]
    public void And_it_comes_back_out_again()
    {
        var original = new XElement("key", new XElement("payload", Canary));

        var info = new MasterKeyXmlEncryptor(Key()).Encrypt(original);
        var round = new MasterKeyXmlDecryptor(Provider(Key())).Decrypt(info.EncryptedElement);

        round.ToString().Should().Be(original.ToString());
    }

    /// <summary>
    /// A second replica holding the same master key reads what the first wrote.
    /// </summary>
    /// <remarks>
    /// The property that makes deriving better than storing here: nothing is
    /// shared between the two but the key the operator already has.
    /// </remarks>
    [Fact]
    public void Another_process_with_the_same_master_key_can_read_it()
    {
        var master = RandomNumberGenerator.GetBytes(32);

        var info = new MasterKeyXmlEncryptor(Key(master)).Encrypt(new XElement("key", Canary));
        var round = new MasterKeyXmlDecryptor(Provider(Key(master))).Decrypt(info.EncryptedElement);

        round.Value.Should().Be(Canary);
    }

    [Fact]
    public void Another_master_key_does_not_open_it()
    {
        var mine = RandomNumberGenerator.GetBytes(32);
        var theirs = RandomNumberGenerator.GetBytes(32);

        var info = new MasterKeyXmlEncryptor(Key(mine)).Encrypt(new XElement("key", Canary));

        var decrypt = () => new MasterKeyXmlDecryptor(Provider(Key(theirs)))
            .Decrypt(info.EncryptedElement);

        decrypt.Should().Throw<InvalidOperationException>().WithMessage("*different master key*");
    }

    /// <summary>
    /// Every byte of the envelope is inside the tag.
    /// </summary>
    /// <remarks>
    /// This is the 4.4.2 finding written as a test. Metadata outside the GCM
    /// tag let an attacker with write access change how the authenticated bytes
    /// were interpreted while decryption still reported integrity.
    /// </remarks>
    [Theory]
    [InlineData(0, "version")]
    [InlineData(3, "salt")]
    [InlineData(20, "nonce")]
    [InlineData(40, "tag or ciphertext")]
    public void A_changed_envelope_byte_is_refused(int index, string part)
    {
        var info = new MasterKeyXmlEncryptor(Key()).Encrypt(new XElement("key", Canary));
        var envelope = Convert.FromBase64String(info.EncryptedElement.Value);
        envelope[index] ^= 0xFF;

        var tampered = new XElement(MasterKeyXmlEncryptor.Element, Convert.ToBase64String(envelope));
        var decrypt = () => new MasterKeyXmlDecryptor(Provider(Key())).Decrypt(tampered);

        decrypt.Should().Throw<InvalidOperationException>($"a changed {part} must not decrypt");
    }

    [Fact]
    public void Two_encryptions_of_the_same_key_differ()
    {
        var encryptor = new MasterKeyXmlEncryptor(Key());
        var element = new XElement("key", Canary);

        var first = encryptor.Encrypt(element).EncryptedElement.Value;
        var second = encryptor.Encrypt(element).EncryptedElement.Value;

        first.Should().NotBe(second, "a fresh salt and nonce per element is what keeps "
            + "one nonce collision from reaching another entry");
    }

    private static readonly byte[] SharedMaster = RandomNumberGenerator.GetBytes(32);

    private static IMasterKeyProvider Key(byte[]? master = null)
    {
        var provider = Substitute.For<IMasterKeyProvider>();
        provider.GetMasterKey().Returns(_ => (byte[])(master ?? SharedMaster).Clone());
        return provider;
    }

    private static IServiceProvider Provider(IMasterKeyProvider keys)
    {
        var services = new ServiceCollection();
        services.AddSingleton(keys);
        return services.BuildServiceProvider();
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
    public void Nonsense_in_the_store_is_not_parsed_as_xml()
    {
        var decrypt = () => new MasterKeyXmlDecryptor(Provider(Key()))
            .Decrypt(new XElement(MasterKeyXmlEncryptor.Element,
                Convert.ToBase64String("far too short to be an envelope"u8.ToArray())));

        decrypt.Should().Throw<InvalidOperationException>()
            .WithMessage("*shape this version wrote*",
                "handing a short buffer to the cipher would report something about "
                + "block sizes, three frames from the cause");
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
