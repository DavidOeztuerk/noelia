using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Communication.Configuration;
using Noelia.Infrastructure.Security.M2M;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Builder;

/// <summary>Adds the calling side of service tokens to the composition.</summary>
public static class ServiceTokenClientModuleExtensions
{
    internal const string EndpointVariable = "NOELIA_M2M_TOKEN_ENDPOINT";
    internal const string ClientIdVariable = "NOELIA_M2M_CLIENT_ID";
    internal const string SecretVariable = "NOELIA_M2M_CLIENT_SECRET";

    /// <summary>
    /// Makes this service fetch tokens for the services it calls, with the client
    /// credentials it was given.
    /// </summary>
    /// <remarks>
    /// Reads <c>NOELIA_M2M_TOKEN_ENDPOINT</c>, <c>NOELIA_M2M_CLIENT_ID</c> and
    /// <c>NOELIA_M2M_CLIENT_SECRET</c> and switches the M2M configuration to
    /// <see cref="ServiceTokenProtocol.ClientCredentials"/>. That is deliberately not
    /// overridable from the <c>ServiceCommunication</c> section: a module that is composed
    /// means what it says, in whatever order <c>AddServiceCommunication</c> runs. A
    /// missing or malformed variable stops the host from starting; the message names the
    /// variable and never its value.
    /// <para>
    /// The audience is not configured here. It is the service being called:
    /// <c>AddServiceToken("identity")</c> on an <c>HttpClient</c>, or the target's name for
    /// <c>IServiceCommunicationManager</c>. This registers the token source; the client
    /// still opts in per call path.
    /// </para>
    /// </remarks>
    public static NoeliaBuilder UseServiceTokenClient(this NoeliaBuilder noelia)
    {
        ArgumentNullException.ThrowIfNull(noelia);

        return noelia.Use(
            NoeliaModule.ServiceTokenClient,
            module => Register(module.Services, module.Configuration),
            contract => contract
                .Provides<IServiceTokenSource>(
                    "Noelia.Infrastructure", "UseServiceTokenClient()"));
    }

    private static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = Read(configuration, EndpointVariable);
        var clientId = Read(configuration, ClientIdVariable);
        var secret = Read(configuration, SecretVariable);

        ServiceTokenClientRegistration.Add(services);

        // Post-configure: it runs after the section is bound, whichever of the two
        // registrations came first, so the order of two lines cannot change the protocol.
        services.AddOptions<ServiceCommunicationOptions>()
            .PostConfigure(options =>
            {
                options.M2M.Enabled = true;
                options.M2M.Protocol = ServiceTokenProtocol.ClientCredentials;
                options.M2M.TokenEndpoint = endpoint;
                options.M2M.ClientId = clientId;
                options.M2M.ClientSecret = secret;
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ServiceCommunicationOptions>, ServiceTokenClientOptionsValidator>();
    }

    private static string? Read(IConfiguration configuration, string variable)
    {
        var value = configuration[variable];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Checks the three values when the host starts. The builder cannot: the options are
    /// finished only after every registration has had its say.
    /// </summary>
    internal sealed class ServiceTokenClientOptionsValidator : IValidateOptions<ServiceCommunicationOptions>
    {
        public ValidateOptionsResult Validate(string? name, ServiceCommunicationOptions options)
        {
            var m2m = options.M2M;
            var failures = new List<string>();

            if (string.IsNullOrEmpty(m2m.TokenEndpoint))
            {
                failures.Add($"{EndpointVariable} is not set. It is the URL of the token endpoint of the issuing service.");
            }
            else if (!Uri.TryCreate(m2m.TokenEndpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add($"{EndpointVariable} is not an absolute http or https URL.");
            }

            if (string.IsNullOrEmpty(m2m.ClientId))
            {
                failures.Add($"{ClientIdVariable} is not set. It is this service's client id, as the issuer knows it.");
            }
            else if (!ServiceTokenFormat.IsIdentifier(m2m.ClientId))
            {
                failures.Add(
                    $"{ClientIdVariable} is not a client id: lower case, starting with a letter, "
                    + "up to 48 letters, digits and hyphens.");
            }

            if (string.IsNullOrEmpty(m2m.ClientSecret))
            {
                failures.Add(
                    $"{SecretVariable} is not set. ServiceClientSecret.Generate(clientId).ToEnvironment() prints it.");
            }
            else if (!ServiceClientSecret.IsWellFormed(m2m.ClientSecret))
            {
                // The shape is described; the value is not repeated. A value that is not a
                // secret may well be the issuer's hash, or a secret meant for another place.
                failures.Add(
                    $"{SecretVariable} is not a service client secret: 43 base64url characters. "
                    + "The sha256: hash belongs to the issuer, not here.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
