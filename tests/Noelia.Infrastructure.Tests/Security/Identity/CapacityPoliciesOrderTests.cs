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
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Identity;
using Noelia.Infrastructure.Security.ServiceTokens;
using Noelia.InMemory.Security;
using Noelia.Infrastructure.Tests.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceCallerHost;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.Identity;

/// <summary>
/// ADR-0006: the capacity policies read the user ASP.NET hands them, so they hold behind the
/// README chain <c>.UseAuth().UsePrincipal()</c>, where authorization runs before the
/// principal step has stored anything.
/// </summary>
/// <remarks>
/// Two groups, kept apart as in ADR-0003. The tests named "Red first" failed before the fix;
/// the "Guard" tests held before it and must never turn red: they are what stops the fix
/// from opening a door that was shut.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class CapacityPoliciesOrderTests
{
    private const string AsSelf = "/capacity/self";
    private const string ForCompany = "/capacity/company";
    private const string DoorWithPerson = "/internal/person-and-self";
    private const string DoorWithPolicy = "/internal/capacity-only";
    private const string ServiceAndSelf = "/capacity/service-and-self";

    private static readonly string Subject = Guid.NewGuid().ToString();
    private static readonly string Tenant = Guid.NewGuid().ToString();

    private static int _reached;

    // ---- Red first: valid people are let in behind the README chain ----------

    [Fact]
    public async Task Red_first_a_person_is_let_in_at_acting_as_self()
    {
        await using var host = await Chain(revocation: false);
        var token = Wire.Person(subject: Subject, tokenId: "live");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, AsSelf, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Red_first_a_company_caller_is_let_in_at_acting_for_company()
    {
        await using var host = await Chain(revocation: false);
        var token = Wire.Person(subject: Subject, tokenId: "live", tenant: Tenant);

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, ForCompany, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Red_first_the_same_person_gets_the_same_answer_with_and_without_the_policy()
    {
        await using var host = await Chain(revocation: false);
        var token = Wire.Person(subject: Subject, tokenId: "live");

        using var plain = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/people/me", token));
        using var policy = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, AsSelf, token));

        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        policy.StatusCode.Should().Be(plain.StatusCode);
    }

    // ---- Red first: a revoked person is refused with 401, and the endpoint never runs ----

    [Theory]
    [InlineData(AsSelf, null)]
    [InlineData(ForCompany, "tenant")]
    public async Task Red_first_a_revoked_person_gets_401_and_the_endpoint_does_not_run(
        string path, string? tenant)
    {
        var store = await StoreRevoking("revoked-token");
        await using var host = await Chain(evaluator: store);
        var token = Wire.Person(
            subject: Subject, tokenId: "revoked-token", tenant: tenant is null ? null : Tenant);
        var before = Volatile.Read(ref _reached);

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, path, token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Volatile.Read(ref _reached).Should().Be(before, "the revocation check answers before the endpoint");
    }

    // ---- Red first: a door with a person beside the service ------------------

    [Fact]
    public async Task Red_first_a_person_with_a_subject_beside_the_service_passes_at_acting_as_self()
    {
        await using var host = await Chain();

        using var response = await host.Client.SendAsync(await Both(DoorWithPerson, "live"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Red_first_a_revoked_person_beside_the_service_gets_401()
    {
        var store = await StoreRevoking("revoked-token");
        await using var host = await Chain(evaluator: store);

        using var response = await host.Client.SendAsync(await Both(DoorWithPerson, "revoked-token"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Red first: the DI constructor is the one with the factory -----------

    [Fact]
    public async Task Red_first_container_resolution_picks_the_constructor_with_the_factory()
    {
        var factory = new RecordingFactory();
        var services = new ServiceCollection();
        services.AddNoeliaIdentity();
        services.AddSingleton<IPrincipalFactory>(factory);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var handlers = scope.ServiceProvider.GetServices<IAuthorizationHandler>().ToArray();
        var requirement = new ActingAsSelfRequirement();
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject)], "Test")),
            resource: null);
        foreach (var handler in handlers)
        {
            await handler.HandleAsync(context);
        }

        factory.Calls.Should().BeGreaterThan(0, "the container picked the longer constructor");
        context.HasSucceeded.Should().BeTrue("the factory the container holds produced the principal");
    }

    // ---- Guards: these were right before the fix and must stay right ---------

    [Fact]
    public async Task Guard_the_other_capacity_is_refused_with_403()
    {
        await using var host = await Chain();
        var person = Wire.Person(subject: Subject, tokenId: "live");
        var company = Wire.Person(subject: Subject, tokenId: "live", tenant: Tenant);

        using var personAtCompany = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, ForCompany, person));
        using var companyAtSelf = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, AsSelf, company));

        personAtCompany.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        companyAtSelf.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(AsSelf)]
    [InlineData(ForCompany)]
    public async Task Guard_no_token_is_401(string path)
    {
        await using var host = await Chain();

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(AsSelf, false)]
    [InlineData(ForCompany, false)]
    [InlineData(ForCompany, true)]
    public async Task Guard_a_person_token_without_a_subject_or_with_an_unusable_tenant_is_403(
        string path, bool badTenant)
    {
        await using var host = await Chain(revocation: false);
        var token = badTenant
            ? Wire.Person(subject: Subject, tokenId: "live", tenant: "garbage")
            : Wire.Person(subject: null, tokenId: "live");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, path, token));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Guard_a_service_token_with_a_subject_at_a_door_with_the_policy_is_404()
    {
        await using var host = await Chain();
        var token = Wire.Service(subject: Subject, tokenId: "service-token");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, DoorWithPolicy, token));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Guard_a_service_token_with_a_subject_is_403_at_the_service_scheme_with_the_policy()
    {
        await using var host = await Chain();
        var token = Wire.Service(subject: Subject, tokenId: "service-token");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, ServiceAndSelf, token));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Guard_a_plain_service_token_at_a_plain_capacity_endpoint_is_401()
    {
        await using var host = await Chain();
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, AsSelf, token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Guard_a_person_without_a_subject_at_the_door_is_404()
    {
        await using var host = await Chain(revocation: false);

        using var response = await host.Client.SendAsync(
            await Both(DoorWithPerson, "person-token", HeaderPerson.NoSubject));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- helpers -------------------------------------------------------------

    private static async Task<HttpRequestMessage> Both(string door, string personTokenId, string? kind = null)
    {
        var request = Wire.Request(HttpMethod.Get, door, await Wire.Issued("advisor"));
        request.Headers.Add(HeaderPerson.Header, personTokenId);
        if (kind is not null)
        {
            request.Headers.Add(HeaderPerson.KindHeader, kind);
        }

        return request;
    }

    private static async Task<InMemoryTokenRevocationStore> StoreRevoking(string tokenId)
    {
        var store = new InMemoryTokenRevocationStore();
        await store.RevokeTokenAsync(tokenId, DateTimeOffset.UtcNow.AddHours(1), "test");
        return store;
    }

    // The README chain: the default chain (UseAuth inside), then the principal step. Revocation
    // sits between the two when asked for, as in ADR-0006.
    private static Task<ServiceCallerHost> Chain(
        bool revocation = true, ITokenRevocationEvaluator? evaluator = null) =>
        StartAsync(settings =>
        {
            settings.Compose = noelia => noelia
                .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
                .UseServiceCallers(ThisAudience)
                .Use(NoeliaModule.Principal);
            settings.Services = services =>
            {
                services.AddSingleton(evaluator ?? new InMemoryTokenRevocationStore());
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderPerson>(HeaderPerson.SchemeName, _ => { });
            };
            settings.AfterChain = app =>
            {
                if (revocation)
                {
                    app.UseTokenRevocation();
                }

                app.UseNoeliaPrincipal();
            };
            settings.Map = endpoints =>
            {
                MapStandard(endpoints);
                Map(endpoints);
            };
        });

    private static void Map(IEndpointRouteBuilder endpoints)
    {
        var personScheme = new AuthorizeAttribute { AuthenticationSchemes = HeaderPerson.SchemeName };
        var serviceScheme = new AuthorizeAttribute { AuthenticationSchemes = ServiceTokenDefaults.AuthenticationScheme };

        endpoints.MapGet(AsSelf, Reached).RequireAuthorization(NoeliaPolicies.ActingAsSelf);
        endpoints.MapGet(ForCompany, Reached).RequireAuthorization(NoeliaPolicies.ActingForCompany);
        endpoints.MapGet(DoorWithPerson, Reached)
            .RequireServiceCaller("advisor")
            .RequireAuthorization(personScheme)
            .RequireAuthorization(NoeliaPolicies.ActingAsSelf);
        endpoints.MapGet(DoorWithPolicy, Reached)
            .RequireServiceCaller("advisor")
            .RequireAuthorization(NoeliaPolicies.ActingAsSelf);
        endpoints.MapGet(ServiceAndSelf, Reached)
            .RequireAuthorization(serviceScheme)
            .RequireAuthorization(NoeliaPolicies.ActingAsSelf);
    }

    private static IResult Reached()
    {
        Interlocked.Increment(ref _reached);
        return Results.Text("reached");
    }

    private sealed class RecordingFactory : IPrincipalFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public PrincipalResult Create(ClaimsPrincipal? user)
        {
            Interlocked.Increment(ref _calls);
            return PrincipalResult.Success(Principal.Person(SubjectId.New()));
        }
    }

    /// <summary>A second way of signing a person in, beside the bearer header: a stand-in for a cookie.</summary>
    private sealed class HeaderPerson(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "HeaderPerson";
        public const string Header = "X-Test-Person-Token-Id";
        public const string KindHeader = "X-Test-Person-Kind";
        public const string NoSubject = "no-subject";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var tokenId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var claims = new List<Claim> { new("jti", tokenId.ToString()), new("iat", issuedAt) };
            if (Request.Headers[KindHeader].ToString() != NoSubject)
            {
                claims.Add(new Claim("sub", Subject));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
