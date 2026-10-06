using Noelia.Abstractions.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Noelia.Infrastructure.Security;

/// <summary>
/// Refuses requests whose access token has been revoked.
/// </summary>
/// <remarks>
/// Reads <c>jti</c>, <c>sub</c>, <c>iat</c> and — when the issuer sets it —
/// <c>session_id</c> (or the standard <c>sid</c>, see <see cref="SessionClaims"/>) from the authenticated principal and asks the registered
/// <see cref="ITokenRevocationEvaluator"/>. A token missing any of the first
/// three cannot be checked and is refused: an unidentifiable token must not be
/// the one that slips through.
/// </remarks>
public class TokenRevocationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITokenRevocationEvaluator _evaluator;
    private readonly ILogger<TokenRevocationMiddleware> _logger;

    public TokenRevocationMiddleware(
        RequestDelegate next,
        ITokenRevocationEvaluator evaluator,
        ILogger<TokenRevocationMiddleware> logger)
    {
        _next = next;
        _evaluator = evaluator;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        if (!TryReadToken(context.User, out var token))
        {
            _logger.LogWarning(
                "Authenticated principal without jti/sub/iat claims on {Path}; refusing.",
                context.Request.Path);

            await Refuse(context, "Token cannot be checked for revocation");
            return;
        }

        var verdict = await _evaluator.EvaluateAsync(token, context.RequestAborted);

        if (verdict.IsStale)
        {
            _logger.LogWarning(
                "Revocation verdict for {TokenId} came from stale state ({Reason}).",
                token.TokenId, verdict.Reason);
        }

        if (!verdict.IsRevoked)
        {
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "Blocked revoked token: jti={TokenId}, sub={SubjectId}, reason={Reason}, path={Path}",
            token.TokenId, token.SubjectId, verdict.Reason, context.Request.Path);

        await Refuse(context, "Token has been revoked");
    }

    private static bool TryReadToken(ClaimsPrincipal user, out TokenIdentity token)
    {
        token = default;

        var jti = user.FindFirst("jti")?.Value;
        var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        var iat = user.FindFirst("iat")?.Value;

        if (string.IsNullOrEmpty(jti) || string.IsNullOrEmpty(sub)
            || !long.TryParse(iat, out var issuedAtSeconds))
        {
            return false;
        }

        token = new TokenIdentity
        {
            TokenId = jti,
            SubjectId = sub,
            IssuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds),
            SessionId = SessionClaims.Read(user)
        };

        return true;
    }

    private static Task Refuse(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsync(message);
    }
}

public static class TokenRevocationMiddlewareExtensions
{
    /// <summary>
    /// Adds the revocation check to the pipeline. Place it after authentication.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="ITokenRevocationEvaluator"/> is registered. Register a
    /// provider, or call <c>AddNoTokenRevocation(rationale)</c> to state that
    /// this deployment does without the check. There is no silent default: a
    /// revocation check that always answers "not revoked" is indistinguishable
    /// from one that works.
    /// </exception>
    public static IApplicationBuilder UseTokenRevocation(this IApplicationBuilder builder)
    {
        if (builder.ApplicationServices.GetService<ITokenRevocationEvaluator>() is null)
        {
            throw new InvalidOperationException(
                "UseTokenRevocation() without a registered ITokenRevocationEvaluator. "
                + "Register a provider (for example the Redis store) or call "
                + "AddNoTokenRevocation(rationale) to turn the check off deliberately.");
        }

        return builder.UseMiddleware<TokenRevocationMiddleware>();
    }
}
