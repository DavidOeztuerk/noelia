using Noelia.Core.Identity;
using Noelia.Infrastructure.Security.ServiceTokens;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Noelia.Infrastructure.Security.Identity;

/// <summary>
/// Translates the authenticated claims of a request into a
/// <see cref="Principal"/> once, and stores it for the rest of the pipeline.
/// </summary>
/// <remarks>
/// Register after authentication and before authorization. A request whose
/// claims cannot be translated is answered with 401 rather than continuing
/// without a principal.
/// <para>
/// A service is not a person and gets no principal: the identity the service scheme
/// authenticated is taken out here, ahead of the factory, so that a factory a service
/// wrote itself needs no knowledge of it. Behind a door that leaves nothing to
/// translate and <see cref="ICurrentPrincipal.Current"/> stays <c>null</c>; a person
/// authenticated beside the service is translated as usual (ADR-0003).
/// </para>
/// </remarks>
public sealed class PrincipalMiddleware
{
    internal const string ItemKey = "Noelia.Principal";

    private readonly RequestDelegate _next;
    private readonly IPrincipalFactory _factory;
    private readonly ILogger<PrincipalMiddleware> _logger;

    public PrincipalMiddleware(
        RequestDelegate next,
        IPrincipalFactory factory,
        ILogger<PrincipalMiddleware> logger)
    {
        _next = next;
        _factory = factory;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var result = _factory.Create(ServiceCallerIdentity.PersonPartOf(context.User));

        if (result.IsInvalid)
        {
            _logger.LogWarning("Rejecting request: {Reason}", result.Error);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (result.Principal is { } principal)
        {
            context.Items[ItemKey] = principal;
        }

        await _next(context);
    }
}
