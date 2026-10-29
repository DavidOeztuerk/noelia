using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// The person scheme refuses a service token on every path that reads one.
/// </summary>
/// <remarks>
/// A blocklist on one token type, not a list of the types allowed: existing
/// person tokens carry whatever <c>typ</c> their issuer chose, and a whitelist
/// would sign them all out.
/// </remarks>
[Trait("Category", "Unit")]
public class ServiceTokenTypeBlockTests
{
    private static string Token(string? typ) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer,
        Audience = PersonAudience,
        Expires = DateTime.UtcNow.AddMinutes(5),
        Claims = new Dictionary<string, object> { ["sub"] = "user-1" },
        SigningCredentials = SigningKey.FromEcdsaPrivateKey(KeyPair.PrivateKey, KeyPair.Kid).SigningCredentials(),
        TokenType = typ
    });

    [Fact]
    public async Task The_bearer_scheme_with_a_key_ring_refuses_a_service_token_and_accepts_a_person_token()
    {
        using var host = await Start();

        (await Get(host, Token(ServiceTokenDefaults.TokenType))).Should().Be(HttpStatusCode.Unauthorized);
        (await Get(host, Token("at+jwt"))).Should().Be(HttpStatusCode.OK);
        (await Get(host, Token(null))).Should().Be(HttpStatusCode.OK);
        (await Get(host, Token("JWT"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void The_bearer_scheme_with_a_key_ring_carries_the_block()
    {
        var parameters = BearerParameters(keys: true);

        AssertBlocks(parameters);
    }

    /// <summary>The path with no ring at all: keys come from an authority's discovery document.</summary>
    [Fact]
    public void The_bearer_scheme_without_a_key_ring_carries_the_block_too()
    {
        var parameters = BearerParameters(keys: false);

        AssertBlocks(parameters);
    }

    [Fact]
    public void A_validator_that_was_already_set_is_not_swallowed()
    {
        var calls = 0;
        var parameters = new TokenValidationParameters
        {
            TypeValidator = (typ, _, _) => { calls++; return typ; }
        };

        ServiceTokenTypeGuard.Apply(parameters);
        parameters.TypeValidator!("at+jwt", null!, parameters);

        calls.Should().Be(1);
    }

    [Fact]
    public void ValidTypes_that_were_already_set_still_decide_for_other_types()
    {
        var parameters = new TokenValidationParameters { ValidTypes = ["at+jwt"] };

        ServiceTokenTypeGuard.Apply(parameters);

        var refused = () => parameters.TypeValidator!("JWT", null!, parameters);
        refused.Should().Throw<SecurityTokenInvalidTypeException>();
        parameters.TypeValidator!("at+jwt", null!, parameters).Should().Be("at+jwt");
    }

    private static void AssertBlocks(TokenValidationParameters parameters)
    {
        parameters.TypeValidator.Should().NotBeNull();

        var refused = () => parameters.TypeValidator!(ServiceTokenDefaults.TokenType, null!, parameters);
        refused.Should().Throw<SecurityTokenInvalidTypeException>();

        parameters.TypeValidator!("at+jwt", null!, parameters).Should().Be("at+jwt");
        parameters.TypeValidator!("JWT", null!, parameters).Should().Be("JWT");
        parameters.TypeValidator!(null!, null!, parameters).Should().BeNull();
    }

    private static TokenValidationParameters BearerParameters(bool keys)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = Config();
        var ring = keys ? new KeyRing([SigningKey.FromEcdsaPublicKey(KeyPair.PublicKey, KeyPair.Kid)], null) : null;
        services.AddJwtAuthentication(ring, keys ? null : "https://authority.test", config, new Env());

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
    }

    private static async Task<IHost> Start() => await new HostBuilder()
        .ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices((context, services) =>
            {
                services.AddRouting();
                services.AddAuthorization();
                services.AddJwtAuthentication(
                    new KeyRing([SigningKey.FromEcdsaPrivateKey(KeyPair.PrivateKey, KeyPair.Kid)], null),
                    null, Config(), context.HostingEnvironment);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapGet("/me", () => "ok").RequireAuthorization());
            }))
        .StartAsync();

    private static async Task<HttpStatusCode> Get(IHost host, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost/me"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await host.GetTestClient().SendAsync(request);
        return response.StatusCode;
    }
}
