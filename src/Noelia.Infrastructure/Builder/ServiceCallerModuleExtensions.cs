using Noelia.Abstractions.Hosting;

namespace Noelia.Infrastructure.Builder;

/// <summary>Adds the receiving side of service tokens to the composition.</summary>
public static class ServiceCallerModuleExtensions
{
    /// <summary>Accepts service tokens for <paramref name="audience"/>.</summary>
    public static NoeliaBuilder UseServiceCallers(this NoeliaBuilder noelia, string audience) =>
        throw new NotImplementedException();
}
