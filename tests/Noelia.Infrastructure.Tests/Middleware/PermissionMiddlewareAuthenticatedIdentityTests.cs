using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Infrastructure.Middleware;
using Noelia.Infrastructure.Security.Authorization;
using Noelia.Infrastructure.Security.Monitoring;
using Noelia.Infrastructure.Tests.Authorization;
using static Noelia.Infrastructure.Tests.Authorization.AuthenticatedIdentityFixtures;

namespace Noelia.Infrastructure.Tests.Middleware;

/// <summary>
/// ADR-0007 (#59), decision 2: the middleware reads rights, roles, wildcards and the user id
/// only from authenticated identities, whatever order the identities stand in.
/// </summary>
[Trait("Category", "Unit")]
public class PermissionMiddlewareAuthenticatedIdentityTests
{
    private sealed record Outcome(int Status, bool NextCalled, ISecurityAlertService Alerts);

    public static TheoryData<string, bool> Matrix()
    {
        var data = new TheoryData<string, bool>();
        // "resource" (Permission:... with a Resource) exists only on the handler: the middleware's
        // requirement has no resource, see PermissionHandlerAuthenticatedIdentityTests.
        foreach (var kind in Kinds.Where(k => k != "resource"))
        {
            data.Add(kind, true);
            data.Add(kind, false);
        }

        return data;
    }

    private static async Task<Outcome> Run(ClaimsPrincipal user)
    {
        var nextCalled = false;
        var alerts = Substitute.For<ISecurityAlertService>();
        var context = new DefaultHttpContext
        {
            User = user,
            RequestServices = new ServiceCollection().AddSingleton(alerts).BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/orders/1";
        context.SetEndpoint(new Endpoint(
            null, new EndpointMetadataCollection(new RequirePermissionAttribute(Required)), "orders"));

        var middleware = new PermissionMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<PermissionMiddleware>.Instance,
            Catalog,
            EndpointAccessPolicy.Empty);

        await middleware.InvokeAsync(context);
        return new Outcome(context.Response.StatusCode, nextCalled, alerts);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task PersonWithoutTheRight_UnauthenticatedIdentityCarriesIt_Is403(string kind, bool unauthenticatedFirst)
    {
        var user = Of(unauthenticatedFirst, Person(), Unauthenticated(Granting(kind)));

        var outcome = await Run(user);

        outcome.NextCalled.Should().BeFalse();
        outcome.Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task OnlyUnauthenticatedIdentities_Is401(string kind, bool unauthenticatedFirst)
    {
        var user = unauthenticatedFirst
            ? new ClaimsPrincipal([Unauthenticated(Granting(kind)), Unauthenticated()])
            : new ClaimsPrincipal([Unauthenticated(), Unauthenticated(Granting(kind))]);

        var outcome = await Run(user);

        outcome.NextCalled.Should().BeFalse();
        outcome.Status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task RightOnTheAuthenticatedIdentity_UnauthenticatedBesideIt_DoesNotDisturb(string kind, bool unauthenticatedFirst)
    {
        var user = Of(unauthenticatedFirst, Person(Granting(kind)), Unauthenticated(new Claim("note", "x")));

        var outcome = await Run(user);

        outcome.NextCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Alert_NamesTheAuthenticatedUserAndListsOnlyItsPermissions(bool unauthenticatedFirst)
    {
        var user = Of(unauthenticatedFirst, Person(new Claim("permission", "orders:read")), Mallory("permission"));

        var outcome = await Run(user);

        outcome.Status.Should().Be(StatusCodes.Status403Forbidden);
        var call = outcome.Alerts.ReceivedCalls().Single();
        var data = (IDictionary<string, object>)call.GetArguments()[4]!;
        data["UserId"].Should().Be("alice");
        ((List<string>)data["UserPermissions"]).Should().BeEquivalentTo("orders:read");
    }
}
