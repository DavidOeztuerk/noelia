using Microsoft.Extensions.Options;
using Noelia.Infrastructure.Models;
using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>One client's hash slots, already parsed to digests.</summary>
/// <param name="Current">The slot that is always set.</param>
/// <param name="Previous">The slot kept open during a rotation, or null when closed.</param>
internal sealed record ServiceClientRegistration(byte[] Current, byte[]? Previous);

/// <summary>
/// What the issuer was told in code: which clients exist, which audiences it may
/// issue for and for how long.
/// </summary>
internal sealed class ServiceTokenIssuerOptions
{
    public IReadOnlyDictionary<string, ServiceClientRegistration> Clients { get; set; } =
        new Dictionary<string, ServiceClientRegistration>();

    public IReadOnlyList<string> Audiences { get; set; } = [];

    public TimeSpan Lifetime { get; set; } = ServiceTokenDefaults.DefaultLifetime;
}

/// <summary>
/// The checks that need the key ring, run when the host starts.
/// </summary>
/// <remarks>
/// The builder cannot make these itself: the ring is registered by <c>UseJwt</c>,
/// which a composition root may call before or after <c>UseServiceTokenIssuer</c>,
/// and an answer that depends on the order of two lines is how a check ends up
/// passing in the one order it was tried in. Resolving the ring here, at start,
/// makes it a property of the finished container.
/// </remarks>
internal sealed class ServiceTokenIssuerOptionsValidator : IValidateOptions<ServiceTokenIssuerOptions>
{
    private readonly IServiceProvider _services;
    private readonly IOptions<JwtSettings> _jwt;

    public ServiceTokenIssuerOptionsValidator(IServiceProvider services, IOptions<JwtSettings> jwt)
    {
        _services = services;
        _jwt = jwt;
    }

    public ValidateOptionsResult Validate(string? name, ServiceTokenIssuerOptions options)
    {
        var failures = new List<string>();

        var ring = (KeyRing?)_services.GetService(typeof(KeyRing));
        if (ring is null)
        {
            failures.Add(
                "Issuing service tokens needs a key ring. Call UseJwt(jwt => jwt.Issue(privateKey, kid)).");
        }
        else if (ring.SigningKey is null)
        {
            failures.Add(
                "Issuing service tokens needs a signing key, and this ring only verifies. "
                + "Call UseJwt(jwt => jwt.Issue(privateKey, kid)) on the issuing service.");
        }
        else if (!ring.SigningKey.SeparatesIssuingFromVerifying)
        {
            // With a shared secret every service that verifies a token also holds the
            // key that signs one, so it could mint a service token for any caller and
            // the receiving side's table of who may do what would decide nothing.
            failures.Add(
                "Issuing service tokens needs an asymmetric signing key. The key ring uses a shared "
                + "secret, with which every verifying service could mint service tokens for any caller. "
                + "Use UseJwt(jwt => jwt.Issue(privateKey, kid)) and give the others the public key.");
        }

        if (options.Clients.Count == 0)
        {
            failures.Add("The issuer has no clients. Name them with Clients(configuration, ids).");
        }

        if (options.Audiences.Count == 0)
        {
            failures.Add("The issuer has no audiences. Name them with Audiences(...).");
        }

        var personAudience = _jwt.Value.Audience;
        if (!string.IsNullOrEmpty(personAudience)
            && options.Audiences.Contains(personAudience, StringComparer.Ordinal))
        {
            failures.Add(
                $"The audience '{personAudience}' is the audience of person tokens "
                + "(JwtSettings:Audience). A service token for it would open the people's API.");
        }

        if (options.Lifetime < ServiceTokenDefaults.MinimumLifetime
            || options.Lifetime > ServiceTokenDefaults.MaximumLifetime)
        {
            failures.Add("The service token lifetime has to be between 1 and 15 minutes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
