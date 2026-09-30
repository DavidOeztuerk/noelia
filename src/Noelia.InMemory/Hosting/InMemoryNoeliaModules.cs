using Noelia.Abstractions.Hosting;
using Noelia.InMemory.Caching;
using Noelia.InMemory.Sessions;

namespace Noelia.InMemory.Hosting;

/// <summary>
/// The modules this package contributes to a Noelia composition.
/// </summary>
/// <remarks>
/// Noelia knows nothing about these. They reach a composition the way a database
/// provider reaches Entity Framework's options: through an extension method on
/// the builder, carrying their own registration with them.
/// </remarks>
public static class InMemoryNoeliaModules
{
    /// <summary>Caching, kept in this process.</summary>
    public static NoeliaModule Cache => new("InMemory.Cache");

    /// <summary>Refresh tokens, kept in this process.</summary>
    public static NoeliaModule RefreshTokens => new("InMemory.RefreshTokens");

    /// <summary>
    /// Keeps cached values in this process.
    /// </summary>
    /// <remarks>
    /// Nothing is shared between instances: a value written on one is invisible
    /// to the next, and an invalidation reaches only the instance that ran it.
    /// Right for a single instance and for tests; for anything else use the
    /// Redis package.
    /// <para>
    /// Without a cache module of some kind, the modules that need one refuse to
    /// start and name themselves while doing it.
    /// </para>
    /// </remarks>
    /// <param name="noelia">The composition.</param>
    /// <param name="keyPrefix">
    /// Namespaces every key, so two services sharing a store cannot read each
    /// other's entries.
    /// </param>
    public static NoeliaBuilder UseInMemoryCache(this NoeliaBuilder noelia, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(noelia);
        return noelia.Use(
            Cache,
            g => g.Services.AddInMemoryCache(keyPrefix),
            contract => contract
                .Provides<Noelia.Abstractions.Caching.IDistributedCacheService>(
                    "Noelia.InMemory", "UseInMemoryCache(prefix)")
                .Provides<Noelia.Abstractions.Security.Keys.IDataProtectionKeyStore>(
                    "Noelia.InMemory", "UseInMemoryCache(prefix)"));
    }

    /// <summary>
    /// Keeps refresh tokens in this process.
    /// </summary>
    /// <remarks>
    /// Every restart signs everyone out, and a second instance does not know the
    /// tokens the first issued. For a deployment, keep them in the database with
    /// <c>AddEntityFrameworkRefreshTokens</c>.
    /// </remarks>
    public static NoeliaBuilder UseInMemoryRefreshTokens(this NoeliaBuilder noelia)
    {
        ArgumentNullException.ThrowIfNull(noelia);
        return noelia.Use(
            RefreshTokens,
            g => g.Services.AddInMemoryRefreshTokens(),
            contract => contract.Provides<Noelia.Abstractions.Security.Sessions.IRefreshTokenStore>(
                "Noelia.InMemory", "UseInMemoryRefreshTokens()"));
    }
}
