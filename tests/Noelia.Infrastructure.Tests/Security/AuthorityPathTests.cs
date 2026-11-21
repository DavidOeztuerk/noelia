using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// The authority path (<c>jwt.From(authority)</c>) refuses a token that is not the provider's,
/// over a real pipeline, with the provider's key set served by a stub and no network.
/// </summary>
/// <remarks>
/// The stub is wired as JwtBearer's <c>BackchannelHttpHandler</c>: discovery asks it for
/// <c>/.well-known/openid-configuration</c> and then for the <c>jwks_uri</c>, so the keys arrive
/// through the very path a real provider's would. Every wrong token reuses the one RSA key
/// where it can, so a 401 is the verdict on the single thing that differs and not on a key that
/// would have failed anyway.
/// <para>
/// ADR-0008, test plan Q-0018. Each check has a counter-check against a deliberately broken
/// chain, in which the same bad token must get through. A refusal that survives its own removal
/// was never produced by the check under test.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class AuthorityPathTests
{
    private const string Kid = "idp-1";
    private const string SharedSecret =
        "ThisIsATestSecretKeyThatIsAtLeast64CharactersLongForHmacSha256Signing!!";

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly RSA ForeignRsa = RSA.Create(2048);

    private static RsaSecurityKey Key(string kid) => new(Rsa) { KeyId = kid };

    private static SigningCredentials Rs(string algorithm, string kid = Kid) => new(Key(kid), algorithm);

    private static string Good() => JwtChainRig.Sign(Rs(SecurityAlgorithms.RsaSha256));

    /// <summary>One token per case, each differing from <see cref="Good"/> in exactly one respect.</summary>
    private static string Bad(string kind) => kind switch
    {
        // A key the provider never published, under a kid it never published. Not the same key under a
        // new label: the token library falls back to trying every published key when the kid matches
        // none, and a signature the provider's own key verifies is rightly accepted.
        "kid" => JwtChainRig.Sign(new SigningCredentials(
            new RsaSecurityKey(ForeignRsa) { KeyId = "never-published" }, SecurityAlgorithms.RsaSha256)),
        "iss" => JwtChainRig.Sign(Rs(SecurityAlgorithms.RsaSha256), issuer: "https://evil.example.test"),
        "aud" => JwtChainRig.Sign(Rs(SecurityAlgorithms.RsaSha256), audience: "another-api"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Task<JwtChainRig> Chain(Action<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>? tamper = null) =>
        JwtChainRig.StartAsync(
            keys: null,
            authority: JwtChainRig.Issuer,
            backchannel: new IdentityProviderStub(Kid, Rsa),
            tamper: tamper);

    [Fact]
    public async Task The_providers_own_token_gets_in()
    {
        await using var chain = await Chain();

        (await chain.GetAsync(Good())).Should().Be(HttpStatusCode.OK,
            "the control: nothing in the chain is broken, so a 401 below comes from the one thing that differs");
    }

    // BOSS. Unknown kid, wrong iss, wrong aud: each is a 401. The name keeps "Alg" because the
    // quest's boss carries it; the algorithm pin is asserted on the key-ring path below.
    //
    // No "alg" case here, by owner decision of 21.11.2026: on this path the library builds
    // TokenValidationParameters without ValidAlgorithms (ServiceCollectionExtensions
    // .AddJwtAuthentication, the keys-is-null branch), so a token signed RS512 with the provider's
    // own key is accepted. Exploiting that takes the provider's private key; unsigned tokens
    // (RequireSignedTokens) and an HMAC signature over the public key stay refused. Which algorithms
    // of a provider count is a design question of its own, settled in a hardening gate after 7.2.0
    // (#72), where this case comes back red first.
    [Theory]
    [InlineData("kid")]
    [InlineData("iss")]
    [InlineData("aud")]
    public async Task AuthorityPath_RejectsForeignAlgKidIss(string kind)
    {
        await using var chain = await Chain();

        (await chain.GetAsync(Bad(kind))).Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- counter-check: the same bad token against a chain with the check taken out ----------

    [Fact]
    public async Task Counter_check_with_issuer_validation_off_a_foreign_iss_gets_in()
    {
        await using var chain = await Chain(o => o.TokenValidationParameters.ValidateIssuer = false);

        (await chain.GetAsync(Bad("iss"))).Should().Be(HttpStatusCode.OK,
            "so the refusal in the real test is the issuer check and nothing else");
    }

    [Fact]
    public async Task Counter_check_with_audience_validation_off_a_foreign_aud_gets_in()
    {
        await using var chain = await Chain(o => o.TokenValidationParameters.ValidateAudience = false);

        (await chain.GetAsync(Bad("aud"))).Should().Be(HttpStatusCode.OK);
    }

    // ---- the key-ring path, where the library does pin ----------------------------------------

    private static KeyRing HmacRing()
    {
        var shared = SigningKey.FromSharedSecret(SharedSecret, kid: null);
        return new KeyRing([shared], shared);
    }

    private static string Hs(string algorithm) =>
        JwtChainRig.Sign(new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SharedSecret)), algorithm));

    [Fact]
    public async Task The_ring_pins_the_algorithm_of_its_key()
    {
        await using var chain = await JwtChainRig.StartAsync(HmacRing());

        (await chain.GetAsync(Hs(SecurityAlgorithms.HmacSha256))).Should().Be(HttpStatusCode.OK, "the control");
        (await chain.GetAsync(Hs(SecurityAlgorithms.HmacSha512))).Should().Be(
            HttpStatusCode.Unauthorized, "same secret, another algorithm: only the pin refuses it");
    }

    [Fact]
    public async Task Counter_check_with_the_ring_pin_emptied_a_foreign_alg_gets_in()
    {
        await using var chain = await JwtChainRig.StartAsync(
            HmacRing(), tamper: o => o.TokenValidationParameters.ValidAlgorithms = null);

        (await chain.GetAsync(Hs(SecurityAlgorithms.HmacSha512))).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A provider stub for the JwtBearer backchannel: OIDC metadata and the key set, in memory.
    /// </summary>
    private sealed class IdentityProviderStub(string kid, RSA rsa) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new
                {
                    issuer = JwtChainRig.Issuer,
                    jwks_uri = JwtChainRig.Issuer + "/jwks"
                }));
            }

            if (path.EndsWith("/jwks", StringComparison.Ordinal))
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
                    new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = kid });
                return Json(JsonSerializer.Serialize(new
                {
                    keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", n = jwk.N, e = jwk.E } }
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
