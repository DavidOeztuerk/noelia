using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Security;
using Noelia.Infrastructure.Models;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Keys;
using Noelia.InMemory.Security;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// A session revocation must reach a token that Noelia's own issuer wrote.
/// </summary>
/// <remarks>
/// Until 7.0.1 the issuer wrote the session as <c>session_id</c> while both
/// readers asked for <c>sid</c>, so <c>RevokeSessionAsync</c> recorded an entry
/// nobody ever looked up. These tests go through the real issuer, not through a
/// hand-written claim, because a claim typed into a test is the same name
/// checked twice.
/// </remarks>
[Trait("Category", "Unit")]
public class SessionRevocationClaimTests
{
    private const string Secret = "ThisIsATestSecretKeyThatIsAtLeast64CharactersLongForHmacSha256Signing!!";

    private static (JwtService Jwt, ITokenRevocationWriter Writer, ITokenRevocationEvaluator Evaluator) Build()
    {
        var provider = new ServiceCollection().AddInMemoryTokenRevocation().BuildServiceProvider();
        var writer = provider.GetRequiredService<ITokenRevocationWriter>();
        var evaluator = provider.GetRequiredService<ITokenRevocationEvaluator>();

        var settings = new JwtSettings { Secret = Secret, Issuer = "iss", Audience = "aud", ExpireMinutes = 60 };
        var shared = SigningKey.FromSharedSecret(settings.Secret, kid: null);
        var jwt = new JwtService(
            Options.Create(settings), new KeyRing([shared], shared),
            NullLogger<JwtService>.Instance, null, evaluator, writer);
        return (jwt, writer, evaluator);
    }

    private static UserClaims User(string session) => new()
    {
        UserId = "anna",
        Email = "anna@example.com",
        Roles = ["User"],
        Permissions = [],
        EmailVerified = true,
        AccountStatus = "Active",
        SessionId = session
    };

    [Fact]
    public async Task A_revoked_session_refuses_a_token_the_issuer_wrote()
    {
        var (jwt, writer, _) = Build();
        var token = (await jwt.GenerateTokenAsync(User("s1"))).AccessToken;

        (await jwt.ValidateTokenAsync(token)).Should().NotBeNull();

        await writer.RevokeSessionAsync("anna", "s1", DateTimeOffset.UtcNow.AddMinutes(15), "probe");

        (await jwt.ValidateTokenAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_session_does_not_touch_another_session()
    {
        var (jwt, writer, _) = Build();
        var other = (await jwt.GenerateTokenAsync(User("s2"))).AccessToken;

        await writer.RevokeSessionAsync("anna", "s1", DateTimeOffset.UtcNow.AddMinutes(15), "probe");

        (await jwt.ValidateTokenAsync(other)).Should().NotBeNull();
    }

    [Fact]
    public async Task The_middleware_refuses_a_token_the_issuer_wrote_once_its_session_is_revoked()
    {
        var (jwt, writer, evaluator) = Build();
        var token = (await jwt.GenerateTokenAsync(User("s1"))).AccessToken;
        var principal = (await jwt.ValidateTokenAsync(token))!;

        await writer.RevokeSessionAsync("anna", "s1", DateTimeOffset.UtcNow.AddMinutes(15), "probe");

        var reached = false;
        var middleware = new TokenRevocationMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            evaluator, NullLogger<TokenRevocationMiddleware>.Instance);
        var context = new DefaultHttpContext { User = principal };

        await middleware.InvokeAsync(context);

        reached.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task The_middleware_also_reads_the_standard_sid_claim_of_a_foreign_issuer()
    {
        var provider = new ServiceCollection().AddInMemoryTokenRevocation().BuildServiceProvider();
        var writer = provider.GetRequiredService<ITokenRevocationWriter>();
        var evaluator = provider.GetRequiredService<ITokenRevocationEvaluator>();
        await writer.RevokeSessionAsync("anna", "s1", DateTimeOffset.UtcNow.AddMinutes(15), "probe");

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("jti", "t1"),
            new Claim("sub", "anna"),
            new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new Claim("sid", "s1"),
        ], "test"));

        var reached = false;
        var middleware = new TokenRevocationMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            evaluator, NullLogger<TokenRevocationMiddleware>.Instance);
        var context = new DefaultHttpContext { User = principal };

        await middleware.InvokeAsync(context);

        reached.Should().BeFalse();
    }

    [Fact]
    public void The_issuer_and_the_readers_share_one_claim_name()
    {
        SessionClaims.Issued.Should().Be("session_id");
        SessionClaims.Standard.Should().Be("sid");
    }
}
