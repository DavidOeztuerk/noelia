using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Noelia.Infrastructure.Security.ServiceTokens;

internal sealed class ServiceCallerAuthorizationResultHandler(
    IAuthorizationMiddlewareResultHandler inner, ILoggerFactory loggerFactory) : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        _ = (inner, loggerFactory);
        throw new NotImplementedException();
    }
}
