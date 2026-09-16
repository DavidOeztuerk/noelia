using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Audit;
using Noelia.Infrastructure.Tests.Security;
using Noelia.Redis.Security.Audit;

namespace Noelia.Infrastructure.Tests.Audit;

/// <summary>
/// The shared-chain contract, answered by a real RESP server.
/// </summary>
/// <remarks>
/// The interesting promise cannot be kept by a dictionary and a lock: two
/// processes racing for one head need the store itself to decide the winner.
/// This is where the Lua script either serialises them or does not, and no
/// in-process double can tell us which.
/// <para>
/// Fails rather than skips without a container runtime, for the same reason as
/// the other Redis suites: a silently skipped integrity test is indistinguishable
/// from a passing one.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class RedisSharedAuditChainTests : SharedAuditChainConformance, IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;

    public RedisSharedAuditChainTests(RedisFixture fixture)
    {
        _fixture = fixture;

        if (fixture.Connection is null)
        {
            throw new InvalidOperationException(
                "The shared audit chain suite needs a container runtime. Start Docker and run again.",
                fixture.StartupFailure);
        }
    }

    /// <summary>
    /// A prefix per test, so one test's chain is never another's predecessor.
    /// </summary>
    protected override IChainedSovereignAuditSink CreateSink() =>
        new RedisSovereignAuditSink(
            _fixture.Connection!,
            NullLogger<RedisSovereignAuditSink>.Instance,
            keyPrefix: $"probe-{Guid.NewGuid():N}");
}
