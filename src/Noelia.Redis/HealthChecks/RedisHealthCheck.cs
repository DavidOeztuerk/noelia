using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Noelia.Redis.HealthChecks;

/// <summary>
/// Whether this service can actually read and write on its RESP server.
/// </summary>
/// <remarks>
/// <para>A round trip, not a description of the server. The check writes a key,
/// reads it back, compares it and deletes it — which is the question a
/// readiness probe is asking, and the only one whose answer decides whether
/// traffic should arrive. A connection that reports itself as open proves
/// nothing: a replica in the middle of a failover accepts connections and
/// refuses writes.</para>
///
/// <para><strong>No admin commands.</strong> Until 5.1.0 this asked for
/// <c>INFO</c> to report the version, the memory in use and the client count.
/// <c>AddRedisConnection</c> enables admin commands in Development only — for
/// good reason, since the same permission carries FLUSHALL and CONFIG — so
/// outside Development the check threw <em>"This operation is not available
/// unless admin mode is enabled"</em> and reported the server as unhealthy
/// whether or not it was. That is why nothing ever registered it: the class was
/// dead because it could not work.</para>
///
/// <para>The figures it used to collect were never a health question anyway.
/// Memory, hit ratio and operations per second belong in the telemetry pipeline
/// where they can be graphed over time, not in a probe that has to answer yes
/// or no — and not in a response body that <c>/health</c> serves to whoever
/// asks.</para>
/// </remarks>
public class RedisHealthCheck : IHealthCheck
{
    /// <summary>Beyond this, the server answers but is not answering well.</summary>
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(500);

    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ILogger<RedisHealthCheck> _logger;

    /// <summary>Takes the multiplexer <c>AddRedisConnection</c> registered.</summary>
    /// <param name="connectionMultiplexer">The shared connection.</param>
    /// <param name="logger">Where the detail of a failure goes.</param>
    public RedisHealthCheck(
        IConnectionMultiplexer connectionMultiplexer,
        ILogger<RedisHealthCheck> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _logger = logger;
    }

    /// <summary>Writes, reads, compares and deletes one key.</summary>
    /// <param name="context">Supplied by the health check service.</param>
    /// <param name="cancellationToken">Abandons the probe.</param>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_connectionMultiplexer.IsConnected)
            {
                return HealthCheckResult.Unhealthy("The connection to the RESP server is not established.");
            }

            var database = _connectionMultiplexer.GetDatabase();

            // Namespaced and short-lived: a probe must not leave anything behind
            // that another probe, or another replica, could read as state.
            var key = new RedisKey($"noelia:healthcheck:{Guid.NewGuid():N}");
            var written = Guid.NewGuid().ToString("N");

            var stopwatch = Stopwatch.StartNew();

            await database.StringSetAsync(key, written, TimeSpan.FromMinutes(1));
            var read = await database.StringGetAsync(key);
            await database.KeyDeleteAsync(key);

            stopwatch.Stop();

            if (read != written)
            {
                return HealthCheckResult.Unhealthy(
                    "The RESP server accepted a write and returned something else.");
            }

            // Shapes only. /health is not an authenticated endpoint everywhere
            // it is exposed, and a round-trip time is the most it should say
            // about the infrastructure behind it.
            var data = new Dictionary<string, object>
            {
                ["round_trip_ms"] = stopwatch.ElapsedMilliseconds
            };

            if (stopwatch.Elapsed > Slow)
            {
                _logger.LogWarning(
                    "RESP round trip took {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

                return HealthCheckResult.Degraded(
                    $"The RESP server answered in {stopwatch.ElapsedMilliseconds}ms.", data: data);
            }

            return HealthCheckResult.Healthy("The RESP server accepted a write and returned it.", data);
        }
        catch (Exception exception)
        {
            // The detail goes to the log, which has a reader who is allowed to
            // see it. The description does not: a StackExchange.Redis failure
            // names the endpoint it was talking to, and /health hands its body
            // to whoever asked.
            _logger.LogError(exception, "RESP health check failed");

            return HealthCheckResult.Unhealthy(
                "The RESP server could not be reached or refused the round trip.");
        }
    }
}
