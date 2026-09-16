using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Noelia.Dashboard;

namespace Demo.Platform;

/// <summary>
/// What an environment is allowed to show, read from configuration rather than
/// decided in code.
/// </summary>
/// <remarks>
/// Every service used to carry its own copy of
/// <c>IsDevelopment() &amp;&amp; IsLoopback(remote)</c>. That predicate is
/// wrong twice over behind a reverse proxy — the remote address is the proxy's
/// — and it forces a rebuild to change a decision that belongs to whoever runs
/// the stage. A customer landscape of <c>dev / int / cons / prod</c> should be
/// four configuration files, not four branches.
/// </remarks>
public enum DashboardVisibility
{
    /// <summary>The route stays an indistinguishable 404 for everyone.</summary>
    None,

    /// <summary>Anyone who can reach the port. Development only.</summary>
    Open,

    /// <summary>A caller proving they are an operator.</summary>
    Operator
}

/// <summary>Reads the demo's per-environment decisions out of configuration.</summary>
public sealed class DemoEnvironment
{
    private const string OperatorHeader = "X-Noelia-Operator";

    /// <summary>SHA-256 of the configured secret, never the secret itself.</summary>
    private readonly byte[]? _operatorSecret;

    private DemoEnvironment(
        DashboardVisibility visibility,
        string? productionReason,
        string? redisConnectionString,
        byte[]? operatorSecret)
    {
        Visibility = visibility;
        ProductionReason = productionReason;
        RedisConnectionString = redisConnectionString;
        _operatorSecret = operatorSecret;
    }

    /// <summary>Who may see the operator dashboard here.</summary>
    public DashboardVisibility Visibility { get; }

    /// <summary>Why the dashboard exists in Production, when it does.</summary>
    public string? ProductionReason { get; }

    /// <summary>The RESP server this stage uses, or <c>null</c> for in-process state.</summary>
    public string? RedisConnectionString { get; }

    /// <summary>
    /// The networks whose forwarded headers this service believes.
    /// </summary>
    /// <remarks>
    /// No service in the demo publishes a port: the only thing that can open a
    /// connection to one is the compose network, and the only thing on it that
    /// forwards is the edge. Trusting the private ranges is therefore trusting
    /// the edge — but it is written down rather than assumed, because the day a
    /// port is published the sentence stops being true and someone has to see
    /// it.
    /// </remarks>
    public string[] TrustedProxies { get; private set; } =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    /// <summary>True when this stage keeps its state in Redis rather than in the process.</summary>
    public bool UsesRedis => !string.IsNullOrWhiteSpace(RedisConnectionString);

    /// <summary>Reads the <c>Demo</c> section.</summary>
    public static DemoEnvironment Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Demo");
        var visibility = Enum.TryParse<DashboardVisibility>(
            section["Dashboard:Visibility"], ignoreCase: true, out var parsed)
            ? parsed
            : DashboardVisibility.None;

        var secret = section["Dashboard:OperatorSecret"];

        if (visibility == DashboardVisibility.Operator && string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "Demo:Dashboard:Visibility is 'Operator' but Demo:Dashboard:OperatorSecret is "
                + "empty. A policy that cannot recognise an operator is a policy that admits "
                + "everyone or no one, and which of the two is an accident.");
        }

        var configured = section.GetSection("TrustedProxies").Get<string[]>();

        return new DemoEnvironment(
            visibility,
            section["Dashboard:ProductionReason"],
            section["Providers:Redis"],
            secret is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(secret)))
        {
            TrustedProxies = configured is { Length: > 0 }
                ? configured
                : ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]
        };
    }

    /// <summary>
    /// True when this stage runs the dashboard at all.
    /// </summary>
    /// <remarks>
    /// The first of the two levers. A stage answering <see cref="DashboardVisibility.None"/>
    /// does not compose the module — the page is not in the process, and the
    /// composition report says so — rather than composing it behind a policy
    /// that refuses everyone. The difference is visible to an operator and
    /// matters: one is "we decided not to run it here", the other is "we ran it
    /// and hope the predicate is right".
    /// </remarks>
    public bool DashboardEnabled => Visibility != DashboardVisibility.None;

    /// <summary>
    /// Applies this stage's answer to the dashboard builder.
    /// </summary>
    /// <remarks>
    /// <see cref="DashboardVisibility.Operator"/> is proved here with a shared
    /// secret in a header, because one of the four hosts — the gateway — holds
    /// no key and verifies no token by design, and an operator surface that
    /// exists on three of four services would teach the wrong lesson. Where a
    /// principal *is* available the same call takes
    /// <c>context.User.HasClaim("permission", "noelia:dashboard")</c> instead,
    /// and that is the better policy for a real system: it names who looked.
    /// </remarks>
    internal void Apply(NoeliaDashboardBuilder dashboard, IHostEnvironment environment)
    {
        if (Visibility == DashboardVisibility.Open)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"Demo:Dashboard:Visibility is 'Open' in {environment.EnvironmentName}. "
                    + "An unauthenticated operator surface is a Development convenience "
                    + "and must not be configured anywhere else.");
            }

            dashboard.VisibleTo(_ => true);
        }
        else
        {
            dashboard.VisibleTo(IsOperator);
        }

        if (environment.IsProduction())
        {
            dashboard.InProduction(ProductionReason
                ?? throw new InvalidOperationException(
                    "Demo:Dashboard:ProductionReason is required when the dashboard runs in "
                    + "Production. It is shown verbatim to whoever opens the page."));
        }
    }

    private bool IsOperator(HttpContext context)
    {
        if (_operatorSecret is null)
        {
            return false;
        }

        var presented = context.Request.Headers[OperatorHeader].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        // Both sides are hashed first so the comparison is fixed-time over a
        // fixed width. FixedTimeEquals on the raw bytes would return early for
        // a length that does not match, which tells a caller how long to guess.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            _operatorSecret);
    }
}
