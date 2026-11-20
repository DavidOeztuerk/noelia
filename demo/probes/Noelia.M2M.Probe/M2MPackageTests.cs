using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Noelia.Abstractions.Security.Checks;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;
using System.Net;
using System.Text.Json;

namespace Noelia.M2M.Probe;

/// <summary>
/// Service-to-service rights, exercised through the packed Noelia.Infrastructure and
/// nothing else: an issuer signs a token over HTTP, a receiver with one door lets the
/// named caller in and answers everyone else with the 404 of a door that is not there.
/// </summary>
/// <remarks>
/// The library's own tests compile against the working tree. This is the only place the
/// feature is exercised as a consumer gets it, which is why it exists (issue 56).
/// </remarks>
public sealed class M2MPackageTests : IAsyncLifetime
{
    private const string Issuer = "https://issuer.probe";
    private const string Receiver = "receiver";

    private static readonly GeneratedKeyPair Keys = SigningKey.GenerateKeyPair("m2m-probe");

    private readonly ServiceClientSecret _allowed = ServiceClientSecret.Generate("allowed-caller");
    private readonly ServiceClientSecret _stranger = ServiceClientSecret.Generate("stranger");

    private WebApplication _issuer = null!;
    private WebApplication _receiver = null!;

    public async Task InitializeAsync()
    {
        _issuer = await StartIssuerAsync();
        _receiver = await StartReceiverAsync();
    }

    public async Task DisposeAsync()
    {
        await _issuer.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    [Fact]
    public async Task The_named_caller_opens_the_door_with_a_token_the_issuer_signed()
    {
        var token = await TokenAsync(_allowed);

        using var response = await CallAsync("/internal/account/42", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("account 42");
    }

    [Fact]
    public async Task A_service_that_is_not_on_the_list_gets_the_same_404_as_a_path_that_does_not_exist()
    {
        var token = await TokenAsync(_stranger);

        using var refused = await CallAsync("/internal/account/42", token);
        using var missing = await CallAsync("/internal/no-such-door", token);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        (await missing.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task No_token_at_all_is_the_same_404()
    {
        using var response = await CallAsync("/internal/account/42", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_persons_token_does_not_open_a_service_door()
    {
        // Signed by the very key the receiver verifies with, and a valid person's token
        // anywhere else; a door for services still refuses it, as a 404.
        using var response = await CallAsync("/internal/account/42", PersonToken());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public void The_report_after_start_says_Pass_for_the_receiver_with_its_door()
    {
        var result = _receiver.Services.GetRequiredService<ISecurityCheckReport>().Latest
            .Single(check => check.Id == "noelia.m2m.caller-policy");

        result.Status.Should().Be(SecurityCheckStatus.Pass, result.Summary);
        result.Summary.Should().Contain("1 service door");
    }

    [Fact]
    public void The_report_after_start_says_Pass_for_the_issuer_with_its_rate_limited_token_endpoint()
    {
        var result = _issuer.Services.GetRequiredService<ISecurityCheckReport>().Latest
            .Single(check => check.Id == "noelia.m2m.caller-policy");

        result.Status.Should().Be(SecurityCheckStatus.Pass, result.Summary);
        result.Summary.Should().Contain("token endpoint");
    }

    private async Task<string> TokenAsync(ServiceClientSecret client)
    {
        using var response = await _issuer.GetTestClient().PostAsync("/m2m/token", new FormUrlEncodedContent(
        [
            new("grant_type", "client_credentials"),
            new("client_id", client.ClientId),
            new("client_secret", client.Secret),
            new("audience", Receiver)
        ]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> CallAsync(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return _receiver.GetTestClient().SendAsync(request);
    }

    private static string PersonToken()
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "persons",
            NotBefore = now,
            IssuedAt = now,
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "person-1" },
            SigningCredentials = SigningKey.FromEcdsaPrivateKey(Keys.PrivateKey, Keys.Kid).SigningCredentials()
        });
    }

    private async Task<WebApplication> StartIssuerAsync()
    {
        var builder = Host();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NOELIA_M2M_CLIENT_ALLOWED_CALLER_SECRET_HASH"] = _allowed.Hash,
            ["NOELIA_M2M_CLIENT_STRANGER_SECRET_HASH"] = _stranger.Hash
        });
        builder.Services.AddNoelia(builder.Configuration, builder.Environment, "issuer-probe", noelia => noelia
            .UseJwt(jwt => jwt.Issue(Keys.PrivateKey, Keys.Kid))
            .UseServiceTokenIssuer(issuer => issuer
                .Clients(builder.Configuration, "allowed-caller", "stranger")
                .Audiences(Receiver)));
        builder.Services.AddRateLimiter(limiter => limiter.AddFixedWindowLimiter("m2m-token", options =>
        {
            options.PermitLimit = 20;
            options.Window = TimeSpan.FromMinutes(1);
        }));

        var app = builder.Build();
        app.UseRateLimiter();
        app.MapServiceTokenEndpoint("/m2m/token").RequireRateLimiting("m2m-token");
        await app.StartAsync();
        return app;
    }

    private async Task<WebApplication> StartReceiverAsync()
    {
        var builder = Host();
        builder.Services.AddNoelia(builder.Configuration, builder.Environment, "receiver-probe", noelia => noelia
            .UseJwt(jwt => jwt.VerifyOnly(Keys.PublicKey, Keys.Kid))
            .UseServiceCallers(Receiver));

        var app = builder.Build();
        app.UseNoelia(builder.Environment, "receiver-probe");
        MapDoors(app);
        await app.StartAsync();
        return app;
    }

    private static void MapDoors(WebApplication app)
    {
        // The fallback is what makes a path with no door answer the same 404 as a refused one.
        var doors = app.MapGroup("/internal");
        doors.MapGet("/account/{id}", (string id) => Results.Text("account " + id))
            .RequireServiceCaller("allowed-caller");
        doors.MapServiceFallback();
    }

    private static WebApplicationBuilder Host()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(M2MPackageTests).Assembly.GetName().Name,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:Issuer"] = Issuer,
            ["JwtSettings:Audience"] = "persons"
        });
        return builder;
    }
}
