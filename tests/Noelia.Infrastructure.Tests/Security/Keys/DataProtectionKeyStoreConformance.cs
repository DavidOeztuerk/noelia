using Noelia.Abstractions.Security.Keys;
using Noelia.InMemory.Caching;
using Noelia.Infrastructure.Tests.Security;
using Noelia.Redis.Security;

namespace Noelia.Infrastructure.Tests.Security.Keys;

/// <summary>The same non-expiring, append-only contract for every key-ring provider.</summary>
public abstract class DataProtectionKeyStoreConformance
{
    protected abstract IDataProtectionKeyStore CreateStore();

    [Fact]
    public async Task Concurrent_key_and_revocation_appends_and_retries_lose_no_elements()
    {
        var store = CreateStore();
        await Task.WhenAll(Enumerable.Range(0, 32).SelectMany(index =>
            Enumerable.Range(0, 2).Select(_ => store.AppendAsync($"element-{index}",
                index % 2 == 0 ? $"<key id='{index}'/>" : $"<revocation id='{index}'/>"))));
        var read = await store.ReadAllAsync();
        read.Should().HaveCount(32);
        read.Select(element => element.Id).Should().OnlyHaveUniqueItems();
        read.Count(element => element.Xml.StartsWith("<revocation", StringComparison.Ordinal)).Should().Be(16);
    }

    [Fact]
    public async Task A_conflicting_append_cannot_replace_stored_key_material()
    {
        var store = CreateStore();
        await store.AppendAsync("key", "<key id='original'/>");
        var change = () => store.AppendAsync("key", "<key id='replacement'/>");
        await change.Should().ThrowAsync<InvalidOperationException>();
        (await store.ReadAllAsync()).Single().Xml.Should().Be("<key id='original'/>");
    }

    [Fact]
    public async Task A_cancelled_append_changes_nothing()
    {
        var store = CreateStore();
        var append = () => store.AppendAsync("key", "<key/>", new CancellationToken(true));
        await append.Should().ThrowAsync<OperationCanceledException>();
        (await store.ReadAllAsync()).Should().BeEmpty();
    }
}

[Trait("Category", "Unit")]
public sealed class InMemoryDataProtectionKeyStoreConformanceTests : DataProtectionKeyStoreConformance
{
    protected override IDataProtectionKeyStore CreateStore() => new InMemoryDataProtectionKeyStore();
}

[Trait("Category", "Integration")]
public sealed class RedisDataProtectionKeyStoreConformanceTests(RedisFixture fixture)
    : DataProtectionKeyStoreConformance, IClassFixture<RedisFixture>
{
    protected override IDataProtectionKeyStore CreateStore() => new RedisDataProtectionKeyStore(
        fixture.Connection ?? throw new InvalidOperationException(
            "Key-ring conformance requires a real disposable Redis container.", fixture.StartupFailure),
        $"keyring-conformance-{Guid.NewGuid():N}");
}
