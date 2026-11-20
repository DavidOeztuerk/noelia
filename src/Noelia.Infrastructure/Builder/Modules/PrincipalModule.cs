using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Security.Identity;
using Microsoft.AspNetCore.Builder;

namespace Noelia.Infrastructure.Builder.Modules;

/// <summary>
/// Turns the verified claims of a request into a
/// <see cref="Noelia.Core.Identity.Principal"/> the application can read.
/// </summary>
public static class PrincipalModule
{
    /// <summary>
    /// Registers principal resolution and the capacity policies. Pair it with
    /// <see cref="UsePrincipal"/>.
    /// </summary>
    /// <remarks>
    /// Independent of <c>AddJwtAuthentication()</c>: a service may verify
    /// tokens without ever building a principal — a gateway that only routes
    /// does exactly that — and a service may build one from claims another
    /// scheme established.
    /// </remarks>
    public static InfrastructureBuilder AddPrincipal(this InfrastructureBuilder builder)
    {
        builder.Services.AddNoeliaIdentity();
        return builder;
    }

    /// <summary>
    /// Resolves the principal once per request.
    /// </summary>
    /// <remarks>
    /// Belongs after <c>UseAuth()</c>: it reads the claims authentication
    /// established, and placed before it every request looks anonymous. It sets the
    /// principal for the steps and endpoints that follow. The capacity policies
    /// (<c>ActingForCompany</c>, <c>ActingAsSelf</c>) do not need it: they translate the
    /// request's user themselves, so they work wherever the step stands (ADR-0006).
    /// </remarks>
    public static InfrastructureMiddlewareBuilder UsePrincipal(
        this InfrastructureMiddlewareBuilder builder) =>
        builder.Step(NoeliaModule.Principal, step =>
        {
            step.Requires<IPrincipalFactory>("UsePrincipal()", "AddPrincipal()");
            step.App.UseNoeliaPrincipal();
        });
}
