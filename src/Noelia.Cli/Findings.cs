namespace Noelia.Cli;

/// <summary>How much a finding matters.</summary>
public enum FindingSeverity
{
    /// <summary>Worth knowing. Nothing is wrong.</summary>
    Note,

    /// <summary>Probably not what was intended.</summary>
    Warning,

    /// <summary>Something promised is not happening.</summary>
    Problem
}

/// <summary>One thing the analysis found.</summary>
/// <param name="Id">A stable dotted id, so a finding can be suppressed or tracked.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Where">The file, endpoint or package it was found in.</param>
/// <param name="What">What was found, in one sentence.</param>
/// <param name="Do">What would resolve it.</param>
public sealed record Finding(
    string Id,
    FindingSeverity Severity,
    string Where,
    string What,
    string Do);

/// <summary>What one analysis run saw.</summary>
/// <param name="Source">Where it read from — a directory or a URL.</param>
/// <param name="Kind">
/// <c>static</c> or <c>live</c>. A reader has to know which, because a static
/// reading sees only literals and says so.
/// </param>
/// <param name="Findings">What it found, most serious first.</param>
/// <param name="Observations">What it read, for a reader who wants the basis.</param>
public sealed record AnalysisResult(
    string Source,
    string Kind,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Observations)
{
    /// <summary>
    /// The exit code a script should act on.
    /// </summary>
    /// <remarks>
    /// The convention scanners settled on, because build pipelines already
    /// encode it: 0 is clean, 1 is findings, and a crash is something else
    /// entirely. A tool that exits 0 on findings gets wired into a gate that
    /// then never fails.
    /// </remarks>
    public int ExitCode =>
        Findings.Any(finding => finding.Severity is FindingSeverity.Problem) ? 1 : 0;
}

/// <summary>
/// The provider calls that must be made through the composition, and the call
/// that makes them.
/// </summary>
/// <remarks>
/// <para>This is the defect this repository keeps finding in itself. A provider
/// registered with <c>AddRedisCache(...)</c> beside <c>AddNoelia(...)</c> is
/// registered <em>before</em> the built-in modules run, and they register their
/// own during <c>Build()</c> — so the later registration wins and nothing says
/// the earlier one was overwritten. The service is present, the provider is
/// not effective, and no error is raised anywhere.</para>
///
/// <para>Hardcoded here and checked against the shipped assemblies by a test in
/// the library's suite, so a pair added to Noelia without being added here
/// fails that build rather than silently going unchecked.</para>
/// </remarks>
public static class ProviderPairs
{
    /// <summary>The eager call, and the one that composes instead.</summary>
    public static IReadOnlyList<(string Eager, string Composed)> All { get; } =
    [
        ("AddRedisCache", "UseRedisCache"),
        ("AddInMemoryCache", "UseInMemoryCache"),
        ("AddRedisEncryption", "UseRedisEncryption"),
        ("AddRedisSecurityAudit", "UseRedisSecurityAudit"),
        ("AddRedisSovereignAudit", "UseRedisSovereignAudit"),
        ("AddRedisTokenRevocation", "UseRedisTokenRevocation"),
        ("AddInMemoryRefreshTokens", "UseInMemoryRefreshTokens"),
        ("AddBCryptPasswords", "UseBCryptPasswords"),
        ("AddArgon2Passwords", "UseArgon2Passwords")
    ];
}
