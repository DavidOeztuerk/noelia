using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Security;

/// <summary>
/// Handler for email verification requirement
/// </summary>
/// <remarks>
/// At least one signed-in person identity has to be there, and every one of them has to say
/// <c>email_verified=true</c> itself; a missing statement is a "no" (ADR-0007). No person at all
/// fails the requirement, also when a policy points at this handler without
/// <c>RequireAuthenticatedUser</c>.
/// </remarks>
public class EmailVerifiedHandler(
    ILogger<EmailVerifiedHandler> logger)
    : AuthorizationHandler<EmailVerifiedRequirement>
{
    private readonly ILogger<EmailVerifiedHandler> _logger = logger;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        EmailVerifiedRequirement requirement)
    {
        var persons = ServiceCallerIdentity.PersonsOf(context.User).ToList();

        if (persons.Count == 0)
        {
            _logger.LogWarning("Email verification check failed: no signed-in person");
            context.Fail();
            return Task.CompletedTask;
        }

        foreach (var person in persons)
        {
            var emailVerifiedClaim = person.FindFirst("email_verified")?.Value;

            if (!(bool.TryParse(emailVerifiedClaim, out var isEmailVerified) && isEmailVerified))
            {
                var userId = person.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "Unknown";
                _logger.LogWarning("Email verification check failed for user {UserId}", userId);
                context.Fail();
                return Task.CompletedTask;
            }
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
