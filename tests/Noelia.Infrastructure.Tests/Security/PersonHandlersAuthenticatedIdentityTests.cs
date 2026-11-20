using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Security.Authorization;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Authorization;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// ADR-0007 (#59), decision 3, the five person handlers: "at least one signed-in person identity
/// and every one of them passes". No person is <c>Fail()</c>, tested on the handler itself and
/// without <c>RequireAuthenticatedUser</c>, because a consumer policy can point at the handler
/// without it and the requirement types are public.
/// </summary>
[Trait("Category", "Unit")]
public class PersonHandlersAuthenticatedIdentityTests
{
    private static readonly string[] Handlers = ["active", "email", "resource", "ownership", "owner"];
    private static readonly string[] NoPersonShapes = ["none", "unauthenticated", "service+unauthenticated", "service"];
    private const string Alice = PersonCheckRig.Owner;
    private const string RecordId = "alice"; // route id: the record alice owns

    private static HttpContext Http()
    {
        var http = new DefaultHttpContext();
        http.Request.RouteValues["id"] = RecordId;
        http.SetEndpoint(new Endpoint(
            null, new EndpointMetadataCollection(new ResourceAuthorizeAttribute("orders")), "test"));
        return http;
    }

    private static async Task<AuthorizationHandlerContext> Run(string handler, ClaimsPrincipal user, PersonCheckRig? rig = null)
    {
        rig ??= new PersonCheckRig();
        var http = Http();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        var (authorizationHandler, requirement, resource) = handler switch
        {
            "active" => ((IAuthorizationHandler)new ActiveAccountHandler(NullLogger<ActiveAccountHandler>.Instance),
                (IAuthorizationRequirement)new ActiveAccountRequirement(), (object?)null),
            "email" => (new EmailVerifiedHandler(NullLogger<EmailVerifiedHandler>.Instance),
                new EmailVerifiedRequirement(), null),
            "resource" => (new ResourceAuthorizationHandler(rig.Resources),
                new ResourceRequirement(NoeliaActions.READ, "orders"), http),
            "ownership" => (new OwnershipAuthorizationHandler(rig.Resources),
                new OwnershipRequirement("orders"), http),
            "owner" => (new ResourceOwnerHandler(accessor, NullLogger<ResourceOwnerHandler>.Instance),
                new ResourceOwnerRequirement(), http),
            _ => throw new ArgumentOutOfRangeException(nameof(handler), handler, null)
        };

        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await authorizationHandler.HandleAsync(context);
        return context;
    }

    /// <summary>Everything any of the handlers could be satisfied by, on one identity.</summary>
    private static ClaimsIdentity Everything(string? authenticationType, string name = Alice) =>
        new(
            [
                new Claim(ClaimTypes.NameIdentifier, name),
                new Claim("account_status", "Active"),
                new Claim("email_verified", "true"),
                new Claim(ClaimTypes.Role, Roles.Admin)
            ],
            authenticationType);

    private static ClaimsIdentity Service() =>
        new([new Claim("client_id", "advisor")], ServiceTokenDefaults.AuthenticationScheme);

    /// <summary>A signed-in person without the admin role: owning has to come from the id.</summary>
    private static ClaimsIdentity Plain(string name, bool withStatements) =>
        new(
            withStatements
                ? [new Claim(ClaimTypes.NameIdentifier, name),
                   new Claim("account_status", "Active"), new Claim("email_verified", "true")]
                : [new Claim(ClaimTypes.NameIdentifier, name)],
            "Bearer");

    private static ClaimsPrincipal Shape(string shape) => shape switch
    {
        "none" => new ClaimsPrincipal(),
        "unauthenticated" => new ClaimsPrincipal(Everything(null)),
        "service+unauthenticated" => new ClaimsPrincipal([Service(), Everything(null)]),
        "service" => new ClaimsPrincipal(Service()),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    public static TheoryData<string, string> NoPerson()
    {
        var data = new TheoryData<string, string>();
        foreach (var handler in Handlers)
        {
            foreach (var shape in NoPersonShapes)
            {
                data.Add(handler, shape);
            }
        }

        return data;
    }

    public static TheoryData<string> HandlerNames() => [.. Handlers];

    public static TheoryData<string, bool> TwoPersons()
    {
        var data = new TheoryData<string, bool>();
        foreach (var handler in Handlers)
        {
            data.Add(handler, true);
            data.Add(handler, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NoPerson))]
    public async Task NoSignedInPerson_FailsTheRequirement_WithoutRequireAuthenticatedUser(string handler, string shape)
    {
        var rig = new PersonCheckRig();

        var context = await Run(handler, Shape(shape), rig);

        context.HasSucceeded.Should().BeFalse();
        context.HasFailed.Should().BeTrue("a handler that only declines leaves the decision to other handlers");
        rig.Asked.Should().BeEmpty("nobody was asked about a person that does not exist");
    }

    [Theory]
    [MemberData(nameof(HandlerNames))]
    public async Task OneSignedInPerson_StillSucceeds(string handler)
    {
        var context = await Run(handler, new ClaimsPrincipal(Everything("Bearer")));

        context.HasSucceeded.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(HandlerNames))]
    public async Task APersonBesideAnIdentityNobodySignedIn_StillSucceeds_InEitherOrder(string handler)
    {
        var stranger = Everything(null, "mallory");

        (await Run(handler, new ClaimsPrincipal([stranger, Everything("Bearer")]))).HasSucceeded.Should().BeTrue();
        (await Run(handler, new ClaimsPrincipal([Everything("Bearer"), stranger]))).HasSucceeded.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(HandlerNames))]
    public async Task APersonBesideAService_StillSucceeds(string handler)
    {
        var context = await Run(handler, new ClaimsPrincipal([Service(), Everything("Bearer")]));

        context.HasSucceeded.Should().BeTrue();
    }

    // ---- two signed-in persons: every one has to pass ---------------------------------------

    [Theory]
    [MemberData(nameof(TwoPersons))]
    public async Task TwoPersons_OneOfThemFailing_RefusesInEitherOrder(string handler, bool failingFirst)
    {
        // alice states and owns everything; bob states nothing, owns nothing, is no admin.
        var alice = Plain(Alice, withStatements: true);
        var bob = Plain("bob", withStatements: false);
        var user = failingFirst ? new ClaimsPrincipal([bob, alice]) : new ClaimsPrincipal([alice, bob]);
        var rig = new PersonCheckRig();
        rig.Refused.Add("bob");

        var context = await Run(handler, user, rig);

        context.HasSucceeded.Should().BeFalse();
        context.HasFailed.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(HandlerNames))]
    public async Task TwoPersons_BothPassing_Succeeds(string handler)
    {
        var user = new ClaimsPrincipal([Plain(Alice, true), Plain(Alice, true)]);

        var context = await Run(handler, user);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task ResourceHandler_AsksTheProviderOncePerPerson_AboutThatPersonAlone()
    {
        var rig = new PersonCheckRig();

        var context = await Run("resource", new ClaimsPrincipal([Plain(Alice, true), Plain("bob", true)]), rig);

        context.HasSucceeded.Should().BeTrue();
        rig.Asked.Should().HaveCount(2);
        rig.Asked.Should().OnlyContain(principal => principal.Identities.Count() == 1);
        rig.Asked.Select(principal => principal.FindFirst(ClaimTypes.NameIdentifier)!.Value)
            .Should().BeEquivalentTo([Alice, "bob"]);
    }

    [Fact]
    public async Task OwnershipHandler_AsksOnceForEachPersonsOwnId()
    {
        var rig = new PersonCheckRig();

        var context = await Run("ownership", new ClaimsPrincipal([Plain(Alice, true), Plain("bob", true)]), rig);

        context.HasFailed.Should().BeTrue("bob does not own the record");
        await rig.Resources.Received(1).IsResourceOwnerAsync(Alice, "orders", RecordId, Arg.Any<CancellationToken>());
        await rig.Resources.Received(1).IsResourceOwnerAsync("bob", "orders", RecordId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OwnerHandler_AdminIsReadFromThePersonsOwnIdentity()
    {
        var alice = Plain(Alice, true);
        var bob = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "bob"), new Claim(ClaimTypes.Role, Roles.Admin)], "Bearer");
        var carol = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "carol")], "Bearer");

        (await Run("owner", new ClaimsPrincipal([alice, bob]))).HasSucceeded.Should().BeTrue(
            "alice owns the record and bob is an admin on his own identity");
        (await Run("owner", new ClaimsPrincipal([carol, bob]))).HasFailed.Should().BeTrue(
            "bob's admin role does not make carol an owner");
    }

    [Fact]
    public async Task AccountState_OneIdentityWithoutAStatement_AndOneActive_IsRefused()
    {
        var silent = Plain("bob", withStatements: false);
        var active = Plain(Alice, withStatements: true);

        foreach (var user in new[] { new ClaimsPrincipal([silent, active]), new ClaimsPrincipal([active, silent]) })
        {
            (await Run("active", user)).HasFailed.Should().BeTrue();
            (await Run("email", user)).HasFailed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task AccountState_SuspendedSignedInIdentity_BesideAnUnauthenticatedActiveOne_IsRefused()
    {
        var suspended = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Alice), new Claim("account_status", "Suspended"),
             new Claim("email_verified", "false")],
            "Bearer");
        var liar = Everything(null, "mallory");

        foreach (var user in new[] { new ClaimsPrincipal([liar, suspended]), new ClaimsPrincipal([suspended, liar]) })
        {
            (await Run("active", user)).HasFailed.Should().BeTrue();
            (await Run("email", user)).HasFailed.Should().BeTrue();
        }
    }

    // ---- the named policies -----------------------------------------------------------------

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton(new PersonCheckRig().Resources);
        services.AddNoeliaAuthorization();
        services.AddResourceAuthorization(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    public static TheoryData<string> PersonPolicies() =>
        [Policies.RequireActiveAccount, Policies.RequireVerifiedEmail, "ResourceRead", "ResourceWrite", "ResourceDelete", "ResourceOwner"];

    [Theory]
    [MemberData(nameof(PersonPolicies))]
    public async Task NamedPolicy_CarriesRequireAuthenticatedUser(string policy)
    {
        await using var provider = Provider();

        var found = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(policy);

        found!.Requirements.Should().ContainSingle(requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Theory]
    [InlineData(Policies.RequireActiveAccount)]
    [InlineData(Policies.RequireVerifiedEmail)]
    [InlineData("ResourceRead")]
    [InlineData("ResourceOwner")]
    public async Task NamedPolicy_WithoutASignedInPerson_IsNotSatisfied(string policy)
    {
        await using var provider = Provider();
        var service = provider.GetRequiredService<IAuthorizationService>();

        foreach (var shape in NoPersonShapes)
        {
            var result = await service.AuthorizeAsync(Shape(shape), Http(), policy);
            result.Succeeded.Should().BeFalse($"shape {shape}");
        }

        (await service.AuthorizeAsync(new ClaimsPrincipal(Everything("Bearer")), Http(), policy))
            .Succeeded.Should().BeTrue("a signed-in person is the control");
    }
}
