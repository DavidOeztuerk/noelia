using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Infrastructure.Security.Authorization;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// ADR-0007 (#59) through the default chain over a real JWT person: the claims-transformation
/// path the owner's decision is about. The middleware and the policy handler both stand behind
/// the same door, so a hole in either shows here.
/// </summary>
[Collection(ServiceCallerHttpCollection.Name)]
[Trait("Category", "Unit")]
public class PermissionFullChainAuthenticatedIdentityTests
{
    private const string Door = "/orders/delete";

    /// <summary>Appends an identity with a right but without an AuthenticationType, in front of or behind the person.</summary>
    private sealed class AppendingTransformation(bool inFront, Claim[] claims, bool toPerson) : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            if (toPerson)
            {
                // The migration path of ADR-0007 decision 9: the claim goes onto the signed-in identity.
                var person = principal.Identities.First(identity => identity.IsAuthenticated);
                if (!claims.All(claim => person.HasClaim(claim.Type, claim.Value)))
                {
                    person.AddClaims(claims);
                }

                return Task.FromResult(principal);
            }

            var appended = new ClaimsIdentity(claims);
            return Task.FromResult(inFront
                ? new ClaimsPrincipal([appended, .. principal.Identities])
                : new ClaimsPrincipal([.. principal.Identities, appended]));
        }
    }

    private static Task<ServiceCallerHost> Host(bool inFront, bool toPerson = false) =>
        ServiceCallerHost.StartAsync(settings =>
        {
            settings.Services = services =>
            {
                services.AddSingleton<IClaimsTransformation>(
                    new AppendingTransformation(inFront, [new Claim("permission", "orders:delete")], toPerson));
            };
            settings.Map = endpoints =>
            {
                endpoints.MapGet(Door, () => Results.Text("deleted"))
                    .RequireAuthorization(new RequirePermissionAttribute("orders:delete"));
            };
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RightOnAnIdentityNobodySignedIn_IsRefusedWith403(bool inFront)
    {
        await using var host = await Host(inFront);

        var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Door, Wire.Person()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RightOnThePersonIdentity_IsAllowed(bool inFront)
    {
        await using var host = await Host(inFront, toPerson: true);

        var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Door, Wire.Person()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NoCredentialsAtAll_Is401()
    {
        await using var host = await Host(inFront: true);

        var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Door));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
