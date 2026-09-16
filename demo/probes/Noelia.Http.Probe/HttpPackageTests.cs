using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Hosting;
using Noelia.Http;

namespace Noelia.Http.Probe;

public sealed class HttpPackageTests
{
    [Fact]
    public async Task In_process_rate_limit_module_rejects_atomically_without_exposing_its_key()
    {
        const string key = "subject:HTTP-PROBE-CANARY-192.0.2.5";
        var host = Host.CreateApplicationBuilder();
        var noelia = new NoeliaBuilder(
            host.Services, host.Configuration, host.Environment, "http-probe", [], []);

        noelia.UseInProcessRateLimits();
        var composition = noelia.Build();
        using var provider = host.Services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDistributedRateLimitStore>();

        var first = await store.SlidingWindowIncrementAsync(key, 1, TimeSpan.FromMinutes(1));
        var second = await store.SlidingWindowIncrementAsync(key, 1, TimeSpan.FromMinutes(1));
        var inspection = await store.InspectAsync();

        composition.Included.Should().Equal(HttpNoeliaModule.InProcessRateLimits);
        first.IsAllowed.Should().BeTrue();
        second.IsAllowed.Should().BeFalse();
        inspection.Counters.Should().ContainSingle().Which.KeyFingerprint
            .Should().HaveLength(12).And.NotContain("CANARY");
    }
}
