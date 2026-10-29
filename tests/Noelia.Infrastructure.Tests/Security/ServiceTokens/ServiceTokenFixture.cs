using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// One place that knows how to stand up an issuing service in a test, so each
/// test states only what it varies.
/// </summary>
internal static class ServiceTokenFixture
{
    public const string Issuer = "https://identity.test";
    public const string PersonAudience = "persons";

    /// <summary>
    /// A value from the ADR shell recipe and the hash that recipe prints for it. Both are
    /// throwaway test vectors generated for this file; they protect nothing.
    /// </summary>
    public const string KnownVector = "vTFQKHO5s-d6tcHO3FIL9VsySJbH4BtwgcszEdEnybw";
    public const string KnownHash = "sha256:rWpn3cMgvuFTUlyssEse2ZRB29ydKiG75UV2pFxISQU";

    public const string OtherVector = "UwML9_1YhWhQSsggG1_xl8sXZIu_Y54wctD5M0uX8vg";
    public const string OtherHash = "sha256:60DzB4MF4xu8p3fpei6ICmgPD-VUhI8jPLHvkDIwpNA";

    public static GeneratedKeyPair KeyPair { get; } = SigningKey.GenerateKeyPair("m2m-test");

    public static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var values = new Dictionary<string, string?>
        {
            ["JwtSettings:Issuer"] = Issuer,
            ["JwtSettings:Audience"] = PersonAudience
        };

        foreach (var (key, value) in entries)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public static IServiceCollection Compose(IConfiguration configuration, Action<NoeliaBuilder> noelia)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNoelia(configuration, new Env(), "identity", noelia);
        return services;
    }

    /// <summary>The usual issuing service: asymmetric ring, two clients, two audiences.</summary>
    public static NoeliaBuilder Standard(NoeliaBuilder noelia, IConfiguration configuration) => noelia
        .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
        .UseServiceTokenIssuer(issuer => issuer
            .Clients(configuration, "notification", "advisor")
            .Audiences("identity", "billing"));

    public static IConfiguration StandardConfig(params (string Key, string Value)[] more) =>
        Config([
            ("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH", KnownHash),
            ("NOELIA_M2M_CLIENT_ADVISOR_SECRET_HASH", OtherHash),
            .. more]);

    internal sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "identity";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
