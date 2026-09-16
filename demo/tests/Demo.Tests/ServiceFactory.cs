using Noelia.Infrastructure.Security.Keys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Demo.Tests;

/// <summary>
/// Hosts one service with the settings a deployment gives it — including the
/// half of the key pair it is entitled to.
/// </summary>
/// <remarks>
/// Values go in through <c>UseSetting</c> rather than
/// <c>ConfigureAppConfiguration</c>. A composition root reads configuration
/// while it is still building, and an app-configuration source added by the
/// test host only appears once the host is built — too late for anything the
/// service needs in order to register.
/// </remarks>
/// <param name="keys">The pair this system runs on.</param>
/// <param name="mayIssue">
/// Whether this service gets the private half. False for every service that
/// only consumes tokens, which is what makes it unable to mint one.
/// </param>
/// <param name="databasePath">
/// Where this service's SQLite file goes. A file, not a server — which is the
/// point of the whole arrangement.
/// </param>
public sealed class ServiceFactory<TEntryPoint>(
    GeneratedKeyPair keys,
    bool mayIssue,
    string? databasePath = null) : WebApplicationFactory<TEntryPoint> where TEntryPoint : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("JwtSettings:Issuer", "noelia-demo-tests");
        builder.UseSetting("JwtSettings:Audience", "noelia-demo-tests");
        builder.UseSetting("Jwt:KeyId", keys.Kid);
        builder.UseSetting("Jwt:PublicKey", keys.PublicKey);
        builder.UseSetting("GateCanary:Secret", GateCanary.Value);
        // The key ring is protected under this, in every stage — so a test host
        // needs one too. Generated per run: a fixed key in a test project is a
        // key somebody eventually copies into a deployment.
        builder.UseSetting("Encryption:MasterKey", TestMasterKey.Value);


        if (mayIssue)
        {
            builder.UseSetting("Jwt:PrivateKey", keys.PrivateKey);
        }

        if (databasePath is not null)
        {
            builder.UseSetting("Database:Path", databasePath);
        }
    }
}

/// <summary>A master key for this test run, and no longer.</summary>
internal static class TestMasterKey
{
    internal static string Value { get; } =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}

internal static class GateCanary
{
    internal static string Value { get; } =
        Environment.GetEnvironmentVariable("NOELIA_GATE_CANARY")
        ?? "NOELIA-GATE-CANARY-DEMO-FALLBACK";
}

/// <summary>A SQLite file that lives as long as the test does.</summary>
public sealed class TemporaryDatabase : IDisposable
{
    public TemporaryDatabase() =>
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"noelia-demo-{Guid.NewGuid():N}.db");

    public string Path { get; }

    public void Dispose()
    {
        foreach (var file in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
