using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Noelia.Abstractions.Security;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Models;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.Sessions;
using Noelia.Infrastructure.Tests.Security.ServiceTokens;
using Noelia.InMemory.Sessions;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// A session revoked through the writer stops that session's access token at a real endpoint, and
/// only that session's, through the chain JwtBearer, revocation step, endpoint.
/// </summary>
/// <remarks>
/// The tokens come out of the library's own <see cref="IJwtService"/>, never from a hand-typed
/// claim: 02-R14 was a claim-name defect (the issuer wrote <c>session_id</c>, the readers asked for
/// <c>sid</c>), and a name typed into a test is the same name checked twice.
/// <para>
/// Reference, not repeated here: JwtBearer, revocation step, revoked token, 401 is already
/// covered for a person's token by
/// <c>ServiceCallerFullChainTests.A_revoked_person_token_is_refused</c> (G-0016). This class adds
/// the session dimension and the sign-out-everywhere promise.
/// </para>
/// ADR-0008, test plan Q-0018. The counter-checks run the same token against a chain without the
/// step, and against one where it stands before authentication: the revoked token must get
/// through there, or the 401 above never came from the step.
/// </remarks>
[Trait("Category", "Unit")]
public class RevocationPipelineE2ETests
{
    private static KeyRing Ring()
    {
        var key = SigningKey.FromEcdsaPrivateKey(ServiceTokenFixture.KeyPair.PrivateKey, ServiceTokenFixture.KeyPair.Kid);
        return new KeyRing([key], key);
    }

    private static Task<JwtChainRig> Chain(RevocationStep step = RevocationStep.AfterAuth) =>
        JwtChainRig.StartAsync(Ring(), revocation: step);

    private static UserClaims Anna(string session) => new()
    {
        UserId = "anna",
        Email = "anna@example.com",
        Roles = ["User"],
        Permissions = [],
        EmailVerified = true,
        AccountStatus = "Active",
        SessionId = session
    };

    private static async Task<string> IssueAsync(JwtChainRig chain, string session)
    {
        using var scope = chain.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        return (await jwt.GenerateTokenAsync(Anna(session))).AccessToken;
    }

    private static Task RevokeAsync(JwtChainRig chain, string session) =>
        chain.Services.GetRequiredService<ITokenRevocationWriter>()
            .RevokeSessionAsync("anna", session, DateTimeOffset.UtcNow.AddMinutes(15), "sign-out");

    // BOSS.
    [Fact]
    public async Task RevocationPipeline_E2E()
    {
        await using var chain = await Chain();
        var ended = await IssueAsync(chain, "s1");
        var other = await IssueAsync(chain, "s2");

        (await chain.GetAsync(ended)).Should().Be(HttpStatusCode.OK, "the control: not revoked yet");

        await RevokeAsync(chain, "s1");

        (await chain.GetAsync(ended)).Should().Be(HttpStatusCode.Unauthorized,
            "the session claim the middleware reads is the one the issuer writes (02-R14)");
        (await chain.GetAsync(other)).Should().Be(HttpStatusCode.OK,
            "another session of the same person is not ended by this one");
    }

    // ---- counter-check: a broken chain lets the revoked token through ------------------------

    [Fact]
    public async Task Counter_check_without_the_revocation_step_a_revoked_session_gets_in()
    {
        await using var chain = await Chain(RevocationStep.Missing);
        var ended = await IssueAsync(chain, "s1");
        await RevokeAsync(chain, "s1");

        (await chain.GetAsync(ended)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Counter_check_with_the_step_before_authentication_a_revoked_session_gets_in()
    {
        await using var chain = await Chain(RevocationStep.BeforeAuth);
        var ended = await IssueAsync(chain, "s1");
        await RevokeAsync(chain, "s1");

        (await chain.GetAsync(ended)).Should().Be(HttpStatusCode.OK,
            "before UseAuthentication the step sees no principal and has nothing to ask about");
    }

    // ---- sign-out-everywhere leaves no grace sibling -----------------------------------------

    [Fact]
    public async Task Signing_out_everywhere_leaves_no_token_inside_the_grace_window()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var options = new TokenSessionOptions();
        var service = new TokenSessionService(
            new InMemoryRefreshTokenStore(), Options.Create(options), clock,
            NullLogger<TokenSessionService>.Instance, new SessionObservations());
        var subject = SubjectId.New();

        var signIn = await service.SignInAsync(subject);
        var rotated = await service.RefreshAsync(signIn.RefreshToken);
        rotated.Outcome.Should().Be(ConsumeOutcome.Rotated, "the control: the predecessor is now inside its grace window");

        await service.SignOutEverywhereAsync(subject);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await service.RefreshAsync(signIn.RefreshToken)).Should().Match<RefreshResult>(
            result => result.Outcome == ConsumeOutcome.Revoked && result.RefreshToken == null,
            "a rotated predecessor inside the window must not mint a sibling after the sign-out");
        (await service.RefreshAsync(rotated.RefreshToken!)).Should().Match<RefreshResult>(
            result => result.Outcome == ConsumeOutcome.Revoked && result.RefreshToken == null,
            "and the successor is closed too: no open token remains");
    }
}
