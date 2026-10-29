using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// The token endpoint: one success shape, one refusal, and nothing in between.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceTokenEndpointTests
{
    private const string Form = "application/x-www-form-urlencoded";

    private static string Body(
        string grant = "client_credentials",
        string id = "notification",
        string secret = KnownVector,
        string audience = "identity") =>
        $"grant_type={grant}&client_id={id}&client_secret={secret}&audience={audience}";

    // ---- success --------------------------------------------------------------

    [Fact]
    public async Task A_valid_request_gets_a_bearer_token_and_must_not_be_cached()
    {
        using var host = await Start();

        var response = await Post(host, Body());
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.ToString().Should().Be("no-cache");
        root.GetProperty("token_type").GetString().Should().Be("Bearer");
        root.GetProperty("expires_in").GetInt32().Should().Be(300);
        root.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo("access_token", "token_type", "expires_in");

        var jwt = new JsonWebToken(root.GetProperty("access_token").GetString());
        jwt.Audiences.Should().Equal("identity");
        jwt.GetPayloadValue<string>("client_id").Should().Be("notification");
    }

    [Fact]
    public async Task A_charset_parameter_on_the_content_type_is_allowed()
    {
        using var host = await Start();

        var response = await Post(host, Body(), $"{Form}; charset=utf-8");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_media_type_is_compared_without_regard_to_case()
    {
        using var host = await Start();

        var response = await Post(host, Body(), "Application/X-WWW-Form-Urlencoded");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_fields_are_ignored()
    {
        using var host = await Start();

        var response = await Post(host, Body() + "&scope=everything&extra=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_pattern_is_the_callers_choice()
    {
        using var host = await Start(pattern: "/oauth/service-token");

        (await Post(host, Body(), path: "/oauth/service-token")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post(host, Body(), path: "/m2m/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_endpoint_answers_post_only()
    {
        using var host = await Start();

        var response = await host.GetTestClient().GetAsync(new Uri("http://localhost/m2m/token"));

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task The_endpoint_needs_no_authentication_to_be_reached()
    {
        // A caller asking for a token has none yet; if anything in the chain demanded
        // one, the endpoint would answer 401 to a correct request.
        using var host = await Start(fullChain: true);

        var response = await Post(host, Body());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- refusal: one answer, byte for byte -------------------------------------

    public static TheoryData<string, string> Refusals() => new()
    {
        { "unknown id", Body(id: "nobody") },
        { "wrong secret", Body(secret: OtherVector) },
        { "unknown audience", Body(audience: "elsewhere") },
        { "the person audience", Body(audience: "persons") },
        { "secret too short", Body(secret: KnownVector[..42]) },
        { "secret too long", Body(secret: KnownVector + "A") },
        { "secret not canonical", Body(secret: KnownVector[..42] + "x") },
        { "secret with plus", Body(secret: KnownVector[..42].Replace('-', '+') + "w") },
        { "secret absent", "grant_type=client_credentials&client_id=notification&audience=identity" },
        { "wrong grant", Body(grant: "password") },
        { "grant absent", $"client_id=notification&client_secret={KnownVector}&audience=identity" },
        { "audience absent", $"grant_type=client_credentials&client_id=notification&client_secret={KnownVector}" },
        { "id absent", $"grant_type=client_credentials&client_secret={KnownVector}&audience=identity" },
        { "empty body", "" },
        { "double secret", Body() + $"&client_secret={KnownVector}" },
        { "double secret, one wrong", Body() + $"&client_secret={OtherVector}" },
        { "double id", Body() + "&client_id=advisor" },
        { "double audience", Body() + "&audience=billing" },
        { "double grant", Body() + "&grant_type=client_credentials" },
        { "id too long", Body(id: new string('a', 49)) },
        { "audience too long", Body(audience: new string('a', 49)) },
        { "garbled escapes", "%%%=%&&&===" },
        { "not a form at all", "{\"client_id\":\"notification\"}" },
        { "over four kibibytes", Body() + "&padding=" + new string('x', 5000) },
        { "exactly over the limit", Body() + "&p=" + new string('x', 4097 - Body().Length - 3) }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Every_refusal_is_the_same_401(string why, string body)
    {
        using var host = await Start();
        var expected = await Snapshot(await Post(host, Body(id: "nobody")));

        var actual = await Snapshot(await Post(host, body));

        actual.Should().Be(expected, why);
        actual.Status.Should().Be(HttpStatusCode.Unauthorized);
        actual.Text.Should().Be("{\"error\":\"invalid_client\"}");
        actual.ContentType.Should().Be("application/json");
        actual.CacheControl.Should().Contain("no-store");
        actual.Pragma.Should().Be("no-cache");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded-extra")]
    [InlineData("")]
    public async Task A_wrong_content_type_is_the_same_401(string contentType)
    {
        using var host = await Start();
        var expected = await Snapshot(await Post(host, Body(id: "nobody")));

        var actual = await Snapshot(await Post(host, Body(), contentType));

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task A_body_that_is_not_utf8_is_the_same_401()
    {
        using var host = await Start();
        var expected = await Snapshot(await Post(host, Body(id: "nobody")));
        using var content = new ByteArrayContent([0xFF, 0xFE, 0x00, 0xC3, 0x28]);
        content.Headers.ContentType = new MediaTypeHeaderValue(Form);

        var actual = await Snapshot(await host.GetTestClient().PostAsync(new Uri("http://localhost/m2m/token"), content));

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task A_body_at_exactly_four_kibibytes_is_still_read()
    {
        using var host = await Start();
        var padding = 4096 - Body().Length - "&p=".Length;

        var response = await Post(host, Body() + "&p=" + new string('x', padding));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>The unlimited stream is the attack; no more than the limit may be taken from it.</summary>
    [Fact]
    public async Task No_more_than_the_limit_is_read_from_the_stream()
    {
        using var host = await Start();
        var counting = new CountingStream(Encoding.ASCII.GetBytes(Body() + "&p=" + new string('x', 1_000_000)));
        using var content = new StreamContent(counting);
        content.Headers.ContentType = new MediaTypeHeaderValue(Form);

        using var response = await host.GetTestClient().PostAsync(new Uri("http://localhost/m2m/token"), content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        counting.BytesRead.Should().BeLessThan(200_000, "the request body is not drained to the end");
    }

    // ---- mapping ---------------------------------------------------------------

    [Fact]
    public void Mapping_without_an_issuer_registered_throws_at_the_mapping()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        var map = () => app.MapServiceTokenEndpoint();

        map.Should().Throw<InvalidOperationException>().WithMessage("*UseServiceTokenIssuer*");
    }

    /// <summary>
    /// RequireRateLimiting and its siblings hang off the returned builder, so it
    /// has to be the endpoint's own: a host constraint added to it must bite.
    /// </summary>
    [Fact]
    public async Task The_returned_builder_is_the_endpoints_own()
    {
        using var host = await Start(decorate: route => route.RequireHost("elsewhere.test"));

        var response = await Post(host, Body());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- the whole chain -------------------------------------------------------

    /// <summary>
    /// Input sanitisation, security headers, rate limiting, auth and audit all
    /// stand in front of the endpoint in a real service, and the secret alphabet
    /// has to pass through all of them unchanged.
    /// </summary>
    [Fact]
    public async Task Through_the_full_Noelia_chain_the_secret_arrives_unchanged_and_the_answer_stays_the_same()
    {
        using var host = await Start(fullChain: true);

        var ok = await Post(host, Body());
        var refusedKnown = await Snapshot(await Post(host, Body(secret: OtherVector)));
        var refusedUnknown = await Snapshot(await Post(host, Body(id: "nobody")));

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        refusedKnown.Should().Be(refusedUnknown);
        refusedKnown.Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_secret_with_every_character_class_survives_the_full_chain()
    {
        // base64url's whole alphabet, including '-' and '_' and a leading digit.
        var secret = ServiceClientSecret.Generate("notification");
        while (!(secret.Secret.Contains('-') && secret.Secret.Contains('_')))
        {
            secret = ServiceClientSecret.Generate("notification");
        }

        using var host = await Start(fullChain: true, hash: secret.Hash);

        var response = await Post(host, Body(secret: secret.Secret));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- plumbing --------------------------------------------------------------

    private static async Task<IHost> Start(
        string pattern = "/m2m/token",
        bool fullChain = false,
        string? hash = null,
        Func<RouteHandlerBuilder, RouteHandlerBuilder>? decorate = null)
    {
        var config = StandardConfig(("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH", hash ?? KnownHash));

        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddNoelia(config, context.HostingEnvironment, "identity", noelia =>
                    {
                        if (fullChain)
                        {
                            noelia.UseDefaults();
                        }

                        Standard(noelia, config);
                    });
                })
                .Configure((context, app) =>
                {
                    app.UseRouting();
                    if (fullChain)
                    {
                        app.UseNoelia(context.HostingEnvironment, "identity");
                    }

                    app.UseEndpoints(endpoints =>
                    {
                        var route = endpoints.MapServiceTokenEndpoint(pattern);
                        decorate?.Invoke(route);
                    });
                }))
            .StartAsync();
    }

    private static async Task<HttpResponseMessage> Post(
        IHost host, string body, string contentType = Form, string path = "/m2m/token")
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (contentType.Length > 0)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return await host.GetTestClient().PostAsync(new Uri("http://localhost" + path), content);
    }

    private static async Task<Answer> Snapshot(HttpResponseMessage response)
    {
        using (response)
        {
            return new Answer(
                response.StatusCode,
                Convert.ToHexString(await response.Content.ReadAsByteArrayAsync()),
                Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()),
                response.Content.Headers.ContentType?.ToString() ?? "",
                response.Headers.CacheControl?.ToString() ?? "",
                string.Join(",", response.Headers.Pragma.Select(p => p.ToString())));
        }
    }

    private sealed record Answer(
        HttpStatusCode Status, string Bytes, string Text, string ContentType, string CacheControl, string Pragma);

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += n;
            return n;
        }
    }
}
