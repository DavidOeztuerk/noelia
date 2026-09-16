using System.Net;
using Microsoft.AspNetCore.Http;
using Noelia.Infrastructure.Http;

namespace Noelia.Infrastructure.Tests.Security.Herkunft;

/// <summary>
/// Ein Dienst, der selbst weiterleitet, beendet die Kette nicht.
/// </summary>
/// <remarks>
/// Gefunden am laufenden Stack: Im Monolithen trug der Refresh-Token-Cookie
/// <c>Secure</c>, hinter dem Gateway nicht. <c>UseForwardedHeaders</c>
/// verbraucht die Kopfzeilen des Edge — richtig so —, und das Gateway rief den
/// Dienst dahinter ohne sie auf. Der Dienst sah <c>http</c> und gab das Cookie
/// ehrlich ohne <c>Secure</c> aus.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class WeitergereichterUrsprungTests
{
    [Fact]
    public async Task Das_Schema_des_Aufrufers_geht_an_den_naechsten_Sprung()
    {
        var gesendet = await SendenAus(Schema: "https", Adresse: IPAddress.Parse("203.0.113.9"));

        gesendet.Should().Contain(("X-Forwarded-Proto", "https"));
        gesendet.Should().Contain(("X-Forwarded-For", "203.0.113.9"));
    }

    [Fact]
    public async Task Was_der_Aufrufer_selbst_gesetzt_hat_bleibt_stehen()
    {
        var gesendet = await SendenAus(
            Schema: "https",
            Adresse: IPAddress.Loopback,
            vorbelegt: ("X-Forwarded-Proto", "http"));

        gesendet.Should().Contain(("X-Forwarded-Proto", "http"));
        gesendet.Count(h => h.Name == "X-Forwarded-Proto").Should().Be(1);
    }

    /// <summary>
    /// Ein Hintergrundaufruf gehört zu keinem Aufrufer — einen Ursprung zu
    /// erfinden hieße, die eigene Adresse als fremde auszugeben.
    /// </summary>
    [Fact]
    public async Task Ohne_Anfrage_wird_nichts_behauptet()
    {
        var gesendet = await SendenAus(Schema: null, Adresse: null);

        gesendet.Should().BeEmpty();
    }

    private static async Task<IReadOnlyList<(string Name, string Wert)>> SendenAus(
        string? Schema,
        IPAddress? Adresse,
        (string Name, string Wert)? vorbelegt = null)
    {
        var accessor = new HttpContextAccessor();

        if (Schema is not null)
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = Schema;
            context.Request.Host = new HostString("gateway.example");
            context.Connection.RemoteIpAddress = Adresse;
            accessor.HttpContext = context;
        }

        var aufzeichnung = new Aufzeichnung();
        var handler = new ForwardedOriginHandler(accessor) { InnerHandler = aufzeichnung };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://downstream.invalid/");

        if (vorbelegt is { } vor)
        {
            request.Headers.TryAddWithoutValidation(vor.Name, vor.Wert);
        }

        await client.SendAsync(request);

        return aufzeichnung.Gesehen;
    }

    private sealed class Aufzeichnung : HttpMessageHandler
    {
        public List<(string Name, string Wert)> Gesehen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers.Where(h => h.Key.StartsWith("X-Forwarded-")))
            {
                Gesehen.Add((header.Key, string.Join(",", header.Value)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
