using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noelia.Infrastructure.Communication.Configuration;
using Noelia.Infrastructure.Security.M2M;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Tests.Security.M2M;

/// <summary>What an unconfigured service still speaks.</summary>
[Trait("Category", "Unit")]
public class ServiceTokenProviderLegacyWireTests
{
    [Fact]
    public void Protocol_is_legacy_and_there_is_no_default_audience_until_asked()
    {
        var config = new M2MConfiguration();

        config.Protocol.Should().Be(ServiceTokenProtocol.Legacy);
        config.Audience.Should().BeNull();
    }

    [Fact]
    public async Task Legacy_sends_the_json_body_and_not_a_form()
    {
        var secret = ServiceClientSecret.Generate("advisor").Secret;
        var endpoint = new TokenEndpointStub { RawBody = "{\"data\":{\"accessToken\":\"legacy-token\",\"expiresIn\":600}}" };
        var provider = new ServiceTokenProvider(
            new HttpClient(endpoint),
            NullLogger<ServiceTokenProvider>.Instance,
            Options.Create(new ServiceCommunicationOptions
            {
                M2M = new M2MConfiguration
                {
                    Enabled = true, TokenEndpoint = "http://identity.test/api/auth/service-token",
                    ClientId = "Advisor", ClientSecret = secret, EnableTokenCaching = false
                }
            }));

        var token = await provider.GetTokenAsync();

        token.Should().Be("legacy-token");
        var (contentType, _) = endpoint.Seen.Single();
        contentType.Should().Be("application/json");
        endpoint.Bodies.Single().Should().Contain("\"serviceName\":\"Advisor\"").And.Contain("\"servicePassword\"");
        endpoint.Bodies.Single().Should().NotContain("grant_type");
    }
}
