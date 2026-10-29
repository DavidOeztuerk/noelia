using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Noelia.Infrastructure.Models;
using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>What the receiving side was told in code: the one audience it answers to.</summary>
internal sealed class ServiceCallerOptions
{
    public string Audience { get; set; } = string.Empty;
}

/// <summary>
/// Makes the service scheme out of what the person scheme already uses: the key ring
/// and the issuer, but a different audience and a different token type.
/// </summary>
/// <remarks>
/// Its own events and nothing inherited. The person scheme answers a challenge with a
/// JSON body, takes a token from the query string on WebSocket paths and sets a
/// <c>Token-Expired</c> header; a door that did any of that would be recognisable.
/// </remarks>
internal sealed class ServiceCallerSchemeConfiguration(
    IServiceProvider services,
    IOptions<JwtSettings> jwt,
    IOptions<ServiceCallerOptions> options) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions bearer)
    {
        if (!string.Equals(name, ServiceTokenDefaults.AuthenticationScheme, StringComparison.Ordinal))
        {
            return;
        }

        // A missing ring or issuer is reported at start by ServiceCallerOptionsValidator;
        // throwing here would replace that message with a stack trace out of the options system.
        var ring = services.GetService<KeyRing>();
        var issuer = jwt.Value.Issuer;
        if (ring is null || string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(options.Value.Audience))
        {
            return;
        }

        var parameters = ring.ValidationParameters(issuer, options.Value.Audience);

        // Both spellings: RFC 7515 lets the "application/" prefix of a media type be left off,
        // and the token library compares the header as written.
        parameters.ValidTypes =
        [
            ServiceTokenDefaults.TokenType,
            "application/" + ServiceTokenDefaults.TokenType
        ];
        parameters.NameClaimType = ServiceTokenDefaults.CallerClaim;
        parameters.AuthenticationType = ServiceTokenDefaults.AuthenticationScheme;

        bearer.TokenValidationParameters = parameters;
        bearer.MapInboundClaims = false;
        bearer.SaveToken = false;
        bearer.RequireHttpsMetadata = false;
        bearer.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ServiceCallerAudit.RecordFailure
        };
    }

    public void Configure(JwtBearerOptions bearer) => Configure(Options.DefaultName, bearer);
}

/// <summary>
/// The checks that need the key ring and the person settings, run when the host starts.
/// </summary>
/// <remarks>
/// Not at the call: the ring is registered by <c>UseJwt</c>, which may stand before or
/// after <c>UseServiceCallers</c>, and an answer that depends on the order of two lines
/// is how a check ends up passing in the one order it was tried in.
/// </remarks>
internal sealed class ServiceCallerOptionsValidator(IServiceProvider services, IOptions<JwtSettings> jwt)
    : IValidateOptions<ServiceCallerOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceCallerOptions options)
    {
        var failures = new List<string>();

        var ring = services.GetService<KeyRing>();
        if (ring is null)
        {
            failures.Add(
                "Receiving service tokens needs a key ring (KeyRing) with the issuer's public key. "
                + "Call UseJwt(jwt => jwt.VerifyOnly(publicKey, kid)); an OpenID Connect authority "
                + "(From(authority)) is not supported for service tokens.");
        }
        else if (ring.ValidationKeys.Any(key => !key.SeparatesIssuingFromVerifying))
        {
            // Every holder of a shared secret could sign a token naming any caller, and the
            // list at the door would decide nothing.
            failures.Add(
                "Receiving service tokens needs an asymmetric key ring. This one uses a shared "
                + "secret, with which every verifying service could mint a token for any caller. "
                + "Use UseJwt(jwt => jwt.VerifyOnly(publicKey, kid)).");
        }

        if (string.IsNullOrEmpty(jwt.Value.Issuer))
        {
            failures.Add("Receiving service tokens needs the issuer: JwtSettings:Issuer is not set.");
        }

        if (!ServiceTokenFormat.IsIdentifier(options.Audience))
        {
            failures.Add("The audience of this service is not a valid identifier.");
        }
        else if (string.Equals(options.Audience, jwt.Value.Audience, StringComparison.Ordinal))
        {
            failures.Add(
                $"The audience '{options.Audience}' is the audience of person tokens "
                + "(JwtSettings:Audience). Service tokens for it would open the people's API; "
                + "name this service's own audience.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Refuses to start a pipeline that would answer a door with a 500.
/// </summary>
/// <remarks>
/// An endpoint carrying authorization metadata, run without the authorization
/// middleware, fails inside the framework on every request. For a door that is worse
/// than a bug: a 500 where a 404 belongs says the door exists. The registration cannot
/// see the pipeline, so the check runs after the whole pipeline has been built.
/// </remarks>
internal sealed class ServiceCallerPipelineGuard : IStartupFilter
{
    // The key UseAuthorization() leaves in the builder's properties.
    private const string AuthorizationMiddlewareSet = "__AuthorizationMiddlewareSet";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);

        if (!app.Properties.ContainsKey(AuthorizationMiddlewareSet))
        {
            throw new InvalidOperationException(
                "UseServiceCallers is composed, but the request pipeline never calls UseAuthorization(). "
                + "Without it a service door answers 500 instead of 404. Call app.UseAuthorization() "
                + "after routing, or use app.UseNoelia(...), whose UseAuth() step does.");
        }
    };
}
