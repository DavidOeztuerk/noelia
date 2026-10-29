using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Noelia.Infrastructure.Models;
using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// Checks a client's secret against the register and signs a token for one audience.
/// </summary>
/// <remarks>
/// Every refusal returns null and none says why, to the caller: a client id that
/// is unknown and a secret that is wrong must look identical from outside, or the
/// endpoint becomes a way to list the services. The reason goes to the log,
/// without the values that were presented.
/// </remarks>
internal sealed class ServiceTokenIssuer : IServiceTokenIssuer
{
    // Compared against when the client is unknown or a slot is empty, so those
    // cases do the same work as a real mismatch. Nothing hashes to it in practice;
    // it is only ever the right-hand side of a comparison that must not match.
    private static readonly byte[] Dummy = SHA256.HashData("noelia-m2m-no-such-client"u8);

    private readonly IReadOnlyDictionary<string, ServiceClientRegistration> _clients;
    private readonly HashSet<string> _audiences;
    private readonly TimeSpan _lifetime;
    private readonly KeyRing _keys;
    private readonly string _issuer;
    private readonly ILogger<ServiceTokenIssuer> _logger;
    private readonly TimeProvider _time;

    public ServiceTokenIssuer(
        IOptions<ServiceTokenIssuerOptions> options,
        KeyRing keys,
        IOptions<JwtSettings> jwt,
        ILogger<ServiceTokenIssuer> logger,
        TimeProvider time)
    {
        // Reading Value is what runs the start-up checks for a container that
        // resolves the issuer without having started a host.
        var settings = options.Value;
        _clients = settings.Clients;
        _audiences = new HashSet<string>(settings.Audiences, StringComparer.Ordinal);
        _lifetime = settings.Lifetime;
        _keys = keys;
        _issuer = jwt.Value.Issuer;
        _logger = logger;
        _time = time;
    }

    public Task<ServiceToken?> IssueAsync(
        string clientId, string clientSecret, string audience, CancellationToken cancellationToken = default)
    {
        // The shape first and without hashing: a caller with no credential must not be
        // able to make this service digest values of its choosing.
        if (!ServiceTokenFormat.IsWellFormedSecret(clientSecret))
        {
            return Refuse(clientId, audience, "malformed secret");
        }

        var known = clientId is not null && _clients.TryGetValue(clientId, out _);
        var registration = known ? _clients[clientId!] : null;
        var presented = ServiceTokenFormat.DigestOf(clientSecret);

        // Both slots, every time, with the non-short-circuit operators: whether the
        // old or the new secret matched must not show in how long this took.
        var currentMatches = CryptographicOperations.FixedTimeEquals(
            presented, registration?.Current ?? Dummy);
        var previousMatches = CryptographicOperations.FixedTimeEquals(
            presented, registration?.Previous ?? Dummy);
        var previousOpen = registration?.Previous is not null;
        var authentic = known & (currentMatches | (previousMatches & previousOpen));

        if (!authentic)
        {
            return Refuse(clientId, audience, known ? "secret mismatch" : "unknown client");
        }

        if (!_audiences.Contains(audience))
        {
            return Refuse(clientId, audience, "audience not declared");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + _lifetime,
            TokenType = ServiceTokenDefaults.TokenType,
            SigningCredentials = _keys.RequireSigningKey().SigningCredentials(),

            // Claims, not Subject: there is no subject. A token with a sub is one a
            // person-shaped code path might accept; this one names a calling service
            // in client_id and nothing else.
            Claims = new Dictionary<string, object>
            {
                [ServiceTokenDefaults.CallerClaim] = clientId!,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            }
        });

        _logger.LogInformation(
            "Service token issued to client {ClientId} for audience {Audience}", clientId, audience);

        return Task.FromResult<ServiceToken?>(new ServiceToken(token, _lifetime));
    }

    /// <summary>
    /// Logs the refusal and returns the one answer a caller gets.
    /// </summary>
    /// <remarks>
    /// The client id appears only when it is registered, and the audience only when
    /// it is declared: both are otherwise strings the caller chose, and writing
    /// them to a log lets whoever calls the endpoint write into it.
    /// </remarks>
    private Task<ServiceToken?> Refuse(string? clientId, string? audience, string reason)
    {
        var client = clientId is not null && _clients.ContainsKey(clientId) ? clientId : "unknown client";
        var target = audience is not null && _audiences.Contains(audience) ? audience : "undeclared audience";

        _logger.LogWarning(
            "Service token refused for {ClientId}, audience {Audience}: {Reason}", client, target, reason);

        return Task.FromResult<ServiceToken?>(null);
    }
}
