using Noelia.Abstractions.Compliance;
using Noelia.Abstractions.Hosting;

namespace Noelia.Abstractions.Security.Checks;

/// <summary>The stable outcome vocabulary for every Noelia security check.</summary>
public enum SecurityCheckStatus
{
    Pass,
    Warning,
    Fail,
    NotApplicable
}

/// <summary>How urgently an operator should act on a non-passing result.</summary>
public enum SecurityCheckSeverity
{
    Informational,
    Low,
    Medium,
    High,
    Critical
}

/// <summary>The security boundary a check examines.</summary>
public enum SecurityCheckCategory
{
    Composition,
    Authentication,
    Browser,
    Session,
    Secrets,
    Encryption,
    AbusePrevention,
    Revocation,

    /// <summary>
    /// What the service does with artificial intelligence, and what that
    /// obliges its operator to keep.
    /// </summary>
    /// <remarks>
    /// Its own boundary rather than a case of <see cref="Secrets"/> or
    /// <see cref="Encryption"/>, because the duties attached to it come from a
    /// different law and land on a different desk. An operator who wants to
    /// know what the AI Act asks of them should be able to read one category.
    /// </remarks>
    ArtificialIntelligence
}

/// <summary>A value-free security finding safe to show in operator tooling.</summary>
/// <remarks>
/// Summary and remediation describe shapes and actions only. Implementations
/// must never copy configuration values, keys, tokens, connection strings or
/// raw exception messages into either field.
/// </remarks>
public sealed record SecurityCheckResult(
    string Id,
    NoeliaModule Module,
    SecurityCheckCategory Category,
    SecurityCheckStatus Status,
    SecurityCheckSeverity Severity,
    string Summary,
    string Remediation)
{
    /// <summary>
    /// The obligations this result is evidence for, if any.
    /// </summary>
    /// <remarks>
    /// <para>Optional, and empty for most checks. A citation belongs on a check
    /// only where the article genuinely asks about the thing the check
    /// measures — attaching one to every result would turn the mapping into
    /// decoration, and a reader who finds one decorative citation stops
    /// trusting the rest.</para>
    ///
    /// <para>Declared by the check itself rather than looked up from a table
    /// keyed by <see cref="Id"/>, so that renaming a check cannot silently
    /// detach it from the obligation it was written for.</para>
    /// </remarks>
    public IReadOnlyList<RegulatoryReference> References { get; init; } = [];
}

/// <summary>One explicitly executable security assertion.</summary>
public interface ISecurityCheck
{
    string Id { get; }
    NoeliaModule Module { get; }
    SecurityCheckCategory Category { get; }
    SecurityCheckSeverity Severity { get; }
    string Remediation { get; }

    /// <summary>
    /// The obligations this check produces evidence for. Empty by default.
    /// </summary>
    IReadOnlyList<RegulatoryReference> References => [];

    Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Runs checks for modules in the actual composition.</summary>
public interface ISecurityCheckRunner
{
    Task<IReadOnlyList<SecurityCheckResult>> RunAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>The latest completed startup or operator-invoked run.</summary>
public interface ISecurityCheckReport
{
    IReadOnlyList<SecurityCheckResult> Latest { get; }
}

/// <summary>Execution limits for security checks.</summary>
public sealed class SecurityCheckOptions
{
    /// <summary>Maximum runtime for one check. A hung provider cannot hang startup.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}
