using Noelia.Core.Identity;
using Noelia.Infrastructure.Security.ServiceTokens;
using Microsoft.AspNetCore.Authorization;

namespace Noelia.Infrastructure.Security.Identity;

/// <summary>Requires the caller to be acting for a company.</summary>
public sealed class ActingForCompanyRequirement : IAuthorizationRequirement;

/// <summary>Requires the caller to be acting for themselves.</summary>
public sealed class ActingAsSelfRequirement : IAuthorizationRequirement;

/// <summary>
/// Succeeds when the user being authorized acts for a company.
/// </summary>
/// <remarks>
/// The handler translates <see cref="AuthorizationHandlerContext.User"/> itself, exactly as
/// <see cref="PrincipalMiddleware"/> does, and never reads <see cref="ICurrentPrincipal"/>:
/// behind <c>UseAuth()</c> authorization runs before the principal step has stored anything,
/// so the order of the two steps must not decide the answer (ADR-0006). A service identity is
/// taken out first and is never a person (ADR-0003). It never fails the requirement and
/// writes nothing to <c>HttpContext.Items</c>.
/// </remarks>
public sealed class ActingForCompanyHandler : AuthorizationHandler<ActingForCompanyRequirement>
{
    private readonly IPrincipalFactory _factory;

    /// <summary>
    /// Kept for source and binary compatibility. The argument is <b>no longer read</b>: the
    /// user of the authorization context is translated instead, so a replaced or faked
    /// <see cref="ICurrentPrincipal"/> does not satisfy the policy; put the claims in the user.
    /// </summary>
    /// <param name="principal">Ignored.</param>
    public ActingForCompanyHandler(ICurrentPrincipal principal)
        : this(principal, new PrincipalFactory())
    {
    }

    /// <summary>
    /// The constructor the container uses, with the factory it holds.
    /// </summary>
    /// <param name="principal">
    /// Ignored. It is here only because two single-argument constructors,
    /// <c>(ICurrentPrincipal)</c> and <c>(IPrincipalFactory)</c>, would be ambiguous for the
    /// container.
    /// </param>
    /// <param name="factory">Translates the claims of the user into a principal.</param>
    public ActingForCompanyHandler(ICurrentPrincipal principal, IPrincipalFactory factory)
    {
        _ = principal;
        _factory = factory;
    }

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActingForCompanyRequirement requirement)
    {
        var result = _factory.Create(ServiceCallerIdentity.PersonPartOf(context.User));
        if (result.Principal?.Acting is Capacity.ForCompany)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Succeeds when the user being authorized acts for themselves.
/// </summary>
/// <remarks>
/// Reads the user of the authorization context, see <see cref="ActingForCompanyHandler"/>.
/// </remarks>
public sealed class ActingAsSelfHandler : AuthorizationHandler<ActingAsSelfRequirement>
{
    private readonly IPrincipalFactory _factory;

    /// <summary>
    /// Kept for source and binary compatibility. The argument is <b>no longer read</b>: the
    /// user of the authorization context is translated instead, so a replaced or faked
    /// <see cref="ICurrentPrincipal"/> does not satisfy the policy; put the claims in the user.
    /// </summary>
    /// <param name="principal">Ignored.</param>
    public ActingAsSelfHandler(ICurrentPrincipal principal)
        : this(principal, new PrincipalFactory())
    {
    }

    /// <summary>
    /// The constructor the container uses, with the factory it holds.
    /// </summary>
    /// <param name="principal">
    /// Ignored. It is here only because two single-argument constructors,
    /// <c>(ICurrentPrincipal)</c> and <c>(IPrincipalFactory)</c>, would be ambiguous for the
    /// container.
    /// </param>
    /// <param name="factory">Translates the claims of the user into a principal.</param>
    public ActingAsSelfHandler(ICurrentPrincipal principal, IPrincipalFactory factory)
    {
        _ = principal;
        _factory = factory;
    }

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActingAsSelfRequirement requirement)
    {
        var result = _factory.Create(ServiceCallerIdentity.PersonPartOf(context.User));
        if (result.Principal?.Acting is Capacity.AsSelf)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
