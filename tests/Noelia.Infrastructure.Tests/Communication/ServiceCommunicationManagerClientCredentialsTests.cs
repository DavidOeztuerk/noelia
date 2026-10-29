using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Messaging;
using Noelia.Infrastructure.Communication;
using Noelia.Infrastructure.Communication.Configuration;
using Noelia.Infrastructure.Security.M2M;

namespace Noelia.Infrastructure.Tests.Communication;

/// <summary>
/// F3 of ADR-0002: in client-credentials mode a token that cannot be had stops the call,
/// and no configured fallback credential takes its place.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceCommunicationManagerClientCredentialsTests
{
    private sealed class Target : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"data\":{}}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Source(Func<string, Task<string>> answer) : IServiceTokenSource
    {
        public List<string> Audiences { get; } = [];

        public Task<string> GetTokenAsync(string audience, CancellationToken cancellationToken = default)
        {
            Audiences.Add(audience);
            return answer(audience);
        }
    }

    private static ServiceCommunicationManager Manager(
        Target target, IServiceTokenSource? source, bool gateway = false, string? fallback = "fallback-bearer",
        ServiceTokenProtocol protocol = ServiceTokenProtocol.ClientCredentials,
        IServiceTokenProvider? provider = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ServiceEndpoints:UserService"] = "http://user.test",
            ["ServiceEndpoints:Gateway"] = "http://gateway.test"
        };
        if (fallback is not null)
        {
            values["ServiceCommunication:BearerToken"] = fallback;
        }

        return new ServiceCommunicationManager(
            new HttpClient(target),
            Substitute.For<IEventBus>(),
            NullLogger<ServiceCommunicationManager>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            options: Options.Create(new ServiceCommunicationOptions
            {
                UseGateway = gateway,
                EnableResponseCaching = false,
                M2M = new M2MConfiguration { Enabled = true, Protocol = protocol }
            }),
            tokenProvider: provider,
            tokenSource: source);
    }

    [Fact]
    public async Task The_audience_is_the_target_service_name_in_lower_case()
    {
        var target = new Target();
        var source = new Source(a => Task.FromResult("tok-" + a));

        await Manager(target, source).GetAsync<object>("UserService", "/x");

        source.Audiences.Should().Equal("userservice");
        target.Seen.Single().Headers.Authorization!.ToString().Should().Be("Bearer tok-userservice");
    }

    [Fact]
    public async Task Through_the_gateway_the_audience_is_still_the_target_and_not_the_gateway()
    {
        var target = new Target();
        var source = new Source(a => Task.FromResult("tok-" + a));

        await Manager(target, source, gateway: true).GetAsync<object>("UserService", "/x");

        source.Audiences.Should().Equal("userservice");
        target.Seen.Single().RequestUri!.Host.Should().Be("gateway.test");
    }

    [Fact]
    public async Task A_token_that_cannot_be_had_stops_the_call_and_the_fallback_is_not_used()
    {
        var target = new Target();
        var source = new Source(_ => throw new ServiceTokenUnavailableException("no token"));

        var get = () => Manager(target, source, fallback: "fallback-bearer").GetAsync<object>("UserService", "/x");

        await get.Should().ThrowAsync<ServiceTokenUnavailableException>();
        target.Seen.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_empty_token_stops_the_call_and_the_fallback_is_not_used(string? token)
    {
        var target = new Target();
        var source = new Source(_ => Task.FromResult(token!));

        var get = () => Manager(target, source).GetAsync<object>("UserService", "/x");

        await get.Should().ThrowAsync<ServiceTokenUnavailableException>();
        target.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_any_token_source_the_mode_throws_and_sends_nothing()
    {
        var target = new Target();

        var get = () => Manager(target, source: null, fallback: "fallback-bearer").GetAsync<object>("UserService", "/x");

        await get.Should().ThrowAsync<InvalidOperationException>();
        target.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task The_environment_fallback_is_skipped_too()
    {
        var target = new Target();
        var source = new Source(_ => throw new ServiceTokenUnavailableException("no token"));
        Environment.SetEnvironmentVariable("SERVICE_COMMUNICATION_BEARER", "env-fallback");
        try
        {
            var get = () => Manager(target, source, fallback: null).SendRequestAsync<object, object>("UserService", "/x", new object());

            await get.Should().ThrowAsync<ServiceTokenUnavailableException>();
            target.Seen.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SERVICE_COMMUNICATION_BEARER", null);
        }
    }

    [Fact]
    public async Task A_caller_supplied_authorization_header_is_an_error_and_nothing_is_sent()
    {
        var target = new Target();
        var source = new Source(a => Task.FromResult("tok"));

        var get = () => Manager(target, source).GetAsync<object>(
            "UserService", "/x", headers: new Dictionary<string, string> { ["Authorization"] = "Bearer person" });

        await get.Should().ThrowAsync<InvalidOperationException>();
        target.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_still_sends_the_fallback_when_there_is_no_provider()
    {
        var target = new Target();

        await Manager(target, source: null, protocol: ServiceTokenProtocol.Legacy).GetAsync<object>("UserService", "/x");

        target.Seen.Single().Headers.Authorization!.ToString().Should().Be("Bearer fallback-bearer");
    }

    [Fact]
    public async Task Legacy_uses_the_provider_token_and_never_asks_for_an_audience()
    {
        var target = new Target();
        var provider = Substitute.For<IServiceTokenProvider>();
        provider.GetTokenAsync(Arg.Any<CancellationToken>()).Returns("legacy-token");
        var source = new Source(_ => Task.FromResult("never"));

        await Manager(target, source, protocol: ServiceTokenProtocol.Legacy, provider: provider)
            .GetAsync<object>("UserService", "/x");

        target.Seen.Single().Headers.Authorization!.ToString().Should().Be("Bearer legacy-token");
        source.Audiences.Should().BeEmpty();
    }
}
