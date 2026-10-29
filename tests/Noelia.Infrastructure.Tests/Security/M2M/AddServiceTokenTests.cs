using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Infrastructure.Security.M2M;

namespace Noelia.Infrastructure.Tests.Security.M2M;

/// <summary>
/// <c>AddServiceToken(audience)</c>: the header is set, never overwritten, and a token
/// that cannot be had means the request is not sent.
/// </summary>
[Trait("Category", "Unit")]
public class AddServiceTokenTests
{
    private sealed class Target : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FixedSource(Func<string, string> answer) : IServiceTokenSource
    {
        public List<string> Audiences { get; } = [];

        public Task<string> GetTokenAsync(string audience, CancellationToken cancellationToken = default)
        {
            Audiences.Add(audience);
            return Task.FromResult(answer(audience));
        }
    }

    private static HttpClient Client(IServiceTokenSource source, Target target, string audience = "identity")
    {
        var services = new ServiceCollection();
        services.AddSingleton(source);
        services.AddHttpClient("target", c => c.BaseAddress = new Uri("http://identity.test"))
            .AddServiceToken(audience)
            .ConfigurePrimaryHttpMessageHandler(() => target);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("target");
    }

    [Fact]
    public async Task The_request_carries_the_token_of_the_named_audience()
    {
        var target = new Target();
        var source = new FixedSource(audience => "token-for-" + audience);

        await Client(source, target).GetAsync("/internal/x");

        target.Seen.Single().Headers.Authorization!.ToString().Should().Be("Bearer token-for-identity");
        source.Audiences.Should().Equal("identity");
    }

    [Fact]
    public async Task Every_request_asks_the_source_again_so_renewal_is_the_sources_business()
    {
        var target = new Target();
        var counter = 0;
        var source = new FixedSource(_ => "t" + ++counter);
        var client = Client(source, target);

        await client.GetAsync("/a");
        await client.GetAsync("/b");

        target.Seen.Select(r => r.Headers.Authorization!.Parameter).Should().Equal("t1", "t2");
    }

    [Fact]
    public async Task An_authorization_header_already_on_the_request_is_an_error_and_nothing_is_sent()
    {
        var target = new Target();
        var source = new FixedSource(_ => "token");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer someone-elses");

        var send = () => Client(source, target).SendAsync(request);

        await send.Should().ThrowAsync<InvalidOperationException>();
        target.Seen.Should().BeEmpty();
        source.Audiences.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_that_cannot_be_had_stops_the_request()
    {
        var target = new Target();
        var source = new ThrowingSource();

        var send = () => Client(source, target).GetAsync("/x");

        await send.Should().ThrowAsync<ServiceTokenUnavailableException>();
        target.Seen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Identity")]
    [InlineData("1billing")]
    public void A_malformed_audience_fails_when_the_client_is_configured(string audience)
    {
        var services = new ServiceCollection();

        var add = () => services.AddHttpClient("x").AddServiceToken(audience);

        add.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task With_the_real_provider_a_refusing_endpoint_means_no_call_to_the_target()
    {
        var endpoint = new TokenEndpointStub { Status = HttpStatusCode.Unauthorized };
        var target = new Target();
        var provider = new ServiceTokenProvider(
            new HttpClient(endpoint), NullLogger<ServiceTokenProvider>.Instance,
            Microsoft.Extensions.Options.Options.Create(new Noelia.Infrastructure.Communication.Configuration.ServiceCommunicationOptions
            {
                M2M = new Noelia.Infrastructure.Communication.Configuration.M2MConfiguration
                {
                    Protocol = Noelia.Infrastructure.Communication.Configuration.ServiceTokenProtocol.ClientCredentials,
                    TokenEndpoint = "http://identity.test/m2m/token", ClientId = "advisor",
                    ClientSecret = Noelia.Infrastructure.Security.ServiceTokens.ServiceClientSecret.Generate("advisor").Secret
                }
            }),
            null, new ServiceTokenCache());

        var send = () => Client(provider, target).GetAsync("/x");

        await send.Should().ThrowAsync<ServiceTokenUnavailableException>();
        target.Seen.Should().BeEmpty();
        endpoint.Requests.Should().Be(1);
    }

    private sealed class ThrowingSource : IServiceTokenSource
    {
        public Task<string> GetTokenAsync(string audience, CancellationToken cancellationToken = default) =>
            throw new ServiceTokenUnavailableException("no token");
    }
}
