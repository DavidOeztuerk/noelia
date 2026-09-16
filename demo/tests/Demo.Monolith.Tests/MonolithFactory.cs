using Noelia.Infrastructure.Security.Keys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Demo.Monolith.Tests;

public sealed class MonolithFactory(GeneratedKeyPair keys, string databasePath)
    : WebApplicationFactory<Monolith.Api.ServiceEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("JwtSettings:Issuer", "noelia-monolith-tests");
        builder.UseSetting("JwtSettings:Audience", "noelia-monolith-tests");
        builder.UseSetting("Jwt:KeyId", keys.Kid);

        // The key ring is protected under this, in every stage — so a test host
        // needs one too. Generated per run: a fixed key in a test project is a
        // key somebody eventually copies into a deployment.
        builder.UseSetting(
            "Encryption:MasterKey",
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("Jwt:PrivateKey", keys.PrivateKey);
        builder.UseSetting("Jwt:PublicKey", keys.PublicKey);
        builder.UseSetting("Database:Path", databasePath);
        builder.UseSetting("GateCanary:Secret", GateCanary.Value);
    }
}

internal static class GateCanary
{
    internal static string Value { get; } =
        Environment.GetEnvironmentVariable("NOELIA_GATE_CANARY")
        ?? "NOELIA-GATE-CANARY-MONOLITH-FALLBACK";
}
