using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.M2M;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// The whole chain over test servers: the client fetches a token from the issuer with
/// the secret it was given, the receiving service lets one named caller in and answers
/// the other as if the path did not exist.
/// </summary>
[Collection(ServiceCallerHttpCollection.Name)]
[Trait("Category", "Unit")]
public class ServiceTokenEndToEndTests
{
    private const string TokenUrl = "http://identity.test/m2m/token";

    [Fact]
    public async Task The_named_caller_gets_in_and_the_other_one_gets_a_404()
    {
        var advisor = ServiceClientSecret.Generate("advisor");
        var notification = ServiceClientSecret.Generate("notification");

        await using var issuer = await StartIssuer(advisor, notification);
        await using var receiver = await ServiceCallerHost.StartAsync(settings => settings.Map = endpoints =>
        {
            var doors = endpoints.MapGroup("/internal").RequireServiceCaller("advisor");
            doors.MapGet("/ping", () => Results.Text("pong"));
            doors.MapServiceFallback();
        });

        using var advisorClient = Client("advisor", advisor.Secret, issuer, receiver);
        using var notificationClient = Client("notification", notification.Secret, issuer, receiver);

        var allowed = await advisorClient.GetAsync("/internal/ping");
        var refused = await notificationClient.GetAsync("/internal/ping");
        var missing = await notificationClient.GetAsync("/internal/does-not-exist");

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await allowed.Content.ReadAsStringAsync()).Should().Be("pong");
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync()).Should().Be(await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_wrong_secret_means_no_request_reaches_the_receiver()
    {
        var advisor = ServiceClientSecret.Generate("advisor");
        var impostor = ServiceClientSecret.Generate("advisor");

        await using var issuer = await StartIssuer(advisor, ServiceClientSecret.Generate("notification"));
        var reached = 0;
        await using var receiver = await ServiceCallerHost.StartAsync(settings => settings.Map = endpoints =>
        {
            var doors = endpoints.MapGroup("/internal").RequireServiceCaller("advisor");
            doors.MapGet("/ping", () =>
            {
                reached++;
                return Results.Text("pong");
            });
            doors.MapServiceFallback();
        });
        using var client = Client("advisor", impostor.Secret, issuer, receiver);

        var call = () => client.GetAsync("/internal/ping");

        await call.Should().ThrowAsync<ServiceTokenUnavailableException>();
        reached.Should().Be(0);
    }

    private static HttpClient Client(string id, string secret, IAsyncDisposable issuer, ServiceCallerHost receiver)
    {
        var issuerHost = (IssuerHost)issuer;
        var config = Config(
            ("NOELIA_M2M_TOKEN_ENDPOINT", TokenUrl),
            ("NOELIA_M2M_CLIENT_ID", id),
            ("NOELIA_M2M_CLIENT_SECRET", secret));
        var services = Compose(config, noelia => noelia.UseServiceTokenClient());
        services.AddHttpClient(nameof(IServiceTokenProvider))
            .ConfigurePrimaryHttpMessageHandler(() => issuerHost.Server.CreateHandler());
        services.AddHttpClient("receiver", client => client.BaseAddress = new Uri("http://billing.test"))
            .AddServiceToken("identity")
            .ConfigurePrimaryHttpMessageHandler(() => ((TestServer)receiver.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateHandler());

        var provider = services.BuildServiceProvider();
        return new OwnedClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("receiver"), provider);
    }

    private static async Task<IssuerHost> StartIssuer(ServiceClientSecret advisor, ServiceClientSecret notification)
    {
        var config = Config(
            ("NOELIA_M2M_CLIENT_ADVISOR_SECRET_HASH", advisor.Hash),
            ("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH", notification.Hash));

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddNoelia(config, context.HostingEnvironment, "identity", noelia => noelia
                        .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
                        .UseServiceTokenIssuer(issuer => issuer
                            .Clients(config, "advisor", "notification")
                            .Audiences("identity")));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapServiceTokenEndpoint());
                }))
            .StartAsync();
        return new IssuerHost(host);
    }

    private sealed class IssuerHost(IHost host) : IAsyncDisposable
    {
        public TestServer Server => host.GetTestServer();

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    /// <summary>Disposing the client disposes the provider that built it.</summary>
    private sealed class OwnedClient(HttpClient inner, ServiceProvider provider) : HttpClient(new Forward(inner))
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                provider.Dispose();
            }
        }

        private sealed class Forward(HttpClient inner) : DelegatingHandler(new Pass(inner))
        {
        }

        private sealed class Pass(HttpClient inner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                inner.SendAsync(request, ct);
        }
    }
}
