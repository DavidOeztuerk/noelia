using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Hosting;
using Noelia.InMemory.Hosting;

namespace Noelia.InMemory.Probe;

public sealed class InMemoryPackageTests
{
    [Fact]
    public async Task Cache_provider_is_one_module_and_roundtrips_a_value()
    {
        var host = Host.CreateApplicationBuilder();
        var noelia = new NoeliaBuilder(
            host.Services, host.Configuration, host.Environment, "in-memory-probe", [], []);

        noelia.UseInMemoryCache("probe");
        var composition = noelia.Build();
        using var provider = host.Services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCacheService>();

        await cache.SetAsync("answer", new Payload("stored"));
        var value = await cache.GetAsync<Payload>("answer");

        composition.Included.Should().Equal(InMemoryNoeliaModules.Cache);
        value.Should().Be(new Payload("stored"));
    }

    private sealed record Payload(string Value);
}
