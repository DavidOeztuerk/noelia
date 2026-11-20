using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Security.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Noelia.Infrastructure.Tests.Security.Identity;

[Trait("Category", "Unit")]
public class PrincipalFactoryTests
{
    private readonly PrincipalFactory _sut = new();

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public void UnauthenticatedRequest_IsAnonymous()
    {
        var result = _sut.Create(new ClaimsPrincipal(new ClaimsIdentity()));

        result.IsAnonymous.Should().BeTrue();
        result.Principal.Should().BeNull();
    }

    [Fact]
    public void NullUser_IsAnonymous()
    {
        _sut.Create(null).IsAnonymous.Should().BeTrue();
    }

    [Fact]
    public void TokenWithoutTenantClaim_YieldsAPerson()
    {
        var subject = SubjectId.New();

        var result = _sut.Create(Authenticated(
            new Claim(JwtRegisteredClaimNames.Sub, subject.ToString())));

        result.Principal!.Subject.Should().Be(subject);
        result.Principal.Acting.Should().BeOfType<Capacity.AsSelf>();
    }

    [Fact]
    public void TokenWithTenantClaim_YieldsACompanyCapacity()
    {
        var subject = SubjectId.New();
        var tenant = TenantId.New();

        var result = _sut.Create(Authenticated(
            new Claim(JwtRegisteredClaimNames.Sub, subject.ToString()),
            new Claim(NoeliaClaimTypes.Tenant, tenant.ToString())));

        result.Principal!.Acting.Should().BeOfType<Capacity.ForCompany>()
            .Which.Tenant.Should().Be(tenant);
    }

    [Fact]
    public void SubjectIsAlsoReadFromNameIdentifier()
    {
        // The JWT handler maps "sub" onto NameIdentifier by default.
        var subject = SubjectId.New();

        var result = _sut.Create(Authenticated(
            new Claim(ClaimTypes.NameIdentifier, subject.ToString())));

        result.Principal!.Subject.Should().Be(subject);
    }

    [Fact]
    public void MissingSubject_IsInvalid()
    {
        var result = _sut.Create(Authenticated(new Claim("email", "a@b.c")));

        result.IsInvalid.Should().BeTrue();
        result.Principal.Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void MalformedTenantClaim_IsInvalidRatherThanDowngraded(string tenantClaim)
    {
        var result = _sut.Create(Authenticated(
            new Claim(JwtRegisteredClaimNames.Sub, SubjectId.New().ToString()),
            new Claim(NoeliaClaimTypes.Tenant, tenantClaim)));

        result.IsInvalid.Should().BeTrue();
        result.Principal.Should().BeNull();
    }
}

[Trait("Category", "Unit")]
public class PrincipalMiddlewareTests
{
    private static HttpContext ContextWith(ClaimsPrincipal user) =>
        new DefaultHttpContext { User = user };

    private static PrincipalMiddleware Middleware(RequestDelegate next) =>
        new(next, new PrincipalFactory(), NullLogger<PrincipalMiddleware>.Instance);

    [Fact]
    public async Task StoresThePrincipalForTheRestOfThePipeline()
    {
        var subject = SubjectId.New();
        var context = ContextWith(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, subject.ToString())], "Test")));
        var called = false;

        await Middleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);

        called.Should().BeTrue();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        new HttpContextCurrentPrincipal(accessor).Current!.Subject.Should().Be(subject);
    }

    [Fact]
    public async Task AnonymousRequestPassesThroughWithoutAPrincipal()
    {
        var context = ContextWith(new ClaimsPrincipal(new ClaimsIdentity()));
        var called = false;

        await Middleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);

        called.Should().BeTrue();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        new HttpContextCurrentPrincipal(accessor).Current.Should().BeNull();
    }

    [Fact]
    public async Task UntranslatableClaimsAreRejectedAndTheChainStops()
    {
        var context = ContextWith(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, SubjectId.New().ToString()),
             new Claim(NoeliaClaimTypes.Tenant, "garbage")], "Test")));
        var called = false;

        await Middleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);

        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }
}

[Trait("Category", "Unit")]
public class CapacityPolicyTests
{
    // The handlers read the user ASP.NET hands the policy (ADR-0006): the claims are in the
    // user, not in an ICurrentPrincipal. The fake the constructor still takes is never read.
    private static AuthorizationHandlerContext ContextFor(
        IAuthorizationRequirement requirement, params Claim[] claims) =>
        new([requirement], claims.Length == 0
                ? new ClaimsPrincipal()
                : new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            resource: null);

    private static Claim Sub() => new(JwtRegisteredClaimNames.Sub, SubjectId.New().ToString());

    private static Claim Tenant() => new(NoeliaClaimTypes.Tenant, TenantId.New().ToString());

    private static ICurrentPrincipal Unread()
    {
        var accessor = Substitute.For<ICurrentPrincipal>();
        accessor.Current.Returns(Principal.Person(SubjectId.New()));
        return accessor;
    }

    [Fact]
    public async Task ActingForCompany_SucceedsForACompanyCaller()
    {
        var requirement = new ActingForCompanyRequirement();
        var context = ContextFor(requirement, Sub(), Tenant());

        await new ActingForCompanyHandler(Unread()).HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task ActingForCompany_FailsForAPersonCaller()
    {
        var requirement = new ActingForCompanyRequirement();
        var context = ContextFor(requirement, Sub());

        await new ActingForCompanyHandler(Unread()).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task ActingAsSelf_FailsForACompanyCaller()
    {
        var requirement = new ActingAsSelfRequirement();
        var context = ContextFor(requirement, Sub(), Tenant());

        await new ActingAsSelfHandler(Unread()).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task ActingAsSelf_SucceedsForAPersonCaller()
    {
        var requirement = new ActingAsSelfRequirement();
        var context = ContextFor(requirement, Sub());

        await new ActingAsSelfHandler(Unread()).HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task BothFailForAnAnonymousCaller()
    {
        var company = new ActingForCompanyRequirement();
        var companyContext = ContextFor(company);
        await new ActingForCompanyHandler(Unread()).HandleAsync(companyContext);

        var self = new ActingAsSelfRequirement();
        var selfContext = ContextFor(self);
        await new ActingAsSelfHandler(Unread()).HandleAsync(selfContext);

        companyContext.HasSucceeded.Should().BeFalse();
        selfContext.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task AFakedCurrentPrincipalDoesNotSatisfyThePolicies()
    {
        var company = ContextFor(new ActingForCompanyRequirement());
        var fake = Substitute.For<ICurrentPrincipal>();
        fake.Current.Returns(Principal.Company(SubjectId.New(), TenantId.New()));

        await new ActingForCompanyHandler(fake).HandleAsync(company);

        company.HasSucceeded.Should().BeFalse("the claims belong in the user, ADR-0006");
    }
}
