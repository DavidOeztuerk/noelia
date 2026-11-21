using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.InMemory.Sessions;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>The conformance suite against the in-process store.</summary>
[Trait("Category", "Unit")]
public class InMemoryRefreshTokenStoreTests : RefreshTokenStoreConformance
{
    private readonly InMemoryRefreshTokenStore _store = new();

    protected override IRefreshTokenStore Store => _store;

    protected override Task<int> CountOpenAsync(SessionId session) =>
        Task.FromResult(_store.OpenTokenCount(session));

    /// <summary>
    /// The store has no way to delete one row, and a test hook in production
    /// code is not worth it; the dictionaries are reached by reflection.
    /// </summary>
    protected override Task DeleteRowAsync(byte[] tokenHash)
    {
        var key = Convert.ToBase64String(tokenHash);
        var tokens = (Dictionary<RefreshTokenId, RefreshTokenRecord>)Field("_tokens").GetValue(_store)!;
        var byHash = (Dictionary<string, RefreshTokenId>)Field("_byHash").GetValue(_store)!;

        lock (Field("_gate").GetValue(_store)!)
        {
            tokens.Remove(byHash[key]);
            byHash.Remove(key);
        }

        return Task.CompletedTask;
    }

    private static System.Reflection.FieldInfo Field(string name) =>
        typeof(InMemoryRefreshTokenStore).GetField(
            name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
}
