namespace Noelia.Abstractions.Sovereignty;

/// <summary>Reports the registered factory HTTP guard's configuration, not observed traffic.</summary>
/// <remarks>
/// Register only with the factory guard, never merely with a policy object.
/// This port does not prove that application code uses the factory, that its
/// handlers were not replaced, or that other transports are confined.
/// </remarks>
public interface IHttpEgressPolicyReport
{
    /// <summary>
    /// Reads the guard's current configuration.
    /// </summary>
    /// <returns>
    /// Whether the allow policy restricts requests, and the host patterns and
    /// network categories it permits. It must not include credentials, request
    /// contents or exception text, and it reports what is configured, not what
    /// was observed. The caller treats an exception as "could not be read", not
    /// as "not enforcing".
    /// </returns>
    HttpEgressPolicyAssessment Assess();
}

/// <summary>Configuration of the HTTP factory guard; no credentials or request contents.</summary>
/// <param name="IsEnforcing">Whether its allow policy restricts requests.</param>
/// <param name="AllowedTargets">Host patterns and network categories, not dependency declarations.</param>
public sealed record HttpEgressPolicyAssessment(bool IsEnforcing, IReadOnlyList<string> AllowedTargets);
