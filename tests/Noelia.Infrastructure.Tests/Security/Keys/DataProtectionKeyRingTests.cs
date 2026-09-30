using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Internal;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.Encryption;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Security.Encryption;
using Noelia.Abstractions.Security.Keys;
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
        var store = new InMemoryDataProtectionKeyStore();
        new NoeliaXmlRepository(store, NullLogger<NoeliaXmlRepository>.Instance, cache.Service)
            .StoreElement(new XElement("key", new XAttribute("id", "one")), "one");

        var read = new NoeliaXmlRepository(store, NullLogger<NoeliaXmlRepository>.Instance, cache.Service)
            .GetAllElements();

        read.Should().HaveCount(1);
        read.Single().Attribute("id")!.Value.Should().Be("one");
    }

    [Fact]
    public void Two_elements_do_not_overwrite_each_other()
    {
        var cache = new RecordingCache();
        var repository = new NoeliaXmlRepository(new InMemoryDataProtectionKeyStore(),
            NullLogger<NoeliaXmlRepository>.Instance, cache.Service);

        repository.StoreElement(new XElement("key", new XAttribute("id", "one")), "one");
        repository.StoreElement(new XElement("key", new XAttribute("id", "two")), "two");

        repository.GetAllElements()
            .Select(element => element.Attribute("id")!.Value)
            .Should().BeEquivalentTo(["one", "two"],
                "a key ring stored as one document loses a key whenever two replicas "
                + "create one at the same moment");
    }

    [Fact]
    public void An_indexed_legacy_element_that_is_gone_cannot_be_silently_migrated()
    {
        var cache = new RecordingCache();
        cache.Seed("noelia:dataprotection:index", new NoeliaXmlRepository.KeyRingIndex
            { Ids = ["one", "two"] });
        cache.Seed("noelia:dataprotection:key:one", new NoeliaXmlRepository.StoredElement
            { Xml = new XElement("key", new XAttribute("id", "one")).ToString() });
        cache.Seed("noelia:dataprotection:key:two", new NoeliaXmlRepository.StoredElement
            { Xml = new XElement("key", new XAttribute("id", "two")).ToString() });
        var repository = new NoeliaXmlRepository(new InMemoryDataProtectionKeyStore(),
            NullLogger<NoeliaXmlRepository>.Instance, cache.Service);
        cache.Forget("noelia:dataprotection:key:one");

        Action read = () => repository.GetAllElements();
        read.Should().Throw<InvalidDataException>(
            "a missing revocation must not disappear from the migrated ring");
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
        var store = provider.GetRequiredService<IDataProtectionKeyStore>();
        var repository = new NoeliaXmlRepository(store, NullLogger<NoeliaXmlRepository>.Instance, cache);

        repository.StoreElement(
            new XElement("key", new XAttribute("id", "one"), new XElement("payload", Canary)),
            "one");

        var read = new NoeliaXmlRepository(store, NullLogger<NoeliaXmlRepository>.Instance, cache)
            .GetAllElements();

        read.Should().HaveCount(1);
        read.Single().Element("payload")!.Value.Should().Be(Canary);
    }

    [Fact]
    public async Task A_new_reader_still_sees_keys_and_revocations_after_31_minutes()
    {
        var clock = new AdvancingClock();
        using var memory = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var cache = new InMemoryDistributedCacheService(memory,
            NullLogger<InMemoryDistributedCacheService>.Instance, "keyring-clock:");
        var store = new InMemoryDataProtectionKeyStore();
        var writer = new NoeliaXmlRepository(store, NullLogger<NoeliaXmlRepository>.Instance, cache);
        writer.StoreElement(new XElement("key", new XAttribute("id", "one")), "one");
        writer.StoreElement(new XElement("revocation", new XAttribute("key", "one")), "revocation");
        await cache.SetAsync("probe", new object());
        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        (await cache.GetAsync<object>("probe")).Should().BeNull();

        var replica = new NoeliaXmlRepository(store, NullLogger<NoeliaXmlRepository>.Instance, cache);
        replica.GetAllElements().Select(element => element.Name.LocalName)
            .Should().BeEquivalentTo(["key", "revocation"]);
    }

    /// <summary>
    /// The key ring needs a store and a master key. A cache is only ever a
    /// migration source, so a service that never had one must still start.
    /// </summary>
    [Fact]
    public async Task UseDataProtection_composes_and_round_trips_with_no_cache_registered()
    {
        using var host = await new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [ConfiguredMasterKeyProvider.ConfigurationKey] =
                        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                }))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddSingleton<IDataProtectionKeyStore, InMemoryDataProtectionKeyStore>();
                services.AddConfiguredMasterKey();
                services.AddNoelia(context.Configuration, context.HostingEnvironment, "keyring-nocache",
                    noelia => noelia.UseDataProtection("keyring-nocache"));
            })
            .StartAsync();

        host.Services.GetService<IDistributedCacheService>().Should().BeNull(
            "the test is only meaningful if no cache was registered");

        var protector = host.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("keyring-nocache-purpose");
        var payload = protector.Protect(Canary);

        protector.Unprotect(payload).Should().Be(Canary);

        // The ring really went through the store, encrypted, not to a directory.
        var stored = await host.Services.GetRequiredService<IDataProtectionKeyStore>().ReadAllAsync();
        stored.Should().NotBeEmpty();
        stored.Should().OnlyContain(element => element.Xml.Contains(MasterKeyXmlEncryptor.Element));
    }

    [Fact]
    public void Without_a_cache_the_ring_works_from_the_store_alone()
    {
        var store = new InMemoryDataProtectionKeyStore();
        var repository = new NoeliaXmlRepository(store, NullLogger<NoeliaXmlRepository>.Instance);

        repository.StoreElement(new XElement("key", new XAttribute("id", "one")), "one");

        repository.GetAllElements().Single().Attribute("id")!.Value.Should().Be("one");
    }

    [Fact]
    public void A_successful_legacy_import_is_not_repeated_on_every_read()
    {
        var cache = new RecordingCache();
        cache.Seed("noelia:dataprotection:index", new NoeliaXmlRepository.KeyRingIndex { Ids = ["one"] });
        cache.Seed("noelia:dataprotection:key:one", new NoeliaXmlRepository.StoredElement
            { Xml = new XElement("key", new XAttribute("id", "one")).ToString() });
        var clock = new ManualTime();
        var repository = new NoeliaXmlRepository(new InMemoryDataProtectionKeyStore(),
            NullLogger<NoeliaXmlRepository>.Instance, cache.Service, clock);

        repository.GetAllElements().Should().HaveCount(1);
        cache.Service.ClearReceivedCalls();

        repository.GetAllElements().Should().HaveCount(1);
        repository.GetAllElements().Should().HaveCount(1);

        cache.Service.ReceivedCalls().Should().BeEmpty(
            "inside the re-check interval a read is served from the store alone");
    }

    /// <summary>
    /// The trade-off: a legacy entry an old replica writes during a rolling
    /// upgrade is picked up at the next re-check, not at the next read.
    /// </summary>
    [Fact]
    public void A_legacy_entry_written_after_the_first_import_is_picked_up_at_the_next_recheck()
    {
        var cache = new RecordingCache();
        cache.Seed("noelia:dataprotection:index", new NoeliaXmlRepository.KeyRingIndex { Ids = ["one"] });
        cache.Seed("noelia:dataprotection:key:one", new NoeliaXmlRepository.StoredElement
            { Xml = new XElement("key", new XAttribute("id", "one")).ToString() });
        var clock = new ManualTime();
        var repository = new NoeliaXmlRepository(new InMemoryDataProtectionKeyStore(),
            NullLogger<NoeliaXmlRepository>.Instance, cache.Service, clock);
        repository.GetAllElements().Should().HaveCount(1);

        // An old replica revokes a key after this process finished importing.
        cache.Seed("noelia:dataprotection:index", new NoeliaXmlRepository.KeyRingIndex { Ids = ["one", "two"] });
        cache.Seed("noelia:dataprotection:key:two", new NoeliaXmlRepository.StoredElement
            { Xml = new XElement("revocation", new XAttribute("key", "one")).ToString() });

        repository.GetAllElements().Should().HaveCount(1, "still inside the interval");

        clock.Advance(NoeliaXmlRepository.LegacyRecheckInterval + TimeSpan.FromSeconds(1));

        repository.GetAllElements().Select(element => element.Name.LocalName)
            .Should().BeEquivalentTo(["key", "revocation"]);
    }

    [Fact]
    public void A_failed_legacy_import_is_retried_on_the_next_read()
    {
        var cache = new RecordingCache();
        cache.Seed("noelia:dataprotection:index", new NoeliaXmlRepository.KeyRingIndex { Ids = ["one"] });
        var repository = new NoeliaXmlRepository(new InMemoryDataProtectionKeyStore(),
            NullLogger<NoeliaXmlRepository>.Instance, cache.Service, new ManualTime());

        var first = () => repository.GetAllElements();
        first.Should().Throw<InvalidDataException>();

        cache.Seed("noelia:dataprotection:key:one", new NoeliaXmlRepository.StoredElement
            { Xml = new XElement("key", new XAttribute("id", "one")).ToString() });

        repository.GetAllElements().Should().HaveCount(1,
            "a failure must not be remembered as a completed import");
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
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

        public void Seed(string key, object value) => _entries[key] = value;

        private T? Read<T>(string key) where T : class =>
            _entries.TryGetValue(key, out var value) ? (T?)value : null;
    }

    private sealed class AdvancingClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
}
