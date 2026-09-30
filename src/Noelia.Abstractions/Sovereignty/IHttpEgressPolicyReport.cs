namespace Noelia.Abstractions.Sovereignty;

/// <summary>Reports the registered factory HTTP guard's configuration, not observed traffic.</summary>
/// <remarks>
/// Register only with the factory guard, never merely with a policy object.
/// This port does not prove that application code uses the factory, that its
/// handlers were not replaced, or that other transports are confined.
/// </remarks>
public interface IHttpEgressPolicyReport
{
    HttpEgressPolicyAssessment Assess();
}

/// <summary>Configuration of the HTTP factory guard; no credentials or request contents.</summary>
/// <param name="IsEnforcing">Whether its allow policy restricts requests.</param>
/// <param name="AllowedTargets">Host patterns and network categories, not dependency declarations.</param>
public sealed record HttpEgressPolicyAssessment(bool IsEnforcing, IReadOnlyList<string> AllowedTargets);
