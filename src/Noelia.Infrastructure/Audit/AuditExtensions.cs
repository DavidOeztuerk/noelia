using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Noelia.Abstractions.Audit;

namespace Noelia.Infrastructure.Audit;

/// <summary>
/// Extension methods for registering revision-safe audit trail services.
/// </summary>
public static class AuditExtensions
{
    /// <summary>
    /// Registers <see cref="IAuditTrailService"/> with <see cref="AuditTrailService"/>.
    /// Requires an <see cref="ISovereignAuditSink"/> to be registered, or falls back to
    /// <see cref="InMemorySovereignAuditSink"/> if none is present.
    /// </summary>
    public static IServiceCollection AddSovereignAuditTrail(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISovereignAuditSink, InMemorySovereignAuditSink>();
        services.TryAddSingleton<IAuditTrailService, AuditTrailService>();

        // Always registered, never conditional on the sink. A verifier that
        // only appeared when the sink happened to support reading would make
        // "no verifier" and "nothing to verify" indistinguishable from the
        // outside; this one answers either way, and says which it is.
        services.TryAddSingleton<IAuditChainVerifier, AuditChainVerifier>();

        return services;
    }

    /// <summary>
    /// Registers a custom sovereign audit sink destination.
    /// </summary>
    /// <typeparam name="TSink">The sink implementation.</typeparam>
    public static IServiceCollection AddSovereignAuditSink<TSink>(this IServiceCollection services)
        where TSink : class, ISovereignAuditSink
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISovereignAuditSink, TSink>();
        services.TryAddSingleton<IAuditTrailService, AuditTrailService>();

        // Always registered, never conditional on the sink. A verifier that
        // only appeared when the sink happened to support reading would make
        // "no verifier" and "nothing to verify" indistinguishable from the
        // outside; this one answers either way, and says which it is.
        services.TryAddSingleton<IAuditChainVerifier, AuditChainVerifier>();

        return services;
    }
}
