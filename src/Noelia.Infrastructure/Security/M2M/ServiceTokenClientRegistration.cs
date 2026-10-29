using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Noelia.Infrastructure.Security.M2M;

/// <summary>
/// The one place the token provider is registered, shared by
/// <c>AddServiceCommunication</c> and <c>UseServiceTokenClient</c>.
/// </summary>
/// <remarks>
/// Both want the same typed client. Registering it twice would leave two descriptors
/// where the last one silently wins, and the process cache is what both must share, so
/// whichever runs first registers and the other finds it there.
/// </remarks>
internal static class ServiceTokenClientRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ServiceTokenCache>();

        // AddHttpClient registers the typed client transient; the cache above is what makes
        // that harmless.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IServiceTokenProvider)))
        {
            services.AddHttpClient<IServiceTokenProvider, ServiceTokenProvider>();
        }

        services.TryAddTransient<IServiceTokenSource>(provider =>
            provider.GetRequiredService<IServiceTokenProvider>() as IServiceTokenSource
            ?? throw new InvalidOperationException(
                "The registered IServiceTokenProvider does not hand out tokens per audience "
                + "(it is not an IServiceTokenSource). Register a ServiceTokenProvider or an IServiceTokenSource."));
    }
}
