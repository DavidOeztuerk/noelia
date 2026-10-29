using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Builder;

/// <summary>Adds the receiving side of service tokens to the composition.</summary>
public static class ServiceCallerModuleExtensions
{
    /// <summary>
    /// Lets this service name which other services may call which of its endpoints.
    /// </summary>
    /// <remarks>
    /// Needs <c>UseJwt(jwt =&gt; jwt.VerifyOnly(publicKey, kid))</c> in the same
    /// composition, in either order. A shared-secret ring, a ring without keys of its own
    /// (<c>From(authority)</c>) and an audience equal to <c>JwtSettings:Audience</c> stop
    /// the host from starting; so does a pipeline that never calls
    /// <c>UseAuthorization()</c>.
    /// <para>
    /// This adds an authentication scheme next to the person scheme without changing the
    /// default one, the requirement and its handler, and the result handler that turns a
    /// refusal into a 404. The doors themselves are <c>RequireServiceCaller(...)</c> and
    /// <c>[ServiceCaller(...)]</c>, and <c>MapServiceFallback()</c> covers the paths
    /// that have none.
    /// </para>
    /// </remarks>
    /// <param name="noelia">The composition.</param>
    /// <param name="audience">
    /// What this service calls itself in a token's <c>aud</c>: lower case, starting with a
    /// letter, up to 48 letters, digits and hyphens. Not the audience of people's tokens.
    /// </param>
    /// <exception cref="ArgumentException">The audience is not an identifier.</exception>
    public static NoeliaBuilder UseServiceCallers(this NoeliaBuilder noelia, string audience)
    {
        ArgumentNullException.ThrowIfNull(noelia);

        if (!ServiceTokenFormat.IsIdentifier(audience))
        {
            throw new ArgumentException(
                "The audience starts with a lower-case letter and has up to 48 lower-case letters, "
                + "digits and hyphens.",
                nameof(audience));
        }

        return noelia.Use(
            NoeliaModule.ServiceCallers,
            module => Register(module.Services, audience),
            contract => contract
                .Requires<KeyRing>(new NoeliaProviderHint(
                    "Noelia.Infrastructure", "UseJwt(jwt => jwt.VerifyOnly(publicKey, kid))"))
                .Provides<IAuthorizationMiddlewareResultHandler>(
                    "Noelia.Infrastructure", "UseServiceCallers(audience)"));
    }

    private static void Register(IServiceCollection services, string audience)
    {
        // No default scheme: the person scheme keeps being the one a plain [Authorize] means.
        services.AddAuthorization();
        services.AddAuthentication().AddJwtBearer(ServiceTokenDefaults.AuthenticationScheme, _ => { });
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<JwtBearerOptions>, ServiceCallerSchemeConfiguration>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, ServiceCallerHandler>());
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IStartupFilter, ServiceCallerPipelineGuard>());

        services.AddSingleton<IValidateOptions<ServiceCallerOptions>, ServiceCallerOptionsValidator>();
        services.AddOptions<ServiceCallerOptions>()
            .Configure(options => options.Audience = audience)
            .ValidateOnStart();

        DecorateResultHandler(services);
    }

    /// <summary>
    /// Puts the 404 decorator in front of whatever result handler is registered, which
    /// may be the framework's or one the service wrote.
    /// </summary>
    private static void DecorateResultHandler(IServiceCollection services)
    {
        var existing = services.LastOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IAuthorizationMiddlewareResultHandler)
            && !descriptor.IsKeyedService);

        if (existing is not null)
        {
            services.Remove(existing);
        }

        services.AddSingleton<IAuthorizationMiddlewareResultHandler>(provider =>
            new ServiceCallerAuthorizationResultHandler(
                Wrapped(provider, existing),
                provider.GetRequiredService<ILoggerFactory>()));
    }

    private static IAuthorizationMiddlewareResultHandler Wrapped(
        IServiceProvider provider, ServiceDescriptor? existing)
    {
        if (existing?.ImplementationInstance is IAuthorizationMiddlewareResultHandler instance)
        {
            return instance;
        }

        if (existing?.ImplementationFactory is { } factory)
        {
            return (IAuthorizationMiddlewareResultHandler)factory(provider);
        }

        if (existing?.ImplementationType is { } type)
        {
            return (IAuthorizationMiddlewareResultHandler)ActivatorUtilities.CreateInstance(provider, type);
        }

        return new Microsoft.AspNetCore.Authorization.Policy.AuthorizationMiddlewareResultHandler();
    }
}
