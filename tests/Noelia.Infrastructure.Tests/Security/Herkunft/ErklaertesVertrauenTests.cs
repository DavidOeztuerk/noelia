using System.Net;
using Microsoft.Extensions.FileProviders;
using Noelia.Infrastructure.Builder.Modules;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using IPNetwork = System.Net.IPNetwork;
using Noelia.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Noelia.Infrastructure.Tests.Security.Herkunft;

/// <summary>
/// The other half: a proxy that was named really is believed.
/// </summary>
/// <remarks>
/// Refusing every forwarded header would satisfy the security tests and leave
/// every deployment behind a load balancer counting one caller. The door has to
/// open for whoever was let in by name.
/// </remarks>
[Trait("Category", "Unit")]
public class ErklaertesVertrauenTests
{
    [Fact]
    public void Ohne_Erklaerung_steht_kein_Vertrauen_im_Behaelter()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        services.GetService<ForwardedHeaderTrust>().Should().BeNull();
    }

    /// <summary>
    /// An empty list reads like trust and grants none — the worst of both, and
    /// the kind of call that survives review.
    /// </summary>
    [Fact]
    public void Eine_leere_Liste_ist_keine_Erklaerung()
    {
        var services = new ServiceCollection();

        var leer = () => services.TrustForwardedHeadersFrom([]);

        leer.Should().Throw<ArgumentException>().WithMessage("*at least one proxy*");
    }

    /// <summary>
    /// The platform trusts loopback out of the box. A declaration replaces that
    /// rather than adding to it.
    /// </summary>
    [Fact]
    public void Die_Loopback_Vorgabe_des_Rahmenwerks_wird_ersetzt()
    {
        var options = Configured("10.0.0.7", "192.168.0.0/16");

        options.KnownProxies.Should().ContainSingle()
            .Which.Should().Be(IPAddress.Parse("10.0.0.7"));
        options.KnownIPNetworks.Should().ContainSingle()
            .Which.Should().Be(new IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
    }

    [Fact]
    public void Etwas_das_weder_Adresse_noch_Netz_ist_wird_abgelehnt()
    {
        var services = new ServiceCollection();

        var unsinn = () => services.TrustForwardedHeadersFrom(["nicht-eine-adresse"]);

        unsinn.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// End to end, through the platform's own middleware: a request arriving
    /// from a named proxy is attributed to the address that proxy forwards.
    /// </summary>
    [Fact]
    public async Task Ein_erklaerter_Vermittler_wird_geglaubt()
    {
        var gesehen = await AddressSeenAsync(
            trusted: ["127.0.0.1"], forwardedFor: "203.0.113.9");

        gesehen.Should().Be("203.0.113.9");
    }

    /// <summary>
    /// And the same request without the declaration is attributed to the
    /// connection, which is all anyone can prove about it.
    /// </summary>
    [Fact]
    public async Task Ohne_Erklaerung_zaehlt_die_Verbindung()
    {
        var gesehen = await AddressSeenAsync(
            trusted: null, forwardedFor: "203.0.113.9");

        gesehen.Should().Be("127.0.0.1");
    }

    /// <summary>
    /// A proxy nobody named is a caller like any other, header or not.
    /// </summary>
    [Fact]
    public async Task Ein_nicht_erklaerter_Absender_wird_nicht_geglaubt()
    {
        var gesehen = await AddressSeenAsync(
            trusted: ["10.99.99.99"], forwardedFor: "203.0.113.9");

        gesehen.Should().Be("127.0.0.1");
    }

    /// <summary>
    /// Durch Noelias eigene Kette, nicht durch eine selbst gebaute.
    /// </summary>
    /// <remarks>
    /// Die Proben darüber rufen <c>app.UseForwardedHeaders()</c> selbst auf und
    /// belegen damit nur, dass die Optionen stimmen. Bis 5.1.0 stellte
    /// <c>UseNoelia(...)</c> diesen Schritt nicht ein: Die Erklärung war im
    /// Behälter, angewandt hat sie niemand, und jeder Dienst hinter einem
    /// Vermittler zählte weiterhin den Vermittler.
    /// </remarks>
    [Fact]
    public async Task Die_Noelia_Kette_wendet_die_Erklaerung_selbst_an()
    {
        var gesehen = await AddressSeenThroughNoeliaAsync(
            trusted: ["127.0.0.1"], forwardedFor: "203.0.113.9");

        gesehen.Should().Be("203.0.113.9");
    }

    /// <summary>
    /// Und ohne Erklärung stellt dieselbe Kette den Schritt nicht ein.
    /// </summary>
    [Fact]
    public async Task Ohne_Erklaerung_stellt_die_Kette_den_Schritt_nicht_ein()
    {
        var gesehen = await AddressSeenThroughNoeliaAsync(
            trusted: null, forwardedFor: "203.0.113.9");

        gesehen.Should().Be("127.0.0.1");
    }

    /// <summary>
    /// Auch das Schema kommt vom Vermittler — daran hängt, ob ein Cookie, das
    /// <c>Secure</c> verlangt, es auch bekommt.
    /// </summary>
    [Fact]
    public async Task Das_erklaerte_Schema_erreicht_die_Anwendung()
    {
        using var host = await NoeliaHostAsync(
            trusted: ["127.0.0.1"],
            report: context => context.Request.Scheme);

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var schema = await (await client.GetAsync(new Uri("http://localhost/")))
            .Content.ReadAsStringAsync();

        schema.Should().Be("https");
    }

    private static async Task<string> AddressSeenThroughNoeliaAsync(
        string[]? trusted,
        string forwardedFor)
    {
        using var host = await NoeliaHostAsync(trusted, ClientAddress.Of);

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);

        return await (await client.GetAsync(new Uri("http://localhost/")))
            .Content.ReadAsStringAsync();
    }

    private static Task<IHost> NoeliaHostAsync(
        string[]? trusted,
        Func<HttpContext, string> report) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddNoelia(
                        new ConfigurationBuilder().Build(),
                        Produktion,
                        "forwarded-probe",
                        _ => { });

                    if (trusted is not null)
                    {
                        services.TrustForwardedHeadersFrom(trusted);
                    }
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Loopback;
                        await next();
                    });

                    app.UseNoelia(
                        Produktion,
                        "forwarded-probe",
                        mw => mw.UseForwardedHeaders());

                    app.Run(context => context.Response.WriteAsync(report(context)));
                }))
            .StartAsync();

    private static readonly IHostEnvironment Produktion = new Umgebung();

    private sealed class Umgebung : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "forwarded-probe";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static ForwardedHeadersOptions Configured(params string[] proxies)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.TrustForwardedHeadersFrom(proxies);

        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }

    /// <summary>Runs one request and reports what <see cref="ClientAddress"/> made of it.</summary>
    private static async Task<string> AddressSeenAsync(string[]? trusted, string forwardedFor)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    if (trusted is not null)
                    {
                        services.TrustForwardedHeadersFrom(trusted);
                    }
                })
                .Configure(app =>
                {
                    // The test server opens no socket, so nothing sets the peer
                    // address — and that address is the whole question here.
                    app.Use(async (context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Loopback;
                        await next();
                    });
                    app.UseForwardedHeaders();
                    app.Run(context => context.Response.WriteAsync(ClientAddress.Of(context)));
                }))
            .StartAsync();

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);

        return await (await client.GetAsync(new Uri("http://localhost/"))).Content.ReadAsStringAsync();
    }
}
