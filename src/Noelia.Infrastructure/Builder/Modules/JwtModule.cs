using Noelia.Infrastructure.Extensions;
using Noelia.Infrastructure.Security;
using Noelia.Infrastructure.Security.Keys;
using Microsoft.Extensions.DependencyInjection;

namespace Noelia.Infrastructure.Builder.Modules;

public static class JwtModule
{
    /// <summary>
    /// Adds JWT authentication: the bearer scheme that verifies tokens and,
    /// where a signing key is present, the <see cref="IJwtService"/> that
    /// issues them.
    /// </summary>
    /// <remarks>
    /// One module for both sides because both read the same keys. A service
    /// that only verifies simply holds no signing key, and then cannot issue
    /// even by mistake.
    /// <para>
    /// Called without <paramref name="configure"/> it uses the shared secret
    /// from configuration, exactly as before. That path cannot separate issuing
    /// from verifying — every service holding the secret can mint a token for
    /// any subject. Prefer a key pair, and give the private half to the issuer
    /// alone.
    /// </para>
    /// <para>
    /// That fallback is silent no longer: the host logs one warning at start,
    /// because Noelia 8.0 will refuse it. Passing keys or an authority, or
    /// <c>UseJwt(jwt => jwt.FromSharedSecret())</c>, is the explicit choice and
    /// does not warn.
    /// </para>
    /// </remarks>
    /// <param name="builder">The infrastructure builder.</param>
    /// <param name="configure">Supplies the keys. Omit for the shared secret.</param>
    public static InfrastructureBuilder AddJwtAuthentication(
        this InfrastructureBuilder builder,
        Action<JwtOptions>? configure = null)
    {
        builder.JwtEnabled = true;

        var options = new JwtOptions();
        configure?.Invoke(options);

        // Built here rather than lazily: a key that cannot be read is a
        // composition error, and finding it on the first request means finding
        // it in production.
        var keys = KeyRingFactory.Build(options, builder.Configuration, out var fromFallback);

        // The key ring and IJwtService are registered by the call below, together,
        // whenever there are keys at all: a service that only verifies still
        // validates tokens through it. Issuing is what fails, and it fails naming
        // the missing signing key.
        builder.Services.AddJwtAuthentication(
            keys, options.Authority, builder.Configuration, builder.Environment);

        if (fromFallback)
        {
            SharedSecretFallbackWarning.AddSharedSecretFallbackWarning(builder.Services);
        }

        return builder;
    }
}
