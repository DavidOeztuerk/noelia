namespace Noelia.Infrastructure.Security.M2M;

/// <summary>
/// The in-process store of service tokens, one per client and audience.
/// </summary>
/// <remarks>
/// A singleton on purpose: the provider is a typed <c>HttpClient</c> and so transient,
/// and a cache that lived in it would fetch a new token on every resolution.
/// </remarks>
public sealed class ServiceTokenCache
{
}
