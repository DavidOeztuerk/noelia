using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// What the receiving side refuses to start with: a ring that cannot protect the
/// doors, an audience that opens the people's API, and a pipeline that would
/// answer a door with a 500.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class ServiceCallerStartupTests
{
    [Fact]
    public async Task A_verify_only_ring_starts()
    {
        await using var host = await ServiceCallerHost.StartAsync();

        host.Services.Should().NotBeNull();
    }

    [Fact]
    public async Task The_order_of_UseJwt_and_UseServiceCallers_does_not_matter()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Compose = noelia => noelia
            .UseServiceCallers("identity")
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid)));

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, "/internal/account/7", await Wire.Issued("advisor")));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_service_that_issues_may_also_receive()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Compose = noelia => noelia
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceCallers("identity"));

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, "/internal/account/7", await Wire.Issued("advisor")));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_any_key_ring_the_service_does_not_start()
    {
        var act = () => ServiceCallerHost.StartAsync(s => s.Compose = noelia => noelia
            .UseJwt(jwt => jwt.From("https://idp.invalid"))
            .UseServiceCallers("identity"));

        Messages((await act.Should().ThrowAsync<Exception>()).Which).Should().Contain("KeyRing");
    }

    [Fact]
    public async Task Without_UseJwt_at_all_the_service_does_not_start()
    {
        var act = () => ServiceCallerHost.StartAsync(s => s.Compose = noelia => noelia.UseServiceCallers("identity"));

        Messages((await act.Should().ThrowAsync<Exception>()).Which).Should().Contain("UseJwt");
    }

    [Fact]
    public async Task A_shared_secret_ring_does_not_start_because_every_verifier_could_mint_a_caller()
    {
        var secret = Wire.RandomSecret();
        var config = Config(("JwtSettings:Secret", secret));
        var act = async () =>
        {
            using var host = await StartCoreAsync(config, noelia => noelia
                .UseJwt(jwt => jwt.FromSharedSecret())
                .UseServiceCallers("identity"));
        };

        var thrown = Messages((await act.Should().ThrowAsync<Exception>()).Which);
        thrown.Should().Contain("asymmetric");
        thrown.Should().NotContain(secret);
    }

    [Fact]
    public async Task An_audience_equal_to_the_person_audience_does_not_start()
    {
        var act = () => ServiceCallerHost.StartAsync(s => s.Compose = noelia => noelia
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers(PersonAudience));

        Messages((await act.Should().ThrowAsync<Exception>()).Which).Should().Contain("JwtSettings:Audience");
    }

    [Theory]
    [InlineData("Identity")]
    [InlineData("")]
    [InlineData("x_y")]
    [InlineData("1identity")]
    [InlineData("https://identity")]
    public void An_audience_that_is_not_an_identifier_is_refused_at_the_line_that_names_it(string audience)
    {
        var act = () => Compose(Config(), noelia => noelia
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers(audience));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_pipeline_without_authorization_middleware_does_not_start()
    {
        // Without it an endpoint that carries authorization metadata is a 500 per request,
        // and a door that answers 500 instead of 404 is a door that says it exists.
        var act = () => StartPipelineAsync(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseEndpoints(e => e.MapGet("/internal/x", () => "x").RequireServiceCaller("advisor"));
        });

        Messages((await act.Should().ThrowAsync<Exception>()).Which).Should().Contain("UseAuthorization");
    }

    [Fact]
    public async Task A_pipeline_with_the_authorization_middleware_starts()
    {
        var act = () => StartPipelineAsync(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(e => e.MapGet("/internal/x", () => "x").RequireServiceCaller("advisor"));
        });

        await act.Should().NotThrowAsync();
    }

    private static string Messages(Exception exception)
    {
        var parts = new List<string>();
        for (var e = exception; e is not null; e = e.InnerException!)
        {
            parts.Add(e.Message);
            if (e is AggregateException aggregate)
            {
                parts.AddRange(aggregate.InnerExceptions.Select(inner => inner.Message));
            }
        }

        return string.Join("\n", parts);
    }

    private static async Task StartPipelineAsync(Action<IApplicationBuilder> pipeline)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddNoelia(Config(), new Env(), "billing", noelia => noelia
                        .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
                        .UseServiceCallers("identity"));
                })
                .Configure(pipeline))
            .StartAsync();
    }

    private static async Task<IHost> StartCoreAsync(IConfiguration config, Action<Noelia.Abstractions.Hosting.NoeliaBuilder> compose)
    {
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddNoelia(config, new Env(), "billing", compose);
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}
