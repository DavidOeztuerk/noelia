using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Identity;
using Noelia.Infrastructure.Security.ServiceTokens;
using Noelia.InMemory.Security;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceCallerHost;
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

    private static Task<ServiceCallerHost> Chain(bool revocation, bool principal) =>
        StartAsync(settings =>
        {
            settings.Compose = noelia => noelia
                .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
                .UseServiceCallers(ThisAudience)
                .Use(NoeliaModule.Principal);
            settings.Services = services =>
                services.AddSingleton<ITokenRevocationEvaluator>(new InMemoryTokenRevocationStore());
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
            };
        });
}
