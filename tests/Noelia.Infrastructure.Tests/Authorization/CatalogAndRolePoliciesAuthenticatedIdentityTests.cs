using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Security.Authorization;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Authorization;
using static Noelia.Infrastructure.Tests.Authorization.AuthenticatedIdentityFixtures;

namespace Noelia.Infrastructure.Tests.Authorization;

/// <summary>
/// ADR-0007 (#59), decision 3, catalogue and role policies: ASP.NET evaluates
/// <c>RequireClaim</c>/<c>RequireRole</c> over every identity, so the policies have to filter
/// to authenticated identities themselves, while meaning exactly what they meant.
/// </summary>
[Trait("Category", "Unit")]
public class CatalogAndRolePoliciesAuthenticatedIdentityTests
{
    private static ServiceProvider Provider()
    {
        var catalog = new PermissionCatalogBuilder()
            .Role("Clerk", Required)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<IPermissionCatalog>(catalog);
        services.AddSingleton(Substitute.For<IResourceAuthorizationService>());
        services.AddNoeliaAuthorization();
        services.AddResourceAuthorization(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static async Task<bool> Passes(string policy, ClaimsPrincipal user)
    {
        await using var provider = Provider();
        var service = provider.GetRequiredService<IAuthorizationService>();
        return (await service.AuthorizeAsync(user, null, policy)).Succeeded;
    }

    /// <summary>Policy name and the claim that satisfies it exactly.</summary>
    public static TheoryData<string, string, string> Rows() => new()
    {
        { Required, "permission", Required },
        { Noelia.Infrastructure.Security.Policies.RequireAdminRole, ClaimTypes.Role, Roles.Admin },
        { Noelia.Infrastructure.Security.Policies.RequireModeratorRole, ClaimTypes.Role, Roles.Moderator },
        { Noelia.Infrastructure.Security.Policies.RequireUserRole, ClaimTypes.Role, Roles.User },
        { "AdminOnly", ClaimTypes.Role, "Admin" },
        { "SuperAdminOnly", ClaimTypes.Role, "SuperAdmin" },
    };

    public static TheoryData<string, string, string, bool> PoliciesByOrder()
    {
        var data = new TheoryData<string, string, string, bool>();
        foreach (var row in Rows())
        {
            data.Add((string)row[0], (string)row[1], (string)row[2], true);
            data.Add((string)row[0], (string)row[1], (string)row[2], false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PoliciesByOrder))]
    public async Task PersonWithoutTheRight_UnauthenticatedIdentityCarriesIt_IsNotMet(
        string policy, string claimType, string value, bool unauthenticatedFirst)
    {
        var user = Of(unauthenticatedFirst, Person(), Unauthenticated(new Claim(claimType, value)));

        (await Passes(policy, user)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(PoliciesByOrder))]
    public async Task OnlyUnauthenticatedIdentities_IsNotMet(
        string policy, string claimType, string value, bool unauthenticatedFirst)
    {
        var carrying = Unauthenticated(new Claim(claimType, value));
        var user = unauthenticatedFirst
            ? new ClaimsPrincipal([carrying, Unauthenticated()])
            : new ClaimsPrincipal([Unauthenticated(), carrying]);

        (await Passes(policy, user)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(PoliciesByOrder))]
    public async Task RightOnTheAuthenticatedIdentity_UnauthenticatedBesideIt_DoesNotDisturb(
        string policy, string claimType, string value, bool unauthenticatedFirst)
    {
        var user = Of(unauthenticatedFirst, Person(new Claim(claimType, value)), Unauthenticated(new Claim("note", "x")));

        (await Passes(policy, user)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task EveryPolicy_DemandsAnAuthenticatedUser_BesidesItsHandler(string policy, string claimType, string value)
    {
        // The handler already ignores unsigned identities, so no behavioural probe can tell this
        // requirement apart; ADR-0007 asks for it as a second, independent line, so it is pinned
        // as structure: a policy without it would rest on its handler alone.
        _ = claimType;
        _ = value;
        await using var provider = Provider();
        var found = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(policy);

        found!.Requirements.Should().ContainSingle(r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task ServiceIdentity_PermissionInItStillSatisfiesTheCatalogPolicy()
    {
        // ADR-0007 decision 6: the service identity is authenticated, its permission claim counts.
        var service = new ClaimsIdentity(
            [new Claim("permission", Required)], Noelia.Infrastructure.Security.ServiceTokens.ServiceTokenDefaults.AuthenticationScheme);

        (await Passes(Required, new ClaimsPrincipal([Unauthenticated(), service]))).Should().BeTrue();
    }

    [Theory]
    [InlineData("role")]
    [InlineData("wildcard")]
    [InlineData("category")]
    public async Task CatalogPolicy_KeepsItsMeaning_OnlyTheExactPermissionCounts(string kind)
    {
        // Not part of the fix and must not move with it: the catalogue policy counts the exact
        // permission claim, no "*", no "orders:*", no catalogue role (README roadmap question).
        var user = new ClaimsPrincipal(Person(Granting(kind)));

        (await Passes(Required, user)).Should().BeFalse();
    }

    [Fact]
    public async Task RolePolicy_FollowsTheRoleClaimTypeOfTheIdentity()
    {
        // An OIDC-style identity names its own role claim type; RequireRole honoured that per identity
        // and the filtered check has to keep honouring it.
        var oidc = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("roles", "Admin")],
            "oidc", ClaimTypes.Name, "roles");

        (await Passes("AdminOnly", new ClaimsPrincipal(oidc))).Should().BeTrue();
        (await Passes(Noelia.Infrastructure.Security.Policies.RequireAdminRole, new ClaimsPrincipal(oidc))).Should().BeTrue();
    }

    [Fact]
    public async Task RolePolicy_ClaimTypeOfAnotherIdentityDoesNotLendItsRole()
    {
        // "roles" is only a role claim on the identity that declares it as RoleClaimType.
        var plain = Person(new Claim("roles", "Admin"));

        (await Passes("AdminOnly", new ClaimsPrincipal(plain))).Should().BeFalse();
    }
}
