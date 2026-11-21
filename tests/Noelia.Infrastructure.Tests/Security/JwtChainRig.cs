using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Tests.Security.ServiceTokens;
using Noelia.InMemory.Security;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>Where the revocation step stands in a test chain.</summary>
internal enum RevocationStep
{
    /// <summary>After authentication: the way the library composes it.</summary>
    AfterAuth,

    /// <summary>Before authentication: deliberately broken, the step sees no principal.</summary>
    BeforeAuth,

    /// <summary>Not in the chain at all: deliberately broken.</summary>
    Missing
}

/// <summary>
/// A real ASP.NET pipeline over a test server: JwtBearer as the library configures it
/// (<c>AddJwtAuthentication(KeyRing?, authority, …)</c>), the revocation step, and one protected
/// endpoint. The suites that need a chain share it so each states only what it varies.
/// </summary>
internal sealed class JwtChainRig : IAsyncDisposable
{
    public const string Issuer = "https://idp.example.test";
    public const string Audience = "noelia-api";
    public const string Secure = "/secure";

    private readonly IHost _host;

    private JwtChainRig(IHost host)
    {
        _host = host;
        Client = host.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _host.Services;

    public static async Task<JwtChainRig> StartAsync(
        KeyRing? keys,
        string? authority = null,
        RevocationStep revocation = RevocationStep.AfterAuth,
        HttpMessageHandler? backchannel = null,
        Action<JwtBearerOptions>? tamper = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:Issuer"] = Issuer,
            ["JwtSettings:Audience"] = Audience
        }).Build();

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddInMemoryTokenRevocation();
                    services.AddAuthorization();
                    services.AddJwtAuthentication(keys, authority, configuration, new ServiceTokenFixture.Env());

                    // Configure, not PostConfigure: JwtBearer builds its backchannel client in its own
                    // post-configuration, which a later PostConfigure would run behind.
                    if (backchannel is not null)
                    {
                        services.Configure<JwtBearerOptions>(
                            JwtBearerDefaults.AuthenticationScheme,
                            options => options.BackchannelHttpHandler = backchannel);
                    }

                    // PostConfigure so the break lands on what the library set up, never under it.
                    if (tamper is not null)
                    {
                        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, tamper);
                    }
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    if (revocation == RevocationStep.BeforeAuth)
                    {
                        app.UseTokenRevocation();
                    }

                    app.UseAuthentication();
                    app.UseAuthorization();
                    if (revocation == RevocationStep.AfterAuth)
                    {
                        app.UseTokenRevocation();
                    }

                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet(Secure, () => Results.Text("secure")).RequireAuthorization());
                }))
            .StartAsync();

        return new JwtChainRig(host);
    }

    public async Task<HttpStatusCode> GetAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost" + Secure));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var response = await Client.SendAsync(request);
        return response.StatusCode;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>A token signed by hand, so a test can vary exactly one thing about it.</summary>
    public static string Sign(
        SigningCredentials credentials,
        string issuer = Issuer,
        string audience = Audience,
        string subject = "anna")
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
