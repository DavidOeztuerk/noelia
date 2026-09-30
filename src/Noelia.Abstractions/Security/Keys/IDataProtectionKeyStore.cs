namespace Noelia.Abstractions.Security.Keys;

/// <summary>Durable, append-only storage for ASP.NET Data Protection key and revocation XML.</summary>
/// <remarks>
/// Appending a named element must be atomic across writers. Reusing an id with
/// different XML must fail; an implementation must never expire an element.
/// This contract contains no cache or provider dependency.
/// </remarks>
public interface IDataProtectionKeyStore
{
    /// <summary>Returns every stored element; an empty result means the ring is empty.</summary>
    Task<IReadOnlyCollection<DataProtectionStoredElement>> ReadAllAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Atomically adds an element if absent, or accepts an identical retry.</summary>
    Task AppendAsync(string id, string xml, CancellationToken cancellationToken = default);
}

/// <summary>One named encrypted key or revocation element.</summary>
public sealed record DataProtectionStoredElement(string Id, string Xml);
