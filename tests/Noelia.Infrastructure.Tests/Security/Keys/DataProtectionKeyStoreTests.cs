using System.Xml.Linq;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Security.Encryption;
using Noelia.Abstractions.Security.Keys;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Redis.Caching;
using Noelia.Infrastructure.Tests.Security;
using Noelia.Redis.Security;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Noelia.Infrastructure.Tests.Security.Keys;

[Trait("Category", "Integration")]
public sealed class RedisDataProtectionKeyStoreTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task Redis_two_replicas_append_every_key_and_revocation_without_expiration()
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Key-ring conformance requires a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"keyring-{Guid.NewGuid():N}";
        var first = new RedisDataProtectionKeyStore(connection, prefix);
        var second = new RedisDataProtectionKeyStore(connection, prefix);
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index =>
            (index % 2 == 0 ? first : second).AppendAsync($"key-{index}",
                new XElement(index == 63 ? "revocation" : "key", new XAttribute("id", index)).ToString())));

        var replica = new RedisDataProtectionKeyStore(connection, prefix);
        var read = await replica.ReadAllAsync();
        read.Should().HaveCount(64);
        read.Select(entry => entry.Id).Should().OnlyHaveUniqueItems();
        read.Should().Contain(entry => entry.Xml.StartsWith("<revocation", StringComparison.Ordinal));
        (await connection.GetDatabase().KeyTimeToLiveAsync($"__noelia:keyring:v2:{prefix}"))
            .Should().BeNull("the key ring may outlive a 31-minute cache window");
        var cache = new RedisDistributedCacheService(connection,
            NullLogger<RedisDistributedCacheService>.Instance, $"{prefix}:", $"{prefix}:tag:");
        await cache.RemoveByPatternAsync("*");
        (await second.ReadAllAsync()).Should().HaveCount(64,
            "clearing ordinary cache data must not remove durable keys or revocations");
    }

    [Fact]
    public async Task Redis_conflicting_retry_cannot_replace_an_existing_key()
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Key-ring conformance requires a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"keyring-conflict-{Guid.NewGuid():N}";
        var store = new RedisDataProtectionKeyStore(connection, prefix);
        await store.AppendAsync("one", "<key id='one'/>");
        await store.AppendAsync("one", "<key id='one'/>");
        var change = () => store.AppendAsync("one", "<key id='other'/>");
        await change.Should().ThrowAsync<InvalidOperationException>();
        (await store.ReadAllAsync()).Single().Xml.Should().Be("<key id='one'/>");
    }

    [Fact]
    public async Task Redis_reader_imports_legacy_cache_ring_before_old_entries_expire()
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Key-ring conformance requires a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"keyring-legacy-{Guid.NewGuid():N}";
        var cache = new RedisDistributedCacheService(connection,
            NullLogger<RedisDistributedCacheService>.Instance,
            keyPrefix: $"{prefix}:", tagPrefix: $"{prefix}:tag:");
        var store = new RedisDataProtectionKeyStore(connection, prefix);
        await cache.SetAsync("noelia:dataprotection:index", new NoeliaXmlRepository.KeyRingIndex
            { Ids = ["legacy-key", "legacy-revocation"] });
        await cache.SetAsync("noelia:dataprotection:key:legacy-key", new NoeliaXmlRepository.StoredElement
            { Xml = "<key id='legacy-key' />" });
        await cache.SetAsync("noelia:dataprotection:key:legacy-revocation", new NoeliaXmlRepository.StoredElement
            { Xml = "<revocation id='legacy-key' />" });

        var repository = new NoeliaXmlRepository(store, cache, NullLogger<NoeliaXmlRepository>.Instance);
        repository.GetAllElements().Should().HaveCount(2);
        await connection.GetDatabase().KeyDeleteAsync($"{prefix}:noelia:dataprotection:index");
        await connection.GetDatabase().KeyDeleteAsync($"{prefix}:noelia:dataprotection:key:legacy-key");
        await connection.GetDatabase().KeyDeleteAsync($"{prefix}:noelia:dataprotection:key:legacy-revocation");

        var fresh = new NoeliaXmlRepository(new RedisDataProtectionKeyStore(connection, prefix), cache,
            NullLogger<NoeliaXmlRepository>.Instance);
        fresh.GetAllElements().Select(element => element.Name.LocalName)
            .Should().BeEquivalentTo(["key", "revocation"]);
    }

    [Fact]
    public async Task Redis_new_replica_reads_rotated_key_and_respects_revocation()
    {
        var connection = fixture.Connection ?? throw new InvalidOperationException(
            "Key-ring conformance requires a real disposable Redis container.", fixture.StartupFailure);
        var prefix = $"keyring-dp-{Guid.NewGuid():N}";
        var master = RandomNumberGenerator.GetBytes(32);
        await using var first = CreateDataProtectionReplica(connection, prefix, master);
        var firstManager = first.GetRequiredService<IKeyManager>();
        var now = DateTimeOffset.UtcNow;
        var rotated = firstManager.CreateNewKey(now, now.AddDays(90));
        var payload = first.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("keyring-conformance").Protect("synthetic-cookie");

        await using var second = CreateDataProtectionReplica(connection, prefix, master);
        second.GetRequiredService<IKeyManager>().GetAllKeys()
            .Should().Contain(key => key.KeyId == rotated.KeyId);
        second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("keyring-conformance").Unprotect(payload).Should().Be("synthetic-cookie");

        firstManager.RevokeKey(rotated.KeyId, "synthetic-revocation");
        await using var third = CreateDataProtectionReplica(connection, prefix, master);
        third.GetRequiredService<IKeyManager>().GetAllKeys()
            .Should().Contain(key => key.KeyId == rotated.KeyId && key.IsRevoked);
        Action unprotect = () => third.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("keyring-conformance").Unprotect(payload);
        unprotect.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task Redis_aof_retains_the_ring_across_its_own_restart()
    {
        await using var container = new RedisBuilder().WithImage("redis:8-alpine")
            .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always")
            .Build();
        await container.StartAsync();
        var prefix = $"keyring-restart-{Guid.NewGuid():N}";
        using (var connection = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString()))
        {
            await new RedisDataProtectionKeyStore(connection, prefix)
                .AppendAsync("key", "<key id='key'/>");
            await new RedisDataProtectionKeyStore(connection, prefix)
                .AppendAsync("revocation", "<revocation id='key'/>");
            await connection.CloseAsync();
        }

        await container.StopAsync();
        await container.StartAsync();
        using var restarted = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        (await new RedisDataProtectionKeyStore(restarted, prefix).ReadAllAsync())
            .Select(entry => entry.Id).Should().BeEquivalentTo(["key", "revocation"]);
    }

    private static ServiceProvider CreateDataProtectionReplica(
        StackExchange.Redis.IConnectionMultiplexer connection, string prefix, byte[] master)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionKeyStore>(new RedisDataProtectionKeyStore(connection, prefix));
        services.AddSingleton<IDistributedCacheService>(new RedisDistributedCacheService(connection,
            NullLogger<RedisDistributedCacheService>.Instance, $"{prefix}:", $"{prefix}:tag:"));
        var keys = Substitute.For<IMasterKeyProvider>();
        keys.GetMasterKey().Returns(_ => (byte[])master.Clone());
        services.AddSingleton(keys);
        services.AddSingleton<IXmlRepository, NoeliaXmlRepository>();
        services.AddSingleton<IXmlEncryptor, MasterKeyXmlEncryptor>();
        services.AddSingleton<IXmlDecryptor, MasterKeyXmlDecryptor>();
        services.AddDataProtection().SetApplicationName("keyring-test");
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<IXmlRepository, IXmlEncryptor>((options, repository, encryptor) =>
            {
                options.XmlRepository = repository;
                options.XmlEncryptor = encryptor;
            });
        return services.BuildServiceProvider();
    }
}
