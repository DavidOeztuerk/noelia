using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Security;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Authorization;
using Noelia.Infrastructure.Security.ServiceTokens;
using Noelia.InMemory.Security;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// ADR-0007 (#59), decision 4: revocation and the check per record go person identity by person
/// identity, each read from its own identity and never across two, and every one has to pass.
/// With no person both steps let the request through, as before.
/// </summary>
[Trait("Category", "Unit")]
public class PersonChecksMiddlewareAuthenticatedIdentityTests
{
    private static readonly string Now =
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static ClaimsIdentity Person(string sub, string jti, string? authenticationType = "Bearer", params Claim[] more) =>
        new(
            [new Claim("sub", sub), new Claim("jti", jti), new Claim("iat", Now), .. more],
            authenticationType);

    private static ClaimsIdentity Service() =>
        new([new Claim("client_id", "advisor"), new Claim("jti", "service-token"), new Claim("iat", Now)],
            ServiceTokenDefaults.AuthenticationScheme);

    private static async Task<InMemoryTokenRevocationStore> Revoking(string jti = "", string? sub = null, string? session = null)
    {
        var store = new InMemoryTokenRevocationStore();
        if (jti.Length > 0)
        {
            await store.RevokeTokenAsync(jti, DateTimeOffset.UtcNow.AddHours(1), "test");
        }

        if (session is not null)
        {
            await store.RevokeSessionAsync(sub!, session, DateTimeOffset.UtcNow.AddHours(1), "test");
        }

        return store;
    }

    private sealed class Recording : ITokenRevocationEvaluator
    {
        public readonly List<TokenIdentity> Asked = [];

        public ValueTask<RevocationVerdict> EvaluateAsync(TokenIdentity token, CancellationToken cancellationToken = default)
        {
            Asked.Add(token);
            return ValueTask.FromResult(new RevocationVerdict(false, RevocationReason.None, false));
        }
    }

    private static async Task<(int Status, bool NextCalled)> Revocation(ITokenRevocationEvaluator evaluator, ClaimsPrincipal user)
    {
        var context = new DefaultHttpContext { User = user };
        context.Response.Body = new MemoryStream();
        var next = false;
        await new TokenRevocationMiddleware(
                _ =>
                {
                    next = true;
                    return Task.CompletedTask;
                },
                evaluator,
                NullLogger<TokenRevocationMiddleware>.Instance)
            .InvokeAsync(context);
        return (context.Response.StatusCode, next);
    }

    private static ClaimsPrincipal Both(bool firstIsA, ClaimsIdentity a, ClaimsIdentity b) =>
        firstIsA ? new ClaimsPrincipal([a, b]) : new ClaimsPrincipal([b, a]);

    // ---- revocation -------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevokedPerson_BesideAnIdentityNobodySignedIn_IsRefused(bool strangerFirst)
    {
        var store = await Revoking("revoked");
        var stranger = new ClaimsIdentity([new Claim("note", "nobody vouches")]);
        var person = Person("alice", "revoked");
        var user = strangerFirst ? new ClaimsPrincipal([stranger, person]) : new ClaimsPrincipal([person, stranger]);

        var (status, next) = await Revocation(store, user);

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoPersons_OneRevoked_IsRefusedInEitherOrder(bool revokedFirst)
    {
        var store = await Revoking("revoked");

        var (status, next) = await Revocation(
            store, Both(revokedFirst, Person("alice", "revoked"), Person("bob", "fine")));

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task TwoPersons_AreEachAskedAboutTheirOwnJtiSubIat()
    {
        var evaluator = new Recording();

        var (_, next) = await Revocation(
            evaluator, new ClaimsPrincipal([Person("alice", "jti-a"), Person("bob", "jti-b")]));

        next.Should().BeTrue();
        evaluator.Asked.Select(token => (token.TokenId, token.SubjectId))
            .Should().BeEquivalentTo([("jti-a", "alice"), ("jti-b", "bob")]);
    }

    [Fact]
    public async Task ASubjectIsNeverPairedWithAJtiOfAnotherIdentity()
    {
        // The second identity has jti and iat but no subject, the first only a subject: read across
        // the two this looked like one complete token. Each on its own is not checkable.
        var evaluator = new Recording();
        var half = new ClaimsIdentity([new Claim("sub", "alice")], "Bearer");
        var other = new ClaimsIdentity([new Claim("jti", "jti-b"), new Claim("iat", Now)], "Bearer");

        var (status, next) = await Revocation(evaluator, new ClaimsPrincipal([half, other]));

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status401Unauthorized);
        evaluator.Asked.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoPersons_SessionOfTheSecondRevoked_WithMixedSidAndSessionId_IsRefused(bool revokedFirst)
    {
        var store = await Revoking(sub: "bob", session: "bobs-session");
        var alice = Person("alice", "jti-a", "Bearer", new Claim("session_id", "alices-session"));
        var bob = Person("bob", "jti-b", "Bearer", new Claim("sid", "bobs-session"));

        var (status, next) = await Revocation(store, Both(revokedFirst, bob, alice));

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task TheSessionComesFromTheIdentityOfTheToken_NotFromAnotherOne()
    {
        var evaluator = new Recording();
        var alice = Person("alice", "jti-a", "Bearer", new Claim("session_id", "alices-session"));
        var bob = Person("bob", "jti-b", "Bearer", new Claim("sid", "bobs-session"));
        var carol = Person("carol", "jti-c");

        await Revocation(evaluator, new ClaimsPrincipal([carol, alice, bob]));

        evaluator.Asked.Single(token => token.TokenId == "jti-c").SessionId.Should().BeNull();
        evaluator.Asked.Single(token => token.TokenId == "jti-a").SessionId.Should().Be("alices-session");
        evaluator.Asked.Single(token => token.TokenId == "jti-b").SessionId.Should().Be("bobs-session");
    }

    [Fact]
    public async Task ASecondPersonWithoutAJti_IsRefused()
    {
        var evaluator = new Recording();
        var noJti = new ClaimsIdentity([new Claim("sub", "bob"), new Claim("iat", Now)], "Cookie");

        var (status, next) = await Revocation(evaluator, new ClaimsPrincipal([Person("alice", "jti-a"), noJti]));

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unauthenticated")]
    [InlineData("service")]
    [InlineData("service+unauthenticated")]
    public async Task NoPerson_LetsTheRequestThrough_AsBefore(string shape)
    {
        var store = await Revoking("service-token");
        var stranger = new ClaimsIdentity([new Claim("sub", "x"), new Claim("jti", "revoked")]);
        var user = shape switch
        {
            "none" => new ClaimsPrincipal(),
            "unauthenticated" => new ClaimsPrincipal(stranger),
            "service" => new ClaimsPrincipal(Service()),
            _ => new ClaimsPrincipal([Service(), stranger])
        };

        var (_, next) = await Revocation(store, user);

        next.Should().BeTrue("this step only filters; the refusal comes from authentication and permissions");
    }

    // ---- the check per record ---------------------------------------------------------------

    private static async Task<(int Status, bool NextCalled)> PerRecord(
        PersonCheckRig rig, ClaimsPrincipal user, Action<DefaultHttpContext>? tweak = null)
    {
        var context = new DefaultHttpContext { User = user };
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/orders/42";
        context.Request.Method = "GET";
        context.Request.RouteValues["controller"] = "Orders";
        context.Request.RouteValues["id"] = "42";
        tweak?.Invoke(context);
        var next = false;
        await new ResourceAuthorizationMiddleware(
                _ =>
                {
                    next = true;
                    return Task.CompletedTask;
                },
                rig.Resources,
                NullLogger<ResourceAuthorizationMiddleware>.Instance)
            .InvokeAsync(context);
        return (context.Response.StatusCode, next);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PerRecord_AnIdentityNobodySignedInWithAForeignId_IsNeverAskedAbout(bool strangerFirst)
    {
        var rig = new PersonCheckRig();
        rig.Refused.Add("victim");
        var stranger = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "victim")]);
        var person = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Bearer");
        var user = strangerFirst ? new ClaimsPrincipal([stranger, person]) : new ClaimsPrincipal([person, stranger]);

        var (status, next) = await PerRecord(rig, user);

        next.Should().BeTrue();
        status.Should().Be(StatusCodes.Status200OK);
        rig.Asked.Select(principal => principal.FindFirst(ClaimTypes.NameIdentifier)!.Value).Should().Equal("alice");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PerRecord_TwoPersons_EveryOneMustPass(bool refusedFirst)
    {
        var rig = new PersonCheckRig();
        rig.Refused.Add("bob");
        var alice = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Bearer");
        var bob = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bob")], "Cookie");

        var (status, next) = await PerRecord(rig, Both(refusedFirst, bob, alice));

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task PerRecord_TwoPersons_WithoutAJti_AreNotRefusedByARevocationLogic()
    {
        var rig = new PersonCheckRig();
        var alice = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Bearer");
        var bob = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bob")], "Cookie");

        var (status, next) = await PerRecord(rig, new ClaimsPrincipal([alice, bob]));

        next.Should().BeTrue();
        status.Should().Be(StatusCodes.Status200OK);
        rig.Asked.Should().HaveCount(2);
    }

    [Fact]
    public async Task PerRecord_APersonWithoutAUserId_IsRefused()
    {
        var rig = new PersonCheckRig();
        rig.Resources.AuthorizeAsync(
                Arg.Is<ClaimsPrincipal>(user => user.FindFirst(ClaimTypes.NameIdentifier) == null),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Noelia.Abstractions.Security.Authorization.AuthorizationResult.Fail("no id"));
        var alice = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Bearer");
        var nameless = new ClaimsIdentity([new Claim("note", "x")], "Cookie");

        var (status, next) = await PerRecord(rig, new ClaimsPrincipal([alice, nameless]));

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task PerRecord_AProviderThatThrows_RefusesInsteadOfLettingThrough()
    {
        var rig = new PersonCheckRig();
        rig.Resources.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Noelia.Abstractions.Security.Authorization.AuthorizationResult>(
                _ => throw new InvalidOperationException("the store is down"));
        var alice = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Bearer");

        var (status, next) = await PerRecord(rig, new ClaimsPrincipal(alice));

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unauthenticated")]
    [InlineData("service")]
    public async Task PerRecord_NoPerson_LetsTheRequestThrough_AsBefore(string shape)
    {
        var rig = new PersonCheckRig();
        rig.Refused.Add("victim");
        var user = shape switch
        {
            "none" => new ClaimsPrincipal(),
            "unauthenticated" => new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "victim")])),
            _ => new ClaimsPrincipal(Service())
        };

        var (_, next) = await PerRecord(rig, user);

        next.Should().BeTrue();
        rig.Asked.Should().BeEmpty();
    }
}
