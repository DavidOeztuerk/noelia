using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security;
using Noelia.Abstractions.Security.Authorization;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Authorization;
using Noelia.Infrastructure.Security.Identity;
using Noelia.Infrastructure.Security.ServiceTokens;
using Noelia.InMemory.Security;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceCallerHost;
using ResourceDecision = Noelia.Abstractions.Security.Authorization.AuthorizationResult;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// A door behind the steps a real service adds to Noelia's chain, in the order
/// Workertransfer has them: the default chain, then revocation, then the principal.
/// </summary>
/// <remarks>
/// <see cref="ServiceCallerDoorTests"/> runs the default chain, which has neither step.
/// That is how 7.1.0 shipped a door no service could open in a service using both.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class ServiceCallerFullChainTests
{
    private const string Account = "/internal/account/7";
    private const string Login = "/people/me";
    private const string Who = "/internal/who";
    private const string Caller = "/internal/caller";
    private const string Required = "/internal/required";
    private const string SignedIn = "/internal/signed-in";
    private const string CapacityDoor = "/internal/capacity";
    private const string ServiceSchemeFirst = "/internal/service-first";
    private const string PersonSchemeFirst = "/internal/person-first";
    private const string NoPrincipal = "no principal";

    private static readonly string Subject = Guid.NewGuid().ToString();

    // ---- who gets in ---------------------------------------------------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_listed_caller_reaches_the_door_behind_revocation_and_the_principal(
        bool revocation, bool principal)
    {
        await using var host = await Chain(revocation, principal);
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Account, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("account 7");
    }

    [Fact]
    public async Task A_service_not_on_the_list_still_gets_the_404_behind_both_steps()
    {
        await using var host = await Chain();
        var token = await Wire.Issued("notification");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Account, token));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- people are refused as they always were ------------------------------

    // The first row is the control: nothing before these two steps refuses the token, so a
    // 401 in the other rows comes from them and not from the scheme.
    [Theory]
    [InlineData(false, false, HttpStatusCode.OK)]
    [InlineData(true, false, HttpStatusCode.Unauthorized)]
    [InlineData(false, true, HttpStatusCode.Unauthorized)]
    [InlineData(true, true, HttpStatusCode.Unauthorized)]
    public async Task A_person_token_without_a_subject_is_refused_by_each_step(
        bool revocation, bool principal, HttpStatusCode expected)
    {
        await using var host = await Chain(revocation, principal);
        var token = Wire.Person(subject: null, tokenId: "person-token");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Login, token));

        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData(false, false, HttpStatusCode.OK)]
    [InlineData(true, false, HttpStatusCode.Unauthorized)]
    [InlineData(false, true, HttpStatusCode.Unauthorized)]
    [InlineData(true, true, HttpStatusCode.Unauthorized)]
    public async Task A_self_set_client_id_does_not_turn_a_person_token_into_a_service(
        bool revocation, bool principal, HttpStatusCode expected)
    {
        await using var host = await Chain(revocation, principal);
        var token = Wire.Person(clientIdClaim: "advisor", subject: null, tokenId: "person-token");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Login, token));

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task A_person_still_gets_in_behind_both_steps()
    {
        await using var host = await Chain();
        var token = Wire.Person(subject: Subject, tokenId: "live-token");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Login, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_revoked_person_token_is_refused()
    {
        var store = await StoreRevoking("revoked-token");
        await using var host = await Chain(evaluator: store);
        var token = Wire.Person(subject: Subject, tokenId: "revoked-token");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Login, token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_evaluator_that_refuses_everything_stops_the_person_and_not_the_service()
    {
        await using var host = await Chain(evaluator: new RefusesEverything());
        var person = Wire.Person(subject: Subject, tokenId: "any-token");
        var service = await Wire.Issued("advisor");

        using var atLogin = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Login, person));
        using var atDoor = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Account, service));

        atLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the evaluator is wired and is asked");
        atDoor.StatusCode.Should().Be(HttpStatusCode.OK, "a service token has no revocation, ADR-0002");
    }

    // ---- what a door handler sees --------------------------------------------

    [Fact]
    public async Task A_door_has_no_principal()
    {
        await using var host = await Chain();
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Who, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(NoPrincipal);
    }

    [Fact]
    public async Task A_service_token_carrying_a_subject_does_not_become_a_person()
    {
        await using var host = await Chain();
        var token = Wire.Service(subject: Subject, tokenId: "service-token");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Who, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(NoPrincipal);
    }

    [Fact]
    public async Task A_service_token_carrying_a_subject_is_not_held_against_the_revocation_list()
    {
        await using var host = await Chain(evaluator: new RefusesEverything());
        var token = Wire.Service(subject: Subject, tokenId: "service-token");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Who, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Code_written_for_people_fails_closed_at_a_door()
    {
        await using var host = await Chain();
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Required, token));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_capacity_policy_on_a_door_is_the_same_404()
    {
        await using var host = await Chain();
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, CapacityDoor, token));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_door_is_authenticated_without_being_a_person()
    {
        await using var host = await Chain();
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, SignedIn, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("False/True");
    }

    [Fact]
    public async Task A_door_handler_reads_the_caller_from_the_user()
    {
        await using var host = await Chain();
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Caller, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("advisor");
    }

    [Fact]
    public async Task A_factory_of_the_service_own_is_not_handed_the_service_identity()
    {
        await using var host = await Chain(factory: new RefusesAnyoneAuthenticated());
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Who, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(NoPrincipal);
    }

    // ---- a person and a service in one principal -----------------------------

    // A policy naming two schemes merges both identities, and which one comes first follows
    // the order of the schemes. Both orders run, because reading claims across identities
    // gives the right answer for one of them and the wrong one for the other.

    [Theory]
    [InlineData(ServiceSchemeFirst)]
    [InlineData(PersonSchemeFirst)]
    public async Task A_revoked_person_next_to_a_service_is_refused(string door)
    {
        var store = await StoreRevoking("revoked-token");
        await using var host = await Chain(evaluator: store);

        using var response = await host.Client.SendAsync(await BothAt(door, "revoked-token"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(ServiceSchemeFirst)]
    [InlineData(PersonSchemeFirst)]
    public async Task A_person_next_to_a_service_is_the_principal_of_the_request(string door)
    {
        var store = await StoreRevoking("revoked-token");
        await using var host = await Chain(evaluator: store);

        using var response = await host.Client.SendAsync(await BothAt(door, "live-token"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(Subject);
    }

    // The control rows again: a service beside the person must not be what lets it through,
    // and it must not be what stops it either.
    [Theory]
    [InlineData(ServiceSchemeFirst, false, false, HttpStatusCode.OK)]
    [InlineData(ServiceSchemeFirst, true, false, HttpStatusCode.Unauthorized)]
    [InlineData(ServiceSchemeFirst, false, true, HttpStatusCode.Unauthorized)]
    [InlineData(PersonSchemeFirst, false, false, HttpStatusCode.OK)]
    [InlineData(PersonSchemeFirst, true, false, HttpStatusCode.Unauthorized)]
    [InlineData(PersonSchemeFirst, false, true, HttpStatusCode.Unauthorized)]
    public async Task A_service_is_no_free_pass_for_a_person_without_a_subject(
        string door, bool revocation, bool principal, HttpStatusCode expected)
    {
        await using var host = await Chain(revocation, principal);

        using var response = await host.Client.SendAsync(
            await BothAt(door, "person-token", HeaderPersonHandler.NoSubject));

        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData(ServiceSchemeFirst)]
    [InlineData(PersonSchemeFirst)]
    public async Task An_identity_nobody_authenticated_next_to_a_service_is_not_a_person(string door)
    {
        // Without permission enforcement, because this is about the steps of ADR-0003.
        // PermissionMiddleware reads only the first identity, and the merge can put the
        // unauthenticated one there; it then answers 401, which fails closed and is a
        // limit of that step, named in the ADR. Once #59 is fixed this runs with the module.
        await using var host = await Chain(permissions: false);

        using var response = await host.Client.SendAsync(
            await BothAt(door, "person-token", HeaderPersonHandler.Unauthenticated));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(NoPrincipal);
    }

    [Fact]
    public async Task A_revoked_person_behind_an_identity_nobody_authenticated_is_still_found()
    {
        var store = await StoreRevoking("revoked-token");
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal([
                new ClaimsIdentity([new Claim("note", "nobody vouches for this")]),
                new ClaimsIdentity(
                    [new Claim("client_id", "advisor"), new Claim("jti", "service-token"), new Claim("iat", issuedAt)],
                    ServiceTokenDefaults.AuthenticationScheme),
                new ClaimsIdentity(
                    [new Claim("sub", Subject), new Claim("jti", "revoked-token"), new Claim("iat", issuedAt)],
                    HeaderPersonHandler.SchemeName)])
        };
        context.Response.Body = new MemoryStream();

        var next = false;
        await new TokenRevocationMiddleware(
                _ =>
                {
                    next = true;
                    return Task.CompletedTask;
                },
                store,
                NullLogger<TokenRevocationMiddleware>.Instance)
            .InvokeAsync(context);

        next.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ---- the check per record ------------------------------------------------

    [Fact]
    public async Task A_listed_caller_reaches_a_controller_door_behind_the_resource_check()
    {
        await using var host = await Chain(resources: RefusesEveryResource());
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/internal-mvc/ping", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the list of the door decides, ADR-0002");
        (await response.Content.ReadAsStringAsync()).Should().Be("pong");
    }

    [Fact]
    public async Task A_person_next_to_a_service_is_still_asked_about_as_the_person()
    {
        var resources = RefusesEveryResource();
        ClaimsPrincipal? asked = null;
        resources.AuthorizeAsync(
                Arg.Do<ClaimsPrincipal>(user => asked = user),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(ResourceDecision.Fail("refused by the test"));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal([
                new ClaimsIdentity([new Claim("client_id", "advisor")], ServiceTokenDefaults.AuthenticationScheme),
                new ClaimsIdentity([new Claim("sub", Subject)], HeaderPersonHandler.SchemeName)])
        };
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/internal-mvc/ping";
        context.Request.Method = "GET";
        context.Request.RouteValues["controller"] = "MvcDoor";

        var next = false;
        await new ResourceAuthorizationMiddleware(
                _ =>
                {
                    next = true;
                    return Task.CompletedTask;
                },
                resources,
                NullLogger<ResourceAuthorizationMiddleware>.Instance)
            .InvokeAsync(context);

        next.Should().BeFalse("the person is refused, whoever stands beside them");
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        asked!.FindFirst("sub")?.Value.Should().Be(Subject);
        asked.FindFirst("client_id").Should().BeNull("the check is about the person, not the service");
    }

    private static IResourceAuthorizationService RefusesEveryResource()
    {
        var resources = Substitute.For<IResourceAuthorizationService>();
        resources.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(ResourceDecision.Fail("refused by the test"));
        return resources;
    }

    private static async Task<HttpRequestMessage> BothAt(string door, string personTokenId, string? kind = null)
    {
        var request = Wire.Request(HttpMethod.Get, door, await Wire.Issued("advisor"));
        request.Headers.Add(HeaderPersonHandler.Header, personTokenId);
        if (kind is not null)
        {
            request.Headers.Add(HeaderPersonHandler.KindHeader, kind);
        }

        return request;
    }

    private static async Task<InMemoryTokenRevocationStore> StoreRevoking(string tokenId)
    {
        var store = new InMemoryTokenRevocationStore();
        await store.RevokeTokenAsync(tokenId, DateTimeOffset.UtcNow.AddHours(1), "test");
        return store;
    }

    private static Task<ServiceCallerHost> Chain(
        bool revocation = true,
        bool principal = true,
        ITokenRevocationEvaluator? evaluator = null,
        IPrincipalFactory? factory = null,
        IResourceAuthorizationService? resources = null,
        bool permissions = true) =>
        StartAsync(settings =>
        {
            settings.Controllers = resources is not null;
            settings.Compose = noelia =>
            {
                noelia
                    .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
                    .UseServiceCallers(ThisAudience)
                    .Use(NoeliaModule.Principal);
                if (!permissions)
                {
                    noelia.Without(NoeliaModule.PermissionEnforcement, "the test is about the steps behind it");
                }
            };
            settings.Services = services =>
            {
                services.AddSingleton(evaluator ?? new InMemoryTokenRevocationStore());
                if (factory is not null)
                {
                    services.AddSingleton(factory);
                }

                if (resources is not null)
                {
                    services.AddSingleton(resources);
                }

                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderPersonHandler>(HeaderPersonHandler.SchemeName, _ => { });
            };
            settings.AfterChain = app =>
            {
                if (revocation)
                {
                    app.UseTokenRevocation();
                }

                if (principal)
                {
                    app.UseNoeliaPrincipal();
                }

                if (resources is not null)
                {
                    app.UseResourceAuthorization();
                }
            };
            settings.Map = endpoints =>
            {
                MapStandard(endpoints);
                MapDoorsOfThisTest(endpoints);
                if (resources is not null)
                {
                    endpoints.MapControllers();
                }
            };
        });

    private static void MapDoorsOfThisTest(IEndpointRouteBuilder endpoints)
    {
        var personScheme = new AuthorizeAttribute { AuthenticationSchemes = HeaderPersonHandler.SchemeName };

        endpoints.MapGet(Who, (ICurrentPrincipal current) => Results.Text(Describe(current)))
            .RequireServiceCaller("advisor");
        endpoints.MapGet(Caller, (HttpContext context) =>
                Results.Text(context.User.FindFirst(ServiceTokenDefaults.CallerClaim)?.Value))
            .RequireServiceCaller("advisor");
        endpoints.MapGet(Required, (ICurrentPrincipal current) => Results.Text(current.Require().ToString()))
            .RequireServiceCaller("advisor");
        endpoints.MapGet(SignedIn, (ICurrentPrincipal current, HttpContext context) =>
                Results.Text($"{current.IsAuthenticated()}/{context.User.Identity?.IsAuthenticated}"))
            .RequireServiceCaller("advisor");
        endpoints.MapGet(CapacityDoor, () => Results.Text("capacity"))
            .RequireServiceCaller("advisor")
            .RequireAuthorization(NoeliaPolicies.ActingAsSelf);
        endpoints.MapGet(ServiceSchemeFirst, (ICurrentPrincipal current) => Results.Text(Describe(current)))
            .RequireServiceCaller("advisor")
            .RequireAuthorization(personScheme);
        endpoints.MapGet(PersonSchemeFirst, (ICurrentPrincipal current) => Results.Text(Describe(current)))
            .RequireAuthorization(personScheme)
            .RequireServiceCaller("advisor");
    }

    private static string Describe(ICurrentPrincipal current) =>
        current.Current is { } principal ? principal.Subject.ToString() : NoPrincipal;

    private sealed class RefusesEverything : ITokenRevocationEvaluator
    {
        public ValueTask<RevocationVerdict> EvaluateAsync(
            TokenIdentity token, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RevocationVerdict(true, RevocationReason.TokenRevoked, false));
    }

    private sealed class RefusesAnyoneAuthenticated : IPrincipalFactory
    {
        public PrincipalResult Create(ClaimsPrincipal? user) =>
            user?.Identity is { IsAuthenticated: true }
                ? PrincipalResult.Invalid("refused by the test")
                : PrincipalResult.Anonymous();
    }

    /// <summary>
    /// A second way of signing a person in, beside the bearer header both JWT schemes read.
    /// Stands in for a cookie: it is what lets one request carry a person and a service.
    /// </summary>
    private sealed class HeaderPersonHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "HeaderPerson";
        public const string Header = "X-Test-Person-Token-Id";
        public const string KindHeader = "X-Test-Person-Kind";
        public const string NoSubject = "no-subject";
        public const string Unauthenticated = "unauthenticated";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var tokenId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var kind = Request.Headers[KindHeader].ToString();
            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var claims = new List<Claim> { new("jti", tokenId.ToString()), new("iat", issuedAt) };
            if (kind != NoSubject)
            {
                claims.Add(new Claim("sub", Subject));
            }

            var identity = new ClaimsIdentity(claims, kind == Unauthenticated ? null : SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
