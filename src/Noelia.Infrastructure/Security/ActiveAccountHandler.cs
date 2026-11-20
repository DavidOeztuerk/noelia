using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Security;

/// <summary>
/// Handler for active account requirement
/// </summary>
/// <remarks>
/// At least one signed-in person identity has to be there, and every one of them has to say
/// <c>account_status=Active</c> itself: an identity that says nothing is a "no", never a "yes"
/// (ADR-0007). No person at all fails the requirement, also when a policy points at this
/// handler without <c>RequireAuthenticatedUser</c>.
/// </remarks>
public class ActiveAccountHandler(
    ILogger<ActiveAccountHandler> logger)
    : AuthorizationHandler<ActiveAccountRequirement>
{
    private readonly ILogger<ActiveAccountHandler> _logger = logger;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveAccountRequirement requirement)
    {
        var persons = ServiceCallerIdentity.PersonsOf(context.User).ToList();

        if (persons.Count == 0)
        {
            _logger.LogWarning("Active account check failed: no signed-in person");
            context.Fail();
            return Task.CompletedTask;
        }

        foreach (var person in persons)
        {
            var accountStatus = person.FindFirst("account_status")?.Value;

            if (!string.Equals(accountStatus, "Active", StringComparison.OrdinalIgnoreCase))
            {
                var userId = person.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "Unknown";
                _logger.LogWarning("Active account check failed for user {UserId} with status {Status}",
                    userId, accountStatus);
                context.Fail();
                return Task.CompletedTask;
            }
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
