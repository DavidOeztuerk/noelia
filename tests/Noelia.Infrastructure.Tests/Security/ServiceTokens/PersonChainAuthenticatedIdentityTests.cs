using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;
using Noelia.InMemory.Security;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceCallerHost;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// ADR-0007 (#59), decision 4 through the whole chain over real tokens: a revoked person is
/// refused whatever else stands in the principal, in either order, with and without
/// <c>PermissionEnforcement</c>, also when two person schemes sit in one policy.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class PersonChainAuthenticatedIdentityTests
{
    private const string Door = "/people/me";
    private const string TwoSchemes = "/people/two";
    private const string TwoSchemesReversed = "/people/two-reversed";
    private const string Revoked = "revoked-token";

    /// <summary>Puts an identity nobody signed in beside the person, in front or behind.</summary>
    private sealed class Appending(bool inFront) : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var stranger = new ClaimsIdentity([new Claim("note", "nobody vouches")]);
            return Task.FromResult(inFront
                ? new ClaimsPrincipal([stranger, .. principal.Identities])
                : new ClaimsPrincipal([.. principal.Identities, stranger]));
        }
    }

    /// <summary>A second person scheme beside the bearer one: it carries its own jti.</summary>
    private sealed class SecondPersonHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "SecondPerson";
        public const string Header = "X-Second-Person-Jti";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var jti))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var identity = new ClaimsIdentity(
                [new Claim("sub", "second-person"), new Claim("jti", jti.ToString()), new Claim("iat", issuedAt)],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private static async Task<ServiceCallerHost> Chain(bool permissions, bool? strangerInFront)
    {
        var store = new InMemoryTokenRevocationStore();
        await store.RevokeTokenAsync(Revoked, DateTimeOffset.UtcNow.AddHours(1), "test");
        return await StartAsync(settings =>
        {
            settings.Compose = noelia =>
            {
                noelia.UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid)).UseServiceCallers(ThisAudience);
                if (!permissions)
                {
                    noelia.Without(NoeliaModule.PermissionEnforcement, "the test is about revocation");
                }
            };
            settings.Services = services =>
            {
                services.AddSingleton<ITokenRevocationEvaluator>(store);
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, SecondPersonHandler>(SecondPersonHandler.SchemeName, _ => { });
                if (strangerInFront is { } inFront)
                {
                    services.AddSingleton<IClaimsTransformation>(new Appending(inFront));
                }
            };
            settings.AfterChain = app => app.UseTokenRevocation();
            settings.Map = endpoints =>
            {
                endpoints.MapGet(Door, () => Results.Text("person")).RequireAuthorization();
                endpoints.MapGet(TwoSchemes, () => Results.Text("two")).RequireAuthorization(
                    new AuthorizeAttribute
                    {
                        AuthenticationSchemes = $"{JwtBearerDefaults.AuthenticationScheme},{SecondPersonHandler.SchemeName}"
                    });
                endpoints.MapGet(TwoSchemesReversed, () => Results.Text("two")).RequireAuthorization(
                    new AuthorizeAttribute
                    {
                        AuthenticationSchemes = $"{SecondPersonHandler.SchemeName},{JwtBearerDefaults.AuthenticationScheme}"
                    });
            };
        });
    }

    private static HttpRequestMessage Get(string path, string bearerJti, string? secondJti = null)
    {
        var request = Wire.Request(HttpMethod.Get, path, Wire.Person(tokenId: bearerJti));
        if (secondJti is not null)
        {
            request.Headers.Add(SecondPersonHandler.Header, secondJti);
        }

        return request;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ARevokedPerson_BesideAnIdentityNobodySignedIn_IsRefused_WithAndWithoutPermissionEnforcement(
        bool permissions, bool strangerInFront)
    {
        await using var host = await Chain(permissions, strangerInFront);

        using var response = await host.Client.SendAsync(Get(Door, Revoked));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task APersonWhoStands_BesideAnIdentityNobodySignedIn_GetsIn(bool permissions, bool strangerInFront)
    {
        await using var host = await Chain(permissions, strangerInFront);

        using var response = await host.Client.SendAsync(Get(Door, "fine-token"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static TheoryData<bool, string, bool> TwoPersonCases()
    {
        var data = new TheoryData<bool, string, bool>();
        foreach (var permissions in new[] { true, false })
        {
            foreach (var door in new[] { TwoSchemes, TwoSchemesReversed })
            {
                foreach (var bearerRevoked in new[] { true, false })
                {
                    data.Add(permissions, door, bearerRevoked);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TwoPersonCases))]
    public async Task TwoPersonSchemes_OneRevoked_IsRefused_InBothOrders(bool permissions, string door, bool bearerRevoked)
    {
        await using var host = await Chain(permissions, strangerInFront: null);

        using var response = await host.Client.SendAsync(
            bearerRevoked ? Get(door, Revoked, "second-fine") : Get(door, "bearer-fine", Revoked));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(TwoSchemes)]
    [InlineData(TwoSchemesReversed)]
    public async Task TwoPersonSchemes_NeitherRevoked_GetsIn(string door)
    {
        await using var host = await Chain(permissions: true, strangerInFront: null);

        using var response = await host.Client.SendAsync(Get(door, "bearer-fine", "second-fine"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- the way MIGRATION.md will show (ADR-0007 decision 9) -------------------------------

    /// <summary>The example transformation of the ADR; the rights are fixed instead of looked up.</summary>
    private sealed class DocumentedTransformation : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var person = principal.Identities.FirstOrDefault(i =>
                i.IsAuthenticated
                && i.AuthenticationType != Noelia.Infrastructure.Security.ServiceTokens.ServiceTokenDefaults.AuthenticationScheme);
            var sub = person?.FindFirst("sub")?.Value ?? person?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (person is not null && sub is not null && !person.HasClaim("permission", "orders:delete"))
            {
                person.AddClaim(new Claim("permission", "orders:delete"));
            }

            return Task.FromResult(principal);
        }
    }

    [Fact]
    public async Task TheDocumentedTransformation_AddsNothingToAServiceIdentity_AndGivesAPersonTheRight()
    {
        await using var host = await StartAsync(settings =>
        {
            settings.Services = services => services.AddSingleton<IClaimsTransformation, DocumentedTransformation>();
            settings.Map = endpoints =>
            {
                endpoints.MapGet("/internal/rights", (HttpContext context) => Results.Text(string.Join(",",
                        context.User.Identities.SelectMany(i => i.FindAll("permission")).Select(c => c.Value))))
                    .RequireServiceCaller("advisor");
                endpoints.MapGet("/people/delete", () => Results.Text("deleted"))
                    .RequireAuthorization(new Noelia.Infrastructure.Security.Authorization.RequirePermissionAttribute("orders:delete"));
            };
        });

        using var service = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, "/internal/rights", await Wire.Issued("advisor")));
        using var person = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/people/delete", Wire.Person()));

        service.StatusCode.Should().Be(HttpStatusCode.OK);
        (await service.Content.ReadAsStringAsync()).Should().BeEmpty("a service token never gets a person's rights");
        person.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
