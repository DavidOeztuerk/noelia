using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Noelia.Abstractions.Security.Encryption;
using Noelia.Redis.Security.Encryption;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Redis;

namespace Noelia.Encryption.Probe;

/// <summary>
/// Consumer-side counter-checks against the packaged Noelia.Redis assembly.
/// There is deliberately no project reference to the Noelia repository.
/// </summary>
public sealed class EncryptionPackageTests
{
    [Fact]
    public void Redis_encryption_is_one_module_with_explicit_provider_requirements()
    {
        var host = Host.CreateApplicationBuilder();
        var noelia = new NoeliaBuilder(
            host.Services, host.Configuration, host.Environment, "redis-probe", [], []);

        noelia.UseRedisEncryption();
        var composition = noelia.Build();
        var contract = composition.Contracts[RedisNoeliaModule.Encryption];

        composition.Included.Should().Equal(RedisNoeliaModule.Encryption);
        contract.Requirements.Select(requirement => requirement.ServiceType).Should().BeEquivalentTo(
            [typeof(IConnectionMultiplexer), typeof(IMasterKeyProvider)]);
        contract.Requirements.SelectMany(requirement => requirement.Providers)
            .Should().Contain(new NoeliaProviderHint(
                "Noelia.Redis", "AddRedisConnection(connectionString, instanceName)"));
    }

    [Fact]
    public async Task Package_encrypts_and_decrypts_compressed_data()
    {
        var key = CreateKey("probe-key");
        var service = CreateService(new Dictionary<string, EncryptionKey> { [key.Id] = key });
        var plaintext = new string('x', 2_048);

        var encrypted = await service.EncryptWithKeyAsync(
            plaintext,
            key.Id,
            new EncryptionOptions { CompressBeforeEncryption = true });
        var decrypted = await service.DecryptWithKeyAsync(encrypted.EncryptedData, key.Id);

        encrypted.Success.Should().BeTrue(encrypted.ErrorMessage);
        decrypted.Success.Should().BeTrue(decrypted.ErrorMessage);
        decrypted.IntegrityVerified.Should().BeTrue();
        decrypted.Data.Should().Be(plaintext);
    }

    [Fact]
    public async Task Package_refuses_a_foreign_key()
    {
        var ownKey = CreateKey("own-key");
        var foreignKey = CreateKey("foreign-key");
        var service = CreateService(new Dictionary<string, EncryptionKey>
        {
            [ownKey.Id] = ownKey,
            [foreignKey.Id] = foreignKey
        });

        var encrypted = await service.EncryptWithKeyAsync("not for the foreign key", ownKey.Id);
        var decrypted = await service.DecryptWithKeyAsync(encrypted.EncryptedData, foreignKey.Id);

        decrypted.Success.Should().BeFalse();
        decrypted.IntegrityVerified.Should().BeFalse();
        decrypted.Data.Should().NotContain("not for the foreign key");
    }

    [Fact]
    public async Task Package_stores_neither_plaintext_nor_a_plaintext_digest()
    {
        const string secret = "consumer-probe-secret-that-must-not-leak";
        var key = CreateKey("leak-probe-key");
        var service = CreateService(new Dictionary<string, EncryptionKey> { [key.Id] = key });

        var encrypted = await service.EncryptWithKeyAsync(secret, key.Id);
        var envelopeBytes = Convert.FromBase64String(encrypted.EncryptedData);
        using var envelope = JsonDocument.Parse(envelopeBytes);
        var ciphertext = Convert.FromBase64String(envelope.RootElement.GetProperty("Data").GetString()!);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var digest = System.Security.Cryptography.SHA256.HashData(plaintext);

        ContainsSubsequence(envelopeBytes, plaintext).Should().BeFalse();
        ContainsSubsequence(ciphertext, plaintext).Should().BeFalse();
        ContainsSubsequence(envelopeBytes, digest).Should().BeFalse();
    }

    [Fact]
    public async Task Packages_442_and_later_authenticate_compression_metadata()
    {
        var key = CreateKey("metadata-probe-key");
        var service = CreateService(new Dictionary<string, EncryptionKey> { [key.Id] = key });
        var encrypted = await service.EncryptWithKeyAsync(
            new string('m', 2_048),
            key.Id,
            new EncryptionOptions { CompressBeforeEncryption = true });

        var envelopeJson = Encoding.UTF8.GetString(Convert.FromBase64String(encrypted.EncryptedData));
        var envelope = JsonNode.Parse(envelopeJson)?.AsObject()
            ?? throw new InvalidOperationException("The package returned no JSON envelope.");
        var implementationVersion = typeof(DataEncryptionService).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Noelia.Redis has no assembly version.");

        if (implementationVersion < new Version(4, 4, 2))
        {
            // Older public baselines wrote envelope 2.0. The authenticated
            // metadata envelope starts with Noelia 4.4.2.
            envelope["Version"]!.GetValue<string>().Should().Be("2.0");
            return;
        }

        envelope["Metadata"]!["compressed"] = "False";
        var tampered = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(envelope));
        var decrypted = await service.DecryptWithKeyAsync(tampered, key.Id);

        envelope["Version"]!.GetValue<string>().Should().Be("2.1");
        decrypted.Success.Should().BeFalse();
        decrypted.IntegrityVerified.Should().BeFalse();
    }

    private static DataEncryptionService CreateService(IReadOnlyDictionary<string, EncryptionKey> keys)
    {
        var keyManagement = Substitute.For<IKeyManagementService>();
        keyManagement.GetKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => keys.GetValueOrDefault(call.ArgAt<string>(0)));

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        return new DataEncryptionService(
            keyManagement,
            Substitute.For<ILogger<DataEncryptionService>>(),
            Options.Create(new DataEncryptionOptions { LogOperations = false }),
            redis);
    }

    private static EncryptionKey CreateKey(string id) => new()
    {
        Id = id,
        Status = KeyStatus.Active,
        KeySize = 256,
        KeyMaterial = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
        KeyType = KeyType.Symmetric,
        Purpose = KeyPurpose.DataEncryption,
        Version = 1,
        ExpiresAt = DateTime.UtcNow.AddYears(1),
        UsageStatistics = new KeyUsageStatistics()
    };

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
