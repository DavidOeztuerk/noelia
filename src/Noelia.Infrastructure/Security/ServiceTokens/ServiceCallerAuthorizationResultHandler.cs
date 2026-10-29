using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// Answers a refused request to a service door with the 404 of a path that does not
/// exist, and hands every other policy to the handler it wraps.
/// </summary>
/// <remarks>
/// Only the status code is set. No body, no header, and neither challenge nor forbid:
/// those would write <c>WWW-Authenticate</c> and Noelia's 401 document, and the door
/// would announce itself. Because nothing more is written, whatever sits further out
/// (status code pages, problem details) treats the refusal like any other 404.
/// <para>
/// "Every other policy" means a policy without a <see cref="ServiceCallerRequirement"/>.
/// An endpoint that asks for a login and for a service caller is a door, and a failure
/// of the login is the same 404.
/// </para>
/// </remarks>
internal sealed class ServiceCallerAuthorizationResultHandler(
    IAuthorizationMiddlewareResultHandler inner,
    ILoggerFactory loggerFactory) : IAuthorizationMiddlewareResultHandler
{
    private readonly ServiceCallerAudit _audit = new(loggerFactory);

    public Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        var doors = policy.Requirements.OfType<ServiceCallerRequirement>().ToArray();
        if (doors.Length == 0)
        {
            return inner.HandleAsync(next, context, policy, authorizeResult);
        }

        var caller = ServiceCallerIdentity.CallerOf(context.User);

        if (authorizeResult.Succeeded)
        {
            _audit.Allowed(context, caller ?? "unknown");
            return next(context);
        }

        if (caller is null)
        {
            _audit.Denied(context, ServiceCallerAudit.ReasonWithoutIdentity(context), caller: null);
        }
        else
        {
            var listed = doors.All(door => door.Callers.Contains(caller));
            _audit.Denied(context, listed ? ServiceCallerAudit.OtherPolicy : ServiceCallerAudit.CallerNotAllowed, caller);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
