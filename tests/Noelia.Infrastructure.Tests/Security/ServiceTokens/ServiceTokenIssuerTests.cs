using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// What the issuer accepts, what it refuses, and the shape of what it signs.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceTokenIssuerTests
{
    private static ServiceProvider Provider(params (string Key, string Value)[] more)
    {
        var config = StandardConfig(more);
        return Compose(config, n => Standard(n, config)).BuildServiceProvider();
    }

    [Fact]
    public async Task The_current_secret_is_accepted()
    {
        using var provider = Provider();

        var token = await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("notification", KnownVector, "identity");

        token.Should().NotBeNull();
    }

    [Fact]
    public async Task Both_slots_are_valid_during_a_rotation()
    {
        var old = ServiceClientSecret.Generate("notification");
        using var provider = Provider(
            ("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH_PREVIOUS", old.Hash));
        var issuer = provider.GetRequiredService<IServiceTokenIssuer>();

        (await issuer.IssueAsync("notification", KnownVector, "identity")).Should().NotBeNull();
        (await issuer.IssueAsync("notification", old.Secret, "identity")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_cleared_previous_slot_opens_nothing()
    {
        var old = ServiceClientSecret.Generate("notification");
        using var provider = Provider(("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH_PREVIOUS", ""));
        var issuer = provider.GetRequiredService<IServiceTokenIssuer>();

        (await issuer.IssueAsync("notification", old.Secret, "identity")).Should().BeNull();
        (await issuer.IssueAsync("notification", OtherVector, "identity")).Should().BeNull();
    }

    [Fact]
    public async Task One_clients_secret_does_not_open_another_client()
    {
        using var provider = Provider();

        (await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("advisor", KnownVector, "identity")).Should().BeNull();
    }

    [Theory]
    [InlineData("unknown", ServiceTokenFixture.KnownVector, "identity")]
    [InlineData("notification", ServiceTokenFixture.OtherVector, "identity")]
    [InlineData("notification", ServiceTokenFixture.KnownVector, "elsewhere")]
    [InlineData("notification", ServiceTokenFixture.KnownVector, "persons")]
    [InlineData("notification", ServiceTokenFixture.KnownVector, "")]
    [InlineData("notification", "short", "identity")]
    [InlineData("notification", "", "identity")]
    [InlineData("", ServiceTokenFixture.KnownVector, "identity")]
    [InlineData("Notification", ServiceTokenFixture.KnownVector, "identity")]
    public async Task Every_refusal_is_null(string clientId, string secret, string audience)
    {
        using var provider = Provider();

        (await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync(clientId, secret, audience)).Should().BeNull();
    }

    [Fact]
    public async Task The_token_carries_type_issuer_one_audience_and_the_caller_and_no_subject()
    {
        using var provider = Provider();

        var token = await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("notification", KnownVector, "billing");
        var jwt = new JsonWebToken(token!.AccessToken);

        jwt.Typ.Should().Be("noelia-service+jwt");
        jwt.Issuer.Should().Be(Issuer);
        jwt.Audiences.Should().Equal("billing");
        jwt.GetPayloadValue<string>("client_id").Should().Be("notification");
        jwt.Claims.Select(c => c.Type).Should().NotContain(["sub", "email", "role", "scope", "scp", "permission"]);
        jwt.Id.Should().NotBeNullOrEmpty();
        jwt.Kid.Should().Be(KeyPair.Kid);
        token.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_token_lives_for_the_lifetime_and_has_iat_and_nbf()
    {
        using var provider = Provider();

        var token = await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("notification", KnownVector, "identity");
        var jwt = new JsonWebToken(token!.AccessToken);

        (jwt.ValidTo - jwt.IssuedAt).Should().Be(TimeSpan.FromMinutes(5));
        jwt.ValidFrom.Should().BeCloseTo(jwt.IssuedAt, TimeSpan.FromSeconds(1));
        token.ExpiresIn.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Two_tokens_have_two_identifiers()
    {
        using var provider = Provider();
        var issuer = provider.GetRequiredService<IServiceTokenIssuer>();

        var a = new JsonWebToken((await issuer.IssueAsync("notification", KnownVector, "identity"))!.AccessToken);
        var b = new JsonWebToken((await issuer.IssueAsync("notification", KnownVector, "identity"))!.AccessToken);

        a.Id.Should().NotBe(b.Id);
    }

    /// <summary>Signed with the ring's key and nothing else: the receiver verifies with the public half.</summary>
    [Fact]
    public async Task The_token_verifies_against_the_public_half_of_the_ring()
    {
        using var provider = Provider();
        var token = await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("notification", KnownVector, "billing");

        var verifier = new KeyRing([SigningKey.FromEcdsaPublicKey(KeyPair.PublicKey, KeyPair.Kid)], null);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token!.AccessToken, verifier.ValidationParameters(Issuer, "billing"));

        result.IsValid.Should().BeTrue(result.Exception?.Message);
        result.TokenType.Should().Be("noelia-service+jwt");
    }

    [Fact]
    public async Task The_token_does_not_verify_for_another_audience()
    {
        using var provider = Provider();
        var token = await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("notification", KnownVector, "billing");

        var verifier = new KeyRing([SigningKey.FromEcdsaPublicKey(KeyPair.PublicKey, KeyPair.Kid)], null);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token!.AccessToken, verifier.ValidationParameters(Issuer, "identity"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task The_issuer_follows_the_configured_issuer_value()
    {
        var config = StandardConfig(("JwtSettings:Issuer", "https://elsewhere.test"));
        using var provider = Compose(config, n => Standard(n, config)).BuildServiceProvider();

        var token = await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("notification", KnownVector, "identity");

        new JsonWebToken(token!.AccessToken).Issuer.Should().Be("https://elsewhere.test");
    }
}
