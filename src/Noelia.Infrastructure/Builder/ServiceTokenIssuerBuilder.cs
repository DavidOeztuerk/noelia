using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using ConfigurationException = Noelia.Core.Exceptions.ConfigurationException;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Builder;

/// <summary>
/// Which services may ask for a token, for which audiences, and for how long it
/// lives.
/// </summary>
/// <remarks>
/// Everything that grants something is written here, in code: the client ids and
/// the audiences. The configuration supplies only the hashes of the secrets, so a
/// stray entry in an environment cannot create a client or open an audience, and
/// the list a reviewer reads in the pull request is the list that runs.
/// </remarks>
public sealed class ServiceTokenIssuerBuilder
{
    private readonly Dictionary<string, ServiceClientRegistration> _clients = new(StringComparer.Ordinal);
    private readonly List<string> _audiences = [];
    private TimeSpan _lifetime = ServiceTokenDefaults.DefaultLifetime;

    internal ServiceTokenIssuerBuilder()
    {
    }

    /// <summary>
    /// Registers the calling services by id and reads their secret hashes.
    /// </summary>
    /// <remarks>
    /// Per client, <c>NOELIA_M2M_CLIENT_&lt;ID&gt;_SECRET_HASH</c> is required and
    /// <c>NOELIA_M2M_CLIENT_&lt;ID&gt;_SECRET_HASH_PREVIOUS</c> is optional; the id
    /// is upper case with hyphens as underscores. The second slot is for a
    /// rotation: while both are set, both secrets work. Empty means closed.
    /// <para>
    /// A hash that is missing, malformed or shared with another client stops the
    /// service from starting. Two clients on one secret would be one client holding
    /// the union of both clients' rights.
    /// </para>
    /// </remarks>
    /// <param name="configuration">Where the hashes are read from.</param>
    /// <param name="clientIds">
    /// Lower case, starting with a letter, up to 48 letters, digits and hyphens.
    /// </param>
    /// <exception cref="ArgumentException">An id is malformed or named twice.</exception>
    /// <exception cref="ConfigurationException">A hash is missing or malformed.</exception>
    public ServiceTokenIssuerBuilder Clients(IConfiguration configuration, params string[] clientIds)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clientIds);

        foreach (var id in clientIds)
        {
            if (!ServiceTokenFormat.IsIdentifier(id))
            {
                throw new ArgumentException(
                    "A client id starts with a lower-case letter and has up to 48 lower-case letters, "
                    + "digits and hyphens.",
                    nameof(clientIds));
            }

            if (_clients.ContainsKey(id))
            {
                throw new ArgumentException($"The client '{id}' is named twice.", nameof(clientIds));
            }

            var current = ReadRequired(configuration, ServiceTokenFormat.HashVariable(id, previous: false));
            var previous = ReadOptional(configuration, ServiceTokenFormat.HashVariable(id, previous: true));
            _clients[id] = new ServiceClientRegistration(current, previous);
        }

        return this;
    }

    /// <summary>
    /// Names the audiences tokens may be issued for.
    /// </summary>
    /// <remarks>
    /// The same set applies to every client; it is not a table of who may call
    /// what. That table lives at the receiving endpoint. This set catches a
    /// misspelt audience and refuses tokens for audiences nobody here owns.
    /// </remarks>
    /// <exception cref="ArgumentException">An audience is malformed.</exception>
    public ServiceTokenIssuerBuilder Audiences(params string[] audiences)
    {
        ArgumentNullException.ThrowIfNull(audiences);

        foreach (var audience in audiences)
        {
            if (!ServiceTokenFormat.IsIdentifier(audience))
            {
                throw new ArgumentException(
                    "An audience starts with a lower-case letter and has up to 48 lower-case letters, "
                    + "digits and hyphens.",
                    nameof(audiences));
            }

            if (!_audiences.Contains(audience))
            {
                _audiences.Add(audience);
            }
        }

        return this;
    }

    /// <summary>
    /// How long an issued token is valid. Five minutes if not called.
    /// </summary>
    /// <remarks>
    /// Short on purpose: a service token cannot be revoked, so its lifetime is how
    /// long a stolen one stays useful.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Shorter than one or longer than fifteen minutes.</exception>
    public ServiceTokenIssuerBuilder Lifetime(TimeSpan lifetime)
    {
        if (lifetime < ServiceTokenDefaults.MinimumLifetime || lifetime > ServiceTokenDefaults.MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime), "A service token lives between 1 and 15 minutes.");
        }

        _lifetime = lifetime;
        return this;
    }

    internal ServiceTokenIssuerOptions Build()
    {
        if (_clients.Count == 0)
        {
            throw new InvalidOperationException(
                "UseServiceTokenIssuer needs at least one client: name them with Clients(configuration, ids).");
        }

        if (_audiences.Count == 0)
        {
            throw new InvalidOperationException(
                "UseServiceTokenIssuer needs at least one audience: name them with Audiences(...). "
                + "An issuer that can issue for none is registered and does nothing.");
        }

        // Every non-empty hash against every other, across clients and across slots.
        // A hash that two clients share is one secret held by two services; the old
        // value left behind as PREVIOUS on several clients is exactly how that happens.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, registration) in _clients.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Claim(seen, registration.Current, $"{id} (current)");
            if (registration.Previous is not null)
            {
                Claim(seen, registration.Previous, $"{id} (previous)");
            }
        }

        return new ServiceTokenIssuerOptions
        {
            Clients = new Dictionary<string, ServiceClientRegistration>(_clients, StringComparer.Ordinal),
            Audiences = _audiences.ToArray(),
            Lifetime = _lifetime
        };
    }

    private static void Claim(Dictionary<string, string> seen, byte[] digest, string owner)
    {
        var key = Convert.ToHexString(digest);
        if (seen.TryGetValue(key, out var other))
        {
            throw new InvalidOperationException(
                $"The slots {other} and {owner} share one secret hash. Two services on one secret "
                + "are one service with both services' rights: give each its own.");
        }

        seen[key] = owner;
    }

    private static byte[] ReadRequired(IConfiguration configuration, string variable)
    {
        var text = configuration[variable];
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ConfigurationException(
                variable, "ServiceTokens",
                $"{variable} is not set. It holds the sha256: hash of the client's secret; "
                + "ServiceClientSecret.Generate(clientId).ToEnvironment() prints it.");
        }

        return Parse(text, variable);
    }

    private static byte[]? ReadOptional(IConfiguration configuration, string variable)
    {
        var text = configuration[variable];
        return string.IsNullOrWhiteSpace(text) ? null : Parse(text, variable);
    }

    private static byte[] Parse(string text, string variable)
    {
        if (ServiceTokenFormat.TryParseHash(text, out var digest))
        {
            return digest;
        }

        // The variable is named; the value is not repeated. A value that is not a
        // hash may well be a plaintext secret someone put in the wrong place.
        throw new ConfigurationException(
            variable, "ServiceTokens",
            $"{variable} is not a secret hash. Expected 'sha256:' followed by 43 base64url "
            + "characters; the plain secret does not belong here.");
    }
}

/// <summary>Adds service-token issuing to the Noelia composition.</summary>
public static class ServiceTokenIssuerModuleExtensions
{
    /// <summary>
    /// Makes this service the issuer of service-to-service tokens.
    /// </summary>
    /// <remarks>
    /// Needs <c>UseJwt(jwt =&gt; jwt.Issue(privateKey, kid))</c> in this same
    /// composition, in either order. A shared-secret or verify-only ring stops the
    /// host from starting, in every environment: with a shared secret every service
    /// that verifies tokens could mint these for any caller.
    /// <para>
    /// This registers the services; <c>MapServiceTokenEndpoint</c> exposes them.
    /// </para>
    /// </remarks>
    public static NoeliaBuilder UseServiceTokenIssuer(
        this NoeliaBuilder noelia,
        Action<ServiceTokenIssuerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(noelia);
        ArgumentNullException.ThrowIfNull(configure);

        // Run here and not when the composition is built: what needs no key ring
        // (formats, duplicates, ids) fails at the line that caused it.
        var builder = new ServiceTokenIssuerBuilder();
        configure(builder);
        var options = builder.Build();

        return noelia.Use(
            NoeliaModule.ServiceTokenIssuing,
            module => Register(module.Services, options),
            contract => contract
                .Requires<KeyRing>(new NoeliaProviderHint(
                    "Noelia.Infrastructure", "UseJwt(jwt => jwt.Issue(privateKey, kid))"))
                .Provides<IServiceTokenIssuer>(
                    "Noelia.Infrastructure", "UseServiceTokenIssuer(issuer => ...)"));
    }

    private static void Register(IServiceCollection services, ServiceTokenIssuerOptions chosen)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidateOptions<ServiceTokenIssuerOptions>, ServiceTokenIssuerOptionsValidator>();

        // ValidateOnStart so a ring that cannot sign refuses the host at startup, not
        // the first service that asks for a token.
        services.AddOptions<ServiceTokenIssuerOptions>()
            .Configure(options =>
            {
                options.Clients = chosen.Clients;
                options.Audiences = chosen.Audiences;
                options.Lifetime = chosen.Lifetime;
            })
            .ValidateOnStart();

        services.AddSingleton<IServiceTokenIssuer, ServiceTokenIssuer>();
    }
}
