using Microsoft.AspNetCore.Authorization;

namespace Noelia.Infrastructure.Security.ServiceTokens;

internal sealed class ServiceCallerHandler : AuthorizationHandler<ServiceCallerRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ServiceCallerRequirement requirement) =>
        throw new NotImplementedException();
}
