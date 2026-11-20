using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Security.Identity;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Tests.Security.Identity;

/// <summary>
/// ADR-0007 (#59), decisions 3 and 5: the principal reads <c>sub</c> and <c>tenant</c> from the
/// same signed-in person identity, an identity nobody signed in is not there, and a factory of the
/// consumer's own is handed signed-in identities only, without the service identity.
/// </summary>
[Trait("Category", "Unit")]
public class PrincipalAuthenticatedIdentityTests
{
    private static readonly SubjectId A = SubjectId.New();
    private static readonly SubjectId B = SubjectId.New();
    private static readonly TenantId Tenant = TenantId.New();

    private static ClaimsIdentity Signed(SubjectId sub, params Claim[] more) =>
        new([new Claim(JwtRegisteredClaimNames.Sub, sub.ToString()), .. more], "Bearer");

    private static ClaimsIdentity TenantOnly(string? authenticationType) =>
        new([new Claim(NoeliaClaimTypes.Tenant, Tenant.ToString())], authenticationType);

    private static ClaimsIdentity Service() =>
        new([new Claim("client_id", "advisor")], ServiceTokenDefaults.AuthenticationScheme);

    private static ClaimsPrincipal Order(bool strangerFirst, ClaimsIdentity person, ClaimsIdentity stranger) =>
        strangerFirst ? new ClaimsPrincipal([stranger, person]) : new ClaimsPrincipal([person, stranger]);

    // ---- the factory itself -----------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Factory_AnUnsignedTenantBehindThePerson_DoesNotMakeThemACompany(bool strangerFirst)
    {
        var result = new PrincipalFactory().Create(Order(strangerFirst, Signed(A), TenantOnly(null)));

        result.Principal!.Subject.Should().Be(A);
        result.Principal.Acting.Should().BeOfType<Capacity.AsSelf>();
    }

    [Fact]
    public void Factory_AnUnsignedSubjectInFront_DoesNotSpeakForThePerson()
    {
        var stranger = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, B.ToString())]);

        var result = new PrincipalFactory().Create(new ClaimsPrincipal([stranger, Signed(A)]));

        result.Principal!.Subject.Should().Be(A);
    }

    [Fact]
    public void Factory_OnlyUnsignedIdentities_AreAnonymous()
    {
        var result = new PrincipalFactory().Create(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, A.ToString())])));

        result.IsAnonymous.Should().BeTrue();
    }

    [Fact]
    public void Factory_SubAndTenantOfTheSamePerson_AreACompany()
    {
        var result = new PrincipalFactory().Create(new ClaimsPrincipal(
            Signed(A, new Claim(NoeliaClaimTypes.Tenant, Tenant.ToString()))));

        result.Principal!.Acting.Should().BeOfType<Capacity.ForCompany>().Which.Tenant.Should().Be(Tenant);
    }

    [Fact]
    public void Factory_TenantOfTheSecondPerson_IsNotGivenToTheFirst_AndIsNotLookedUpEither()
    {
        var first = Signed(A);
        var second = Signed(B, new Claim(NoeliaClaimTypes.Tenant, Tenant.ToString()));

        var result = new PrincipalFactory().Create(new ClaimsPrincipal([first, second]));

        result.Principal!.Subject.Should().Be(A);
        result.Principal.Acting.Should().BeOfType<Capacity.AsSelf>();
    }

    // ---- the middleware and the capacity policies -------------------------------------------

    private static async Task<Principal?> CurrentAfterMiddleware(ClaimsPrincipal user, IPrincipalFactory? factory = null)
    {
        var context = new DefaultHttpContext { User = user };
        await new PrincipalMiddleware(
                _ => Task.CompletedTask, factory ?? new PrincipalFactory(), NullLogger<PrincipalMiddleware>.Instance)
            .InvokeAsync(context);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return new HttpContextCurrentPrincipal(accessor).Current;
    }

    private static async Task<(bool ForCompany, bool AsSelf)> Capacities(ClaimsPrincipal user)
    {
        var forCompany = new AuthorizationHandlerContext([new ActingForCompanyRequirement()], user, null);
        var asSelf = new AuthorizationHandlerContext([new ActingAsSelfRequirement()], user, null);
        var current = Substitute.For<ICurrentPrincipal>();
        await new ActingForCompanyHandler(current).HandleAsync(forCompany);
        await new ActingAsSelfHandler(current).HandleAsync(asSelf);
        return (forCompany.HasSucceeded, asSelf.HasSucceeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsignedTenantBehindThePerson_CurrentStaysThePerson_ActingForCompanyIsNotMet(bool strangerFirst)
    {
        var user = Order(strangerFirst, Signed(A), TenantOnly(null));

        var current = await CurrentAfterMiddleware(user);
        var (forCompany, asSelf) = await Capacities(user);

        current!.Subject.Should().Be(A);
        current.Acting.Should().BeOfType<Capacity.AsSelf>();
        forCompany.Should().BeFalse();
        asSelf.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TenantOnTheSignedInPerson_StillMakesACompany(bool strangerFirst)
    {
        var person = Signed(A, new Claim(NoeliaClaimTypes.Tenant, Tenant.ToString()));
        var user = Order(strangerFirst, person, new ClaimsIdentity([new Claim("note", "x")]));

        var current = await CurrentAfterMiddleware(user);
        var (forCompany, asSelf) = await Capacities(user);

        current!.Acting.Should().BeOfType<Capacity.ForCompany>();
        forCompany.Should().BeTrue();
        asSelf.Should().BeFalse();
    }

    [Fact]
    public async Task TwoPersons_SubOfAAndTenantOfB_CurrentIsAWithoutATenant_ActingForCompanyIsNotMet()
    {
        var user = new ClaimsPrincipal([
            Signed(A),
            Signed(B, new Claim(NoeliaClaimTypes.Tenant, Tenant.ToString()))]);

        var current = await CurrentAfterMiddleware(user);
        var (forCompany, _) = await Capacities(user);

        current!.Subject.Should().Be(A);
        current.Acting.Should().BeOfType<Capacity.AsSelf>();
        forCompany.Should().BeFalse();
    }

    // ---- a factory of the consumer's own ---------------------------------------------------

    private sealed class Spy : IPrincipalFactory
    {
        public readonly List<ClaimsPrincipal?> Got = [];

        public PrincipalResult Create(ClaimsPrincipal? user)
        {
            Got.Add(user);
            return PrincipalResult.Anonymous();
        }
    }

    [Fact]
    public async Task AFactoryOfYourOwn_GetsOnlySignedInIdentities_WithoutTheServiceIdentity_ByTheMiddleware()
    {
        var spy = new Spy();
        var person = Signed(A);
        var stranger = TenantOnly(null);
        var service = Service();

        await CurrentAfterMiddleware(new ClaimsPrincipal([stranger, service, person]), spy);

        spy.Got.Should().ContainSingle().Which!.Identities.Should().Equal(person);
    }

    [Fact]
    public async Task AFactoryOfYourOwn_GetsOnlySignedInIdentities_WithoutTheServiceIdentity_ByTheCapacityPolicies()
    {
        var spy = new Spy();
        var person = Signed(A);
        var user = new ClaimsPrincipal([TenantOnly(null), Service(), person]);
        var current = Substitute.For<ICurrentPrincipal>();

        await new ActingForCompanyHandler(current, spy).HandleAsync(
            new AuthorizationHandlerContext([new ActingForCompanyRequirement()], user, null));
        await new ActingAsSelfHandler(current, spy).HandleAsync(
            new AuthorizationHandlerContext([new ActingAsSelfRequirement()], user, null));

        spy.Got.Should().HaveCount(2);
        spy.Got.Should().AllSatisfy(got => got!.Identities.Should().Equal(person));
    }

    [Fact]
    public async Task AFactoryOfYourOwn_WithNoServiceIdentity_StillGetsAFilteredPrincipal()
    {
        // Until 7.2.0 a principal without a service identity came back as it went in.
        var spy = new Spy();
        var person = Signed(A);

        await CurrentAfterMiddleware(new ClaimsPrincipal([TenantOnly(null), person]), spy);

        spy.Got.Should().ContainSingle().Which!.Identities.Should().Equal(person);
    }

    [Fact]
    public async Task AFactoryOfYourOwn_WithNoPerson_GetsAPrincipalWithoutIdentities()
    {
        var spy = new Spy();

        await CurrentAfterMiddleware(new ClaimsPrincipal([TenantOnly(null), Service()]), spy);

        spy.Got.Should().ContainSingle().Which!.Identities.Should().BeEmpty();
    }

    [Fact]
    public void PersonPartOf_ReturnsTheSignedInIdentitiesWithoutTheServiceOne_AlsoWithoutAServiceIdentity()
    {
        var person = Signed(A);
        var second = Signed(B);

        var part = ServiceCallerIdentity.PersonPartOf(new ClaimsPrincipal([TenantOnly(null), person, Service(), second]));

        part.Identities.Should().Equal(person, second);
    }

    [Fact]
    public void PersonPartOf_WithoutAServiceIdentity_DoesNotReturnThePrincipalAsItWentIn()
    {
        // ADR-0007 decision 5: until 7.2.0 this came back unchanged and carried the identity nobody signed in.
        var person = Signed(A);
        var original = new ClaimsPrincipal([TenantOnly(null), person]);

        var part = ServiceCallerIdentity.PersonPartOf(original);

        part.Identities.Should().Equal(person);
        part.Should().NotBeSameAs(original);
    }
}
