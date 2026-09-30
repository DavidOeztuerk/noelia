using Noelia.Abstractions.Sovereignty;

namespace Noelia.Infrastructure.Sovereignty;

// Created by the same registration that installs the handler. Reading the
// resolved policy keeps this aligned with the policy that the handler consumes.
internal sealed class HttpEgressPolicyReport(IEgressPolicy policy) : IHttpEgressPolicyReport
{
    public HttpEgressPolicyAssessment Assess() => new(policy.IsEnforcing,
        [.. policy.DeclaredHosts.Order(StringComparer.Ordinal)]);
}
