using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Communication;
using Noelia.Infrastructure.Communication.Configuration;
using Noelia.Infrastructure.Security.M2M;
using Noelia.Infrastructure.Security.ServiceTokens;
using Noelia.Infrastructure.Tests.Security.M2M;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Builder;

/// <summary>
/// <c>UseServiceTokenClient()</c>: what it sets, what it refuses to start with, and that
/// it and the communication module are one registration and one cache.
/// </summary>
[Trait("Category", "Unit")]
public class UseServiceTokenClientTests
{
    private const string Endpoint = "NOELIA_M2M_TOKEN_ENDPOINT";
    private const string ClientId = "NOELIA_M2M_CLIENT_ID";
    private const string Secret = "NOELIA_M2M_CLIENT_SECRET";

    private static readonly string GoodSecret = ServiceClientSecret.Generate("advisor").Secret;

    private static IConfiguration Good(params (string Key, string Value)[] more) => Config([
        (Endpoint, "http://identity.test/m2m/token"),
        (ClientId, "advisor"),
        (Secret, GoodSecret),
        .. more]);

    private static ServiceProvider Build(IConfiguration config, Action<IServiceCollection>? services = null)
    {
        var collection = Compose(config, noelia => noelia.UseServiceTokenClient());
        services?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

    [Fact]
    public void The_variables_become_the_client_credentials_configuration()
    {
        using var provider = Build(Good());

        var m2m = provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value.M2M;

        m2m.Enabled.Should().BeTrue();
        m2m.Protocol.Should().Be(ServiceTokenProtocol.ClientCredentials);
        m2m.TokenEndpoint.Should().Be("http://identity.test/m2m/token");
        m2m.ClientId.Should().Be("advisor");
        m2m.ClientSecret.Should().Be(GoodSecret);
    }

    [Fact]
    public void The_module_is_in_the_composition_and_the_source_resolves_to_the_provider()
    {
        using var provider = Build(Good());

        provider.GetRequiredService<NoeliaComposition>().Included.Should().Contain(NoeliaModule.ServiceTokenClient);
        provider.GetRequiredService<IServiceTokenSource>().Should().BeOfType<ServiceTokenProvider>();
        provider.GetRequiredService<IServiceTokenProvider>().Should().BeOfType<ServiceTokenProvider>();
    }

    [Fact]
    public void The_module_overrides_a_section_that_says_legacy()
    {
        using var provider = Build(Good(
            ("ServiceCommunication:M2M:Protocol", "Legacy"),
            ("ServiceCommunication:M2M:Enabled", "false"),
            ("ServiceCommunication:M2M:ClientSecret", "from-the-section")));

        var m2m = provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value.M2M;

        m2m.Protocol.Should().Be(ServiceTokenProtocol.ClientCredentials);
        m2m.Enabled.Should().BeTrue();
        m2m.ClientSecret.Should().Be(GoodSecret);
    }

    [Fact]
    public void Without_the_module_the_protocol_is_legacy()
    {
        var services = Compose(Config(), _ => { });
        services.AddServiceCommunication(Config());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value.M2M.Protocol
            .Should().Be(ServiceTokenProtocol.Legacy);
    }

    [Theory]
    [InlineData(Endpoint)]
    [InlineData(ClientId)]
    [InlineData(Secret)]
    public void A_missing_variable_stops_the_start_and_is_named(string missing)
    {
        var config = Good();
        config[missing] = null;
        using var provider = Build(config);

        var resolve = () => provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*" + missing + "*");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://identity.test/m2m/token")]
    public void An_endpoint_that_is_not_an_absolute_http_url_stops_the_start(string value)
    {
        using var provider = Build(Good((Endpoint, value)));

        var resolve = () => provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*" + Endpoint + "*");
    }

    [Theory]
    [InlineData("Advisor")]
    [InlineData("1advisor")]
    [InlineData("a b")]
    public void A_client_id_that_is_not_an_identifier_stops_the_start(string value)
    {
        using var provider = Build(Good((ClientId, value)));

        var resolve = () => provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*" + ClientId + "*");
    }

    [Fact]
    public void A_malformed_secret_stops_the_start_and_its_value_is_never_in_the_message()
    {
        const string wrong = "hunter2-this-is-not-a-secret-shape";
        using var provider = Build(Good((Secret, wrong)));

        var resolve = () => provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value;

        var message = resolve.Should().Throw<OptionsValidationException>().Which.Message;
        message.Should().Contain(Secret).And.NotContain(wrong);
    }

    [Fact]
    public void A_hash_in_the_secret_variable_is_refused_without_repeating_it()
    {
        // The wrong half of the pair, pasted: the issuer's hash where the client's secret belongs.
        using var provider = Build(Good((Secret, KnownHash)));

        var resolve = () => provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().Which.Message.Should().NotContain(KnownHash);
    }

    [Fact]
    public void The_start_check_does_not_depend_on_the_order_of_the_communication_module()
    {
        foreach (var communicationFirst in new[] { true, false })
        {
            var config = Good();
            var services = new ServiceCollection();
            services.AddLogging();
            if (communicationFirst)
            {
                services.AddServiceCommunication(config);
            }

            Noelia.Infrastructure.Extensions.NoeliaServiceCollectionExtensions.AddNoelia(
                services, config, new Env(), "advisor", noelia => noelia.UseServiceTokenClient());
            if (!communicationFirst)
            {
                services.AddServiceCommunication(config);
            }

            using var provider = services.BuildServiceProvider();
            var m2m = provider.GetRequiredService<IOptions<ServiceCommunicationOptions>>().Value.M2M;

            m2m.Protocol.Should().Be(ServiceTokenProtocol.ClientCredentials, $"communicationFirst={communicationFirst}");
            m2m.ClientId.Should().Be("advisor");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task One_registration_and_one_cache_whichever_comes_first(bool communicationFirst)
    {
        var config = Good();
        var endpoint = new TokenEndpointStub();
        var services = new ServiceCollection();
        services.AddLogging();
        if (communicationFirst)
        {
            services.AddServiceCommunication(config);
        }

        Noelia.Infrastructure.Extensions.NoeliaServiceCollectionExtensions.AddNoelia(
            services, config, new Env(), "advisor", noelia => noelia.UseServiceTokenClient());
        if (!communicationFirst)
        {
            services.AddServiceCommunication(config);
        }

        services.AddHttpClient(nameof(IServiceTokenProvider)).ConfigurePrimaryHttpMessageHandler(() => endpoint);
        using var provider = services.BuildServiceProvider();

        services.Count(d => d.ServiceType == typeof(IServiceTokenProvider)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(ServiceTokenCache)).Should().Be(1);

        // Two resolutions are two instances and still one token request.
        var first = await provider.GetRequiredService<IServiceTokenSource>().GetTokenAsync("identity");
        var second = await provider.GetRequiredService<IServiceTokenSource>().GetTokenAsync("identity");
        var viaProvider = await ((IServiceTokenSource)provider.GetRequiredService<IServiceTokenProvider>())
            .GetTokenAsync("identity");

        second.Should().Be(first);
        viaProvider.Should().Be(first);
        endpoint.Requests.Should().Be(1);
    }

    [Fact]
    public async Task The_communication_manager_uses_the_same_cache_as_the_http_client_handler()
    {
        var config = Good();
        var endpoint = new TokenEndpointStub();
        var services = Compose(config, noelia => noelia.UseServiceTokenClient());
        services.AddServiceCommunication(config);
        services.AddHttpClient(nameof(IServiceTokenProvider)).ConfigurePrimaryHttpMessageHandler(() => endpoint);
        using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<IServiceTokenSource>();
        await manager.GetTokenAsync("identity");
        await provider.CreateScope().ServiceProvider.GetRequiredService<IServiceTokenSource>().GetTokenAsync("identity");

        endpoint.Requests.Should().Be(1);
    }

    [Fact]
    public void The_contract_says_what_the_module_provides()
    {
        using var provider = Build(Good());

        var report = provider.GetRequiredService<NoeliaComposition>();

        report.Included.Should().Contain(NoeliaModule.ServiceTokenClient);
    }
}
