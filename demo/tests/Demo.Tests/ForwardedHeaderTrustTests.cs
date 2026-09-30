using System.Net;
using Demo.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Infrastructure.Http;

namespace Demo.Tests;

/// <summary>
/// A forwarded header is believed from the configured hop and from nobody else.
/// </summary>
/// <remarks>
/// The demo used to trust all of RFC 1918, so any container that could reach a
/// service — not just the edge — could choose the client address the service
/// saw, and with it the rate-limit bucket. These tests run the demo's own
/// trust list (<see cref="DemoEnvironment.TrustedProxies"/>) through the same
/// platform middleware the services use, with the peer address set the way the
/// compose network would set it.
/// </remarks>
public sealed class ForwardedHeaderTrustTests
{
    private const string Edge = "172.29.20.2";
    private const string Gateway = "172.29.20.3";

    private static async Task<string> WhoAmI(
        IDictionary<string, string?> settings, string peer, string forwardedFor)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var trusted = DemoEnvironment.Read(configuration).TrustedProxies;

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.TrustForwardedHeadersFrom(trusted))
                .Configure(app =>
                {
                    app.UseForwardedHeaders();
                    app.Run(context => context.Response.WriteAsync(
                        $"{context.Connection.RemoteIpAddress}|{context.Request.Scheme}"));
                }))
            .StartAsync();

        var server = host.GetTestServer();
        var context = await server.SendAsync(http =>
        {
            http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            http.Request.Method = "GET";
            http.Request.Path = "/";
            http.Request.Headers["X-Forwarded-For"] = forwardedFor;
            http.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static Dictionary<string, string?> Configured(params string[] proxies) =>
        proxies.Select((proxy, index) => ($"Demo:TrustedProxies:{index}", (string?)proxy))
            .ToDictionary(pair => pair.Item1, pair => pair.Item2);

    [Fact]
    public async Task A_header_from_the_configured_edge_is_believed()
    {
        var seen = await WhoAmI(Configured(Edge), peer: Edge, forwardedFor: "203.0.113.7");

        seen.Should().Be("203.0.113.7|https");
    }

    [Fact]
    public async Task A_header_from_a_neighbour_in_the_same_private_range_is_ignored()
    {
        // Same /24, same RFC 1918 block the old default trusted, not the edge.
        var seen = await WhoAmI(Configured(Edge), peer: "172.29.20.99", forwardedFor: "203.0.113.7");

        seen.Should().Be("172.29.20.99|http");
    }

    [Fact]
    public async Task A_service_behind_the_gateway_believes_the_gateway_and_the_edge_only()
    {
        var settings = Configured(Edge, Gateway);

        (await WhoAmI(settings, Gateway, "203.0.113.7")).Should().Be("203.0.113.7|https");
        (await WhoAmI(settings, Edge, "203.0.113.7")).Should().Be("203.0.113.7|https");
        (await WhoAmI(settings, "172.29.20.4", "203.0.113.7")).Should().Be("172.29.20.4|http");
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.10")]
    public async Task With_nothing_configured_no_private_range_is_trusted(string peer)
    {
        var seen = await WhoAmI(new Dictionary<string, string?>(), peer, "203.0.113.7");

        seen.Should().Be($"{peer}|http");
    }

    [Fact]
    public void The_default_is_loopback_only()
    {
        var trusted = DemoEnvironment.Read(new ConfigurationBuilder().Build()).TrustedProxies;

        trusted.Should().BeEquivalentTo("127.0.0.1", "::1");
    }
}
