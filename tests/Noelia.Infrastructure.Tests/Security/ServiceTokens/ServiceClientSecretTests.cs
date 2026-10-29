using System.Security.Cryptography;
using System.Text;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// The secret an operator generates, and the hash the issuer stores for it.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceClientSecretTests
{
    [Fact]
    public void A_generated_secret_is_43_base64url_characters_that_decode_to_32_bytes()
    {
        var secret = ServiceClientSecret.Generate("notification");

        secret.Secret.Should().HaveLength(43);
        secret.Secret.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        ServiceClientSecret.IsWellFormed(secret.Secret).Should().BeTrue();
    }

    [Fact]
    public void Two_generated_secrets_differ()
    {
        ServiceClientSecret.Generate("a").Secret
            .Should().NotBe(ServiceClientSecret.Generate("a").Secret);
    }

    [Fact]
    public void The_hash_is_sha256_prefixed_and_50_characters_long()
    {
        var secret = ServiceClientSecret.Generate("notification");

        secret.Hash.Should().StartWith("sha256:").And.HaveLength(50);
        secret.Hash.Should().Be(ServiceClientSecret.HashOf(secret.Secret));
    }

    /// <summary>
    /// The pair the ADR's shell recipe prints. Held as a literal on purpose: a
    /// test that recomputed the hash with the code under test would agree with
    /// it whatever it did.
    /// </summary>
    [Fact]
    public void The_hash_of_a_known_secret_equals_what_the_shell_recipe_prints()
    {
        ServiceClientSecret.HashOf(ServiceTokenFixture.KnownVector)
            .Should().Be(ServiceTokenFixture.KnownHash);
        ServiceClientSecret.HashOf(ServiceTokenFixture.OtherVector)
            .Should().Be(ServiceTokenFixture.OtherHash);
    }

    /// <summary>The same arithmetic, written out once more without the type under test.</summary>
    [Fact]
    public void The_hash_is_the_sha256_of_the_ascii_characters_not_of_the_decoded_bytes()
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(ServiceTokenFixture.KnownVector));
        var expected = "sha256:" + Convert.ToBase64String(digest)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        ServiceClientSecret.HashOf(ServiceTokenFixture.KnownVector).Should().Be(expected);
    }

    [Fact]
    public void ToEnvironment_prints_the_secret_for_the_caller_and_the_hash_for_the_issuer()
    {
        var secret = ServiceClientSecret.Generate("my-service");

        var lines = secret.ToEnvironment().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToArray();

        lines.Should().HaveCount(2);
        lines[0].Should().Be($"NOELIA_M2M_CLIENT_SECRET={secret.Secret}");
        lines[1].Should().Be($"NOELIA_M2M_CLIENT_MY_SERVICE_SECRET_HASH={secret.Hash}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("vTFQKHO5s-d6tcHO3FIL9VsySJbH4BtwgcszEdEnyb")]    // 42
    [InlineData("vTFQKHO5s-d6tcHO3FIL9VsySJbH4BtwgcszEdEnybwA")]  // 44
    [InlineData("vTFQKHO5s+d6tcHO3FIL9VsySJbH4BtwgcszEdEnybw")]   // '+'
    [InlineData("vTFQKHO5s/d6tcHO3FIL9VsySJbH4BtwgcszEdEnybw")]   // '/'
    [InlineData("vTFQKHO5s=d6tcHO3FIL9VsySJbH4BtwgcszEdEnybw")]   // '='
    [InlineData("vTFQKHO5s-d6tcHO3FIL9VsySJbH4BtwgcszEdEnybx")]   // last char leaves unused bits set
    public void A_secret_in_another_shape_is_not_well_formed(string secret)
    {
        ServiceClientSecret.IsWellFormed(secret).Should().BeFalse();
    }

    [Fact]
    public void Null_is_not_well_formed()
    {
        ServiceClientSecret.IsWellFormed(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Notification")]
    [InlineData("1abc")]
    [InlineData("with_underscore")]
    public void A_client_id_the_register_would_refuse_cannot_be_generated_for(string clientId)
    {
        var generate = () => ServiceClientSecret.Generate(clientId);

        generate.Should().Throw<ArgumentException>();
    }
}
