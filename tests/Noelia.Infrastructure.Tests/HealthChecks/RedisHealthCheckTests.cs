using Noelia.Redis.HealthChecks;
using Noelia.Infrastructure.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Noelia.Infrastructure.Tests.HealthChecks;

[Trait("Category", "Unit")]
public class RedisHealthCheckTests
{
    private static HealthCheckContext CreateContext(IHealthCheck check) =>
        new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("redis", check, null, null)
        };

    [Fact]
    public async Task CheckHealthAsync_WhenNotConnected_ReturnsUnhealthy()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var endPoints = new EndPointCollection { new System.Net.DnsEndPoint("localhost", 6379) };
        multiplexer.GetEndPoints().Returns(endPoints.ToArray());
        multiplexer.IsConnected.Returns(false);

        var logger = Substitute.For<ILogger<RedisHealthCheck>>();
        var check = new RedisHealthCheck(multiplexer, logger);
        var context = CreateContext(check);

        var result = await check.CheckHealthAsync(context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("not established");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenGetDatabaseThrows_ReturnsUnhealthy()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var endPoints = new EndPointCollection { new System.Net.DnsEndPoint("localhost", 6379) };
        multiplexer.GetEndPoints().Returns(endPoints.ToArray());
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>())
            .Throws(RedisFailures.Unreachable("Connection refused"));

        var logger = Substitute.For<ILogger<RedisHealthCheck>>();
        var check = new RedisHealthCheck(multiplexer, logger);
        var context = CreateContext(check);

        var result = await check.CheckHealthAsync(context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        // Deliberately not attached. A writer that serialises the exception —
        // and Noelia's own did until 5.1.0 — puts the driver's endpoint into an
        // anonymous response. The detail is in the log, which has a reader who
        // is allowed to see it.
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task CheckHealthAsync_WhenStringSetFails_ReturnsUnhealthy()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var endPoints = new EndPointCollection { new System.Net.DnsEndPoint("localhost", 6379) };

        multiplexer.GetEndPoints().Returns(endPoints.ToArray());
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
            Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("SET failed"));

        var logger = Substitute.For<ILogger<RedisHealthCheck>>();
        var check = new RedisHealthCheck(multiplexer, logger);
        var context = CreateContext(check);

        var result = await check.CheckHealthAsync(context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenReadWriteValueMismatch_ReturnsUnhealthy()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var endPoints = new EndPointCollection { new System.Net.DnsEndPoint("localhost", 6379) };

        multiplexer.GetEndPoints().Returns(endPoints.ToArray());
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
            Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()).Returns(true);
        // Return different value than what was stored
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)"unexpected-value");
        database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);

        var logger = Substitute.For<ILogger<RedisHealthCheck>>();
        var check = new RedisHealthCheck(multiplexer, logger);
        var context = CreateContext(check);

        var result = await check.CheckHealthAsync(context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("returned something else");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenReadWriteSucceeds_ReturnsHealthy()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var server = Substitute.For<IServer>();
        var endPoints = new EndPointCollection { new System.Net.DnsEndPoint("localhost", 6379) };

        multiplexer.GetEndPoints().Returns(endPoints.ToArray());
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        multiplexer.GetServer(Arg.Any<System.Net.EndPoint>(), Arg.Any<object?>()).Returns(server);

        // Capture stored value so StringGetAsync can echo it back
        RedisValue storedValue = default;
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
            Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                storedValue = callInfo.ArgAt<RedisValue>(1);
                return Task.FromResult(true);
            });
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromResult(storedValue));
        database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);

        IGrouping<string, KeyValuePair<string, string>>[] emptyInfo = Array.Empty<IGrouping<string, KeyValuePair<string, string>>>();
        server.InfoAsync(Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(emptyInfo);

        var logger = Substitute.For<ILogger<RedisHealthCheck>>();
        var check = new RedisHealthCheck(multiplexer, logger);
        var context = CreateContext(check);

        var result = await check.CheckHealthAsync(context);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("accepted a write and returned it");
    }
}

/// <summary>
/// Pins what the RESP readiness check may and may not do.
/// </summary>
/// <remarks>
/// Both of these were real. The check asked for <c>INFO</c>, which
/// <c>AddRedisConnection</c> permits in Development only, so outside
/// Development it reported every reachable server as unhealthy — which is why
/// nothing ever registered it. And it copied the driver's exception message
/// into the description, which <c>/health</c> serves anonymously; a
/// StackExchange.Redis failure names the endpoint it was talking to.
/// </remarks>
[Trait("Category", "Unit")]
public class RedisHealthCheckStaysUsableTests
{
    private const string Canary = "redis://admin:hunter2@cache.internal.example:6379";

    [Fact]
    public async Task A_driver_failure_does_not_reach_the_response_body()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase().Returns(_ => throw new InvalidOperationException(
            $"No connection is available to service this operation: {Canary}"));

        var check = new RedisHealthCheck(
            multiplexer, Substitute.For<ILogger<RedisHealthCheck>>());

        var result = await check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("redis", check, null, null)
        });

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotContain("cache.internal.example");
        result.Description.Should().NotContain("hunter2");
        result.Description.Should().NotContain(Canary);
    }

    [Fact]
    public void The_check_names_no_admin_command()
    {
        var source = File.ReadAllText(SourceFile());

        source.Should().NotContain("InfoAsync",
            "INFO is an admin command, and AddRedisConnection enables admin mode in "
            + "Development only — a readiness check that needs it reports every "
            + "production server as unhealthy");
    }

    private static string SourceFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "Noelia.Redis", "HealthChecks", "RedisHealthCheck.cs");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("RedisHealthCheck.cs not found above the test output.");
    }
}
