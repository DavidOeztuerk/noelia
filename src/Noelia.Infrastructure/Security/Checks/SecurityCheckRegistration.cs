using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Security.Checks;

namespace Noelia.Infrastructure.Security.Checks;

internal static class SecurityCheckRegistration
{
    internal static IServiceCollection AddNoeliaSecurityChecks(this IServiceCollection services)
    {
        services.AddOptions<SecurityCheckOptions>();
        services.TryAddSingleton<SecurityCheckReport>();
        services.TryAddSingleton<ISecurityCheckReport>(
            provider => provider.GetRequiredService<SecurityCheckReport>());
        services.TryAddSingleton<ISecurityCheckRunner, SecurityCheckRunner>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, SecurityCheckStartupService>());

        Add<CompositionSecurityCheck>(services);
        Add<AuditChainScopeSecurityCheck>(services);
        Add<DataProtectionKeyRingSecurityCheck>(services);
        Add<EgressGuardSecurityCheck>(services);
        Add<ArtificialIntelligenceInventoryCheck>(services);
        Add<ArtificialIntelligenceTransferCheck>(services);
        Add<ArtificialIntelligenceRecordKeepingCheck>(services);
        Add<ReadinessCoverageSecurityCheck>(services);
        Add<JwtSecurityCheck>(services);
        Add<SecurityHeadersSecurityCheck>(services);
        Add<RefreshCookieSecurityCheck>(services);
        Add<CorsSecurityCheck>(services);
        Add<SecretProviderSecurityCheck>(services);
        Add<EncryptionSecurityCheck>(services);
        Add<RateLimitDegradationSecurityCheck>(services);
        Add<RevocationDegradationSecurityCheck>(services);

        return services;
    }

    private static void Add<TCheck>(IServiceCollection services)
        where TCheck : class, ISecurityCheck =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecurityCheck, TCheck>());
}
