using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// The shape most services are written in: <c>WebApplication</c>, no explicit routing
/// call. The pipeline guard must not refuse it, and must not let it through unguarded.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class ServiceCallerMinimalHostingTests
{
    [Fact]
    public async Task A_web_application_with_the_noelia_chain_serves_doors_and_refuses_strangers()
    {
        await using var app = await StartAsync(useNoelia: true);
        var client = app.GetTestClient();

        using var refused = await client.SendAsync(Wire.Request(HttpMethod.Get, "/internal/x"));
        using var allowed = await client.SendAsync(Wire.Request(HttpMethod.Get, "/internal/x", await Wire.Issued("advisor")));

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_web_application_that_never_calls_UseAuthorization_never_answers_a_door_with_a_500()
    {
        // Measured: WebApplication adds the authorization middleware itself once authorization
        // services are registered, so the guard has nothing to refuse here and the door is a 404.
        await using var app = await StartAsync(useNoelia: false);

        using var response = await app.GetTestClient().SendAsync(Wire.Request(HttpMethod.Get, "/internal/x"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<WebApplication> StartAsync(bool useNoelia)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNoelia(Config(), new Env(), "billing", noelia => noelia
            .UseDefaults()
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers("identity"));

        var app = builder.Build();
        if (useNoelia)
        {
            app.UseNoelia(new Env(), "billing");
        }
        else
        {
            app.UseAuthentication();
        }

        var doors = app.MapGroup("/internal");
        doors.MapGet("/x", () => Results.Text("x")).RequireServiceCaller("advisor");
        doors.MapServiceFallback();

        await app.StartAsync();
        return app;
    }
}
