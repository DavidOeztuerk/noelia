using System.Net;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Noelia.Infrastructure.Communication.Configuration;
using Noelia.Infrastructure.Security.M2M;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Tests.Security.M2M;

/// <summary>
/// The client-credentials mode of the provider: a token per audience, kept in the
/// process, fetched once however many ask, and an error when there is none.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceTokenProviderClientCredentialsTests
{
    private const string Endpoint = "http://identity.test/m2m/token";

    private readonly string _secret = ServiceClientSecret.Generate("advisor").Secret;
    private readonly TokenEndpointStub _endpoint = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceTokenCache _cache = new();
    private readonly IDistributedCache _distributed = Substitute.For<IDistributedCache>();

    private ServiceTokenProvider Provider(
        Action<M2MConfiguration>? tweak = null, ServiceTokenCache? cache = null, TokenEndpointStub? endpoint = null)
    {
        var m2m = new M2MConfiguration
        {
            Enabled = true,
            Protocol = ServiceTokenProtocol.ClientCredentials,
            TokenEndpoint = Endpoint,
            ClientId = "advisor",
            ClientSecret = _secret,
            RefreshBeforeExpiry = TimeSpan.FromMinutes(5)
        };
        tweak?.Invoke(m2m);
        return new ServiceTokenProvider(
            new HttpClient(endpoint ?? _endpoint),
            NullLogger<ServiceTokenProvider>.Instance,
            Options.Create(new ServiceCommunicationOptions { M2M = m2m }),
            _distributed,
            cache ?? _cache,
            _time);
    }

    [Fact]
    public async Task The_request_is_a_client_credentials_form_with_the_four_fields()
    {
        await Provider().GetTokenAsync("identity");

        _endpoint.Seen.TryPeek(out var seen).Should().BeTrue();
        seen.ContentType.Should().Be("application/x-www-form-urlencoded");
        seen.Form.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "advisor",
            ["client_secret"] = _secret,
            ["audience"] = "identity"
        });
    }

    [Fact]
    public async Task Two_audiences_get_two_tokens_and_each_is_fetched_once()
    {
        var provider = Provider();

        var identity = await provider.GetTokenAsync("identity");
        var billing = await provider.GetTokenAsync("billing");
        var identityAgain = await provider.GetTokenAsync("identity");
        var billingAgain = await provider.GetTokenAsync("billing");

        identity.Should().NotBe(billing);
        identityAgain.Should().Be(identity);
        billingAgain.Should().Be(billing);
        _endpoint.Requests.Should().Be(2);
        _endpoint.Seen.Select(s => s.Form["audience"]).Should().BeEquivalentTo("identity", "billing");
    }

    [Fact]
    public async Task Two_resolved_providers_share_one_token_through_the_singleton_cache()
    {
        // The provider is a typed HttpClient and so transient: two resolutions are two
        // instances, and the cache is what keeps them from fetching twice.
        var first = Provider();
        var second = Provider();

        var a = await first.GetTokenAsync("identity");
        var b = await second.GetTokenAsync("identity");

        b.Should().Be(a);
        _endpoint.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Parallel_callers_cause_one_token_request()
    {
        _endpoint.Delay = TimeSpan.FromMilliseconds(150);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => Provider().GetTokenAsync("identity"))));

        tokens.Distinct().Should().ContainSingle();
        _endpoint.Requests.Should().Be(1);
    }

    [Fact]
    public async Task A_token_in_the_renewal_window_is_replaced_by_one_new_request()
    {
        _endpoint.ExpiresIn = 300; // window = min(5 min, 300 s / 5) = 60 s
        var provider = Provider();
        var first = await provider.GetTokenAsync("identity");

        _time.Advance(TimeSpan.FromSeconds(239)); // 1 s before the window opens
        (await provider.GetTokenAsync("identity")).Should().Be(first);
        _endpoint.Requests.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(2)); // inside the window
        var renewed = await provider.GetTokenAsync("identity");

        renewed.Should().NotBe(first);
        _endpoint.Requests.Should().Be(2);
        (await provider.GetTokenAsync("identity")).Should().Be(renewed);
        _endpoint.Requests.Should().Be(2);
    }

    [Fact]
    public async Task The_window_is_the_configured_one_when_that_is_shorter_than_a_fifth()
    {
        _endpoint.ExpiresIn = 900; // a fifth would be 180 s
        var provider = Provider(m => m.RefreshBeforeExpiry = TimeSpan.FromSeconds(30));
        await provider.GetTokenAsync("identity");

        _time.Advance(TimeSpan.FromSeconds(860)); // 40 s left: outside a 30 s window
        await provider.GetTokenAsync("identity");
        _endpoint.Requests.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(15)); // 25 s left: inside
        await provider.GetTokenAsync("identity");
        _endpoint.Requests.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_renewal_inside_the_window_keeps_serving_the_token_that_is_still_valid()
    {
        var provider = Provider();
        var first = await provider.GetTokenAsync("identity");

        _time.Advance(TimeSpan.FromSeconds(250));
        _endpoint.Status = HttpStatusCode.ServiceUnavailable;

        (await provider.GetTokenAsync("identity")).Should().Be(first);
    }

    [Fact]
    public async Task An_expired_token_is_fetched_again_before_the_call_returns()
    {
        var provider = Provider();
        var first = await provider.GetTokenAsync("identity");

        _time.Advance(TimeSpan.FromSeconds(301));
        var second = await provider.GetTokenAsync("identity");

        second.Should().NotBe(first);
        _endpoint.Requests.Should().Be(2);
    }

    [Fact]
    public async Task An_expired_token_is_never_served_when_the_endpoint_is_down()
    {
        var provider = Provider();
        await provider.GetTokenAsync("identity");
        _time.Advance(TimeSpan.FromSeconds(301));
        _endpoint.Status = HttpStatusCode.ServiceUnavailable;

        var call = () => provider.GetTokenAsync("identity");

        await call.Should().ThrowAsync<ServiceTokenUnavailableException>();
    }

    [Fact]
    public async Task The_distributed_cache_is_neither_read_nor_written()
    {
        var provider = Provider(m => m.Audience = "identity");

        await provider.GetTokenAsync("identity");
        await provider.GetTokenAsync();
        await provider.GetTokenAsync("billing");
        await provider.RefreshTokenAsync();
        await provider.InvalidateTokenAsync();
        await provider.GetTokenInfoAsync();

        _distributed.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refusing_endpoint_is_a_ServiceTokenUnavailableException(HttpStatusCode status)
    {
        _endpoint.Status = status;

        var call = () => Provider().GetTokenAsync("identity");

        (await call.Should().ThrowAsync<ServiceTokenUnavailableException>())
            .Which.Message.Should().NotContain(_secret);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"\",\"token_type\":\"Bearer\",\"expires_in\":300}")]
    [InlineData("{\"access_token\":\"x\",\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"x\",\"token_type\":\"Bearer\",\"expires_in\":0}")]
    [InlineData("{\"access_token\":\"x\",\"token_type\":\"mac\",\"expires_in\":300}")]
    [InlineData("{\"access_token\":\"x y\",\"token_type\":\"Bearer\",\"expires_in\":300}")]
    public async Task An_answer_that_is_not_a_token_is_a_ServiceTokenUnavailableException(string body)
    {
        _endpoint.RawBody = body;

        var call = () => Provider().GetTokenAsync("identity");

        await call.Should().ThrowAsync<ServiceTokenUnavailableException>();
    }

    [Fact]
    public async Task A_network_failure_is_a_ServiceTokenUnavailableException_and_keeps_its_cause()
    {
        _endpoint.Throw = new HttpRequestException("connection refused");

        var call = () => Provider().GetTokenAsync("identity");

        (await call.Should().ThrowAsync<ServiceTokenUnavailableException>())
            .Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_failure_is_not_cached_so_the_next_call_asks_again()
    {
        _endpoint.Status = HttpStatusCode.Unauthorized;
        var provider = Provider();
        await provider.Invoking(p => p.GetTokenAsync("identity")).Should().ThrowAsync<ServiceTokenUnavailableException>();

        _endpoint.Status = HttpStatusCode.OK;

        (await provider.GetTokenAsync("identity")).Should().Be(TokenEndpointStub.TokenNumber(1));
        _endpoint.Requests.Should().Be(2);
    }

    [Fact]
    public async Task Cancelling_the_call_is_a_cancellation_and_not_an_unavailable_token()
    {
        _endpoint.Delay = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var call = () => Provider().GetTokenAsync("identity", cts.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Failures_are_logged_without_the_secret_or_the_response_body()
    {
        var logger = new CapturingLoggerStub();
        _endpoint.Status = HttpStatusCode.Unauthorized;
        var provider = new ServiceTokenProvider(
            new HttpClient(_endpoint), logger,
            Options.Create(new ServiceCommunicationOptions
            {
                M2M = new M2MConfiguration
                {
                    Protocol = ServiceTokenProtocol.ClientCredentials, TokenEndpoint = Endpoint,
                    ClientId = "advisor", ClientSecret = _secret
                }
            }),
            null, _cache, _time);

        await provider.Invoking(p => p.GetTokenAsync("identity")).Should().ThrowAsync<ServiceTokenUnavailableException>();

        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().NotContain(line => line.Contains(_secret) || line.Contains("invalid_client"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Identity")]
    [InlineData("a b")]
    public async Task A_malformed_audience_is_an_ArgumentException_and_no_request_is_made(string audience)
    {
        var call = () => Provider().GetTokenAsync(audience);

        await call.Should().ThrowAsync<ArgumentException>();
        _endpoint.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Without_an_audience_the_configured_default_is_asked_for()
    {
        var provider = Provider(m => m.Audience = "identity");

        var token = await provider.GetTokenAsync();

        token.Should().Be(TokenEndpointStub.TokenNumber(1));
        _endpoint.Seen.Single().Form["audience"].Should().Be("identity");
        (await provider.GetTokenAsync("identity")).Should().Be(token);
        _endpoint.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Without_an_audience_and_without_a_default_it_is_an_InvalidOperationException()
    {
        var call = () => Provider().GetTokenAsync();

        await call.Should().ThrowAsync<InvalidOperationException>();
        _endpoint.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_replaces_the_default_audiences_token_and_invalidate_drops_it()
    {
        var provider = Provider(m => m.Audience = "identity");
        var first = await provider.GetTokenAsync();

        var refreshed = await provider.RefreshTokenAsync();
        refreshed.Should().NotBe(first);
        (await provider.GetTokenAsync()).Should().Be(refreshed);
        _endpoint.Requests.Should().Be(2);

        await provider.InvalidateTokenAsync();
        (await provider.GetTokenAsync()).Should().NotBe(refreshed);
        _endpoint.Requests.Should().Be(3);
    }

    [Fact]
    public async Task Token_info_describes_the_default_audiences_token()
    {
        var provider = Provider(m => m.Audience = "identity");

        var info = await provider.GetTokenInfoAsync();

        info.Should().NotBeNull();
        info!.Token.Should().Be(TokenEndpointStub.TokenNumber(1));
        info.ExpiresAt.Should().Be(_time.GetUtcNow().UtcDateTime.AddSeconds(300));
    }

    [Fact]
    public async Task The_legacy_mode_does_not_hand_out_audience_tokens()
    {
        var provider = Provider(m => m.Protocol = ServiceTokenProtocol.Legacy);

        var call = () => provider.GetTokenAsync("identity");

        await call.Should().ThrowAsync<InvalidOperationException>();
        _endpoint.Requests.Should().Be(0);
    }

    [Fact]
    public async Task A_disabled_provider_hands_out_no_audience_tokens()
    {
        var provider = Provider(m => m.Enabled = false);

        var call = () => provider.GetTokenAsync("identity");

        await call.Should().ThrowAsync<InvalidOperationException>();
        _endpoint.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Another_client_in_the_same_process_does_not_share_a_token()
    {
        var other = new TokenEndpointStub();
        var a = await Provider().GetTokenAsync("identity");
        var b = await Provider(m => m.ClientId = "notification", endpoint: other).GetTokenAsync("identity");

        other.Requests.Should().Be(1);
        _endpoint.Requests.Should().Be(1);
        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    private sealed class CapturingLoggerStub : ILogger<ServiceTokenProvider>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + " " + exception);
    }
}
