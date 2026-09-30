using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Infrastructure.Sovereignty;

namespace Noelia.Infrastructure.Tests.Sovereignty;

[Trait("Category", "Integration")]
public sealed class EgressRedirectTests
{
    [Theory]
    [InlineData("loop")]
    [InlineData("scheme")]
    [InlineData("allowed-port")]
    public async Task Even_allowed_origin_redirects_and_loops_require_an_explicit_new_request(string kind)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var uri = $"http://127.0.0.1:{port}/redirect";
        var location = kind switch
        {
            "scheme" => $"https://127.0.0.1:{port}/redirect",
            "allowed-port" => "http://127.0.0.1:9/redirect",
            _ => uri
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Respond(server, 307, location, timeout.Token);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddNoeliaEgressPolicy(policy => policy.Allow("127.0.0.1"));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient();
        using var response = await client.GetAsync(uri, timeout.Token);
        await serve;
        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        server.Pending().Should().BeFalse();
    }

    [Fact]
    public async Task An_allowed_direct_request_still_reaches_the_transport()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Respond(server, 200, "", timeout.Token);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddNoeliaEgressPolicy(policy => policy.Allow("127.0.0.1"));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/", timeout.Token);
        await serve;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void An_unknown_primary_transport_is_refused_under_an_enforcing_policy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNoeliaEgressPolicy(policy => policy.Allow("allowed.example"));
        services.AddHttpClient("custom").ConfigurePrimaryHttpMessageHandler(() => new CustomTransport());
        using var provider = services.BuildServiceProvider();
        var create = () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("custom");
        create.Should().Throw<InvalidOperationException>().WithMessage("*primary transport*");
    }

    [Fact]
    public async Task An_unrestricted_policy_preserves_custom_transport_behavior()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNoeliaEgressPolicy(_ => { });
        services.AddHttpClient("custom").ConfigurePrimaryHttpMessageHandler(() => new CustomTransport());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("custom");
        using var response = await client.GetAsync("http://example.test/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static IEnumerable<object[]> Cases =>
        from status in new[] { 301, 302, 303, 307, 308 }
        from sockets in new[] { true, false }
        from policyFirst in new[] { true, false }
        select new object[] { status, sockets, policyFirst };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Automatic_redirects_cannot_escape_the_guard(
        int status, bool sockets, bool policyFirst)
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        destination.Start();
        var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Respond(source, status, $"http://localhost:{targetPort}/forbidden", timeout.Token);
        var hits = 0;
        var targetServe = Respond(destination, 200, "", timeout.Token, () => Interlocked.Increment(ref hits));
        var services = new ServiceCollection();
        services.AddLogging();
        if (policyFirst) services.AddNoeliaEgressPolicy(policy => policy.Allow("127.0.0.1"));
        services.AddHttpClient("guarded").ConfigurePrimaryHttpMessageHandler(() => sockets
            ? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = true }
            : new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true });
        if (!policyFirst) services.AddNoeliaEgressPolicy(policy => policy.Allow("127.0.0.1"));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("guarded");

        try
        {
            using var response = await client.PostAsync($"http://127.0.0.1:{sourcePort}/redirect",
                new StringContent("synthetic-private-body"), timeout.Token);
            await serve;

            ((int)response.StatusCode).Should().Be(status, "redirects must be returned for an explicit policy-checked decision");
            Volatile.Read(ref hits).Should().Be(0, "the forbidden destination must receive no connection or body");
        }
        finally
        {
            await timeout.CancelAsync();
            try { await targetServe; }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task Respond(TcpListener server, int status, string location, CancellationToken token,
        Action? accepted = null)
    {
        using var connection = await server.AcceptTcpClientAsync(token);
        accepted?.Invoke();
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        _ = await reader.ReadLineAsync(token);
        var length = 0;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token)))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[15..], System.Globalization.CultureInfo.InvariantCulture);
        if (length > 0) await reader.ReadBlockAsync(new char[length], token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} Redirect\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
    }

    private sealed class CustomTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
