using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Noelia.Infrastructure.Models;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// A service token is not a person's token, in the two places the person path
/// reads tokens in code.
/// </summary>
/// <remarks>
/// The token below is the worst case: right key, right issuer, and the person
/// audience. Only its <c>typ</c> tells it apart, and that is what must be enough.
/// </remarks>
[Trait("Category", "Unit")]
public class JwtServiceServiceTokenTypeTests
{
    private const string Issuer = "https://identity.test";
    private const string Audience = "persons";

    private static readonly GeneratedKeyPair Pair = SigningKey.GenerateKeyPair("typ-test");

    private static JwtService Service()
    {
        var ring = new KeyRing([SigningKey.FromEcdsaPrivateKey(Pair.PrivateKey, Pair.Kid)],
            SigningKey.FromEcdsaPrivateKey(Pair.PrivateKey, Pair.Kid));
        return new JwtService(
            Options.Create(new JwtSettings { Issuer = Issuer, Audience = Audience, ExpireMinutes = 15 }),
            ring,
            NullLogger<JwtService>.Instance);
    }

    private static string Token(string? typ, DateTime? expires = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            NotBefore = expires is null ? null : expires.Value.AddMinutes(-10),
            IssuedAt = expires is null ? null : expires.Value.AddMinutes(-10),
            Claims = new Dictionary<string, object> { ["sub"] = "user-1", ["jti"] = Guid.NewGuid().ToString() },
            SigningCredentials = SigningKey.FromEcdsaPrivateKey(Pair.PrivateKey, Pair.Kid).SigningCredentials(),
            TokenType = typ
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task ValidateTokenAsync_refuses_a_service_token()
    {
        (await Service().ValidateTokenAsync(Token(ServiceTokenDefaults.TokenType))).Should().BeNull();
    }

    [Fact]
    public async Task GetPrincipalFromExpiredTokenAsync_refuses_a_service_token_even_though_expiry_is_ignored()
    {
        var expired = DateTime.UtcNow.AddMinutes(-30);

        (await Service().GetPrincipalFromExpiredTokenAsync(Token(ServiceTokenDefaults.TokenType, expired)))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("JWT")]
    [InlineData("at+jwt")]
    [InlineData("something-else+jwt")]
    public async Task A_person_token_with_any_other_typ_stays_valid(string? typ)
    {
        (await Service().ValidateTokenAsync(Token(typ))).Should().NotBeNull();
    }

    [Fact]
    public async Task An_expired_person_token_with_another_typ_can_still_be_read_for_refresh()
    {
        var expired = DateTime.UtcNow.AddMinutes(-30);

        (await Service().GetPrincipalFromExpiredTokenAsync(Token("at+jwt", expired))).Should().NotBeNull();
    }
}
