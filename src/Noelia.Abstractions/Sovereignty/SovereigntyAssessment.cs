namespace Noelia.Abstractions.Sovereignty;

/// <summary>
/// How much can be said about where a dependency runs.
/// </summary>
public enum Jurisdiction
{
    /// <summary>
    /// Loopback, a private network, or a name reserved for internal use. Runs on
    /// infrastructure the operator controls.
    /// </summary>
    SelfHosted,

    /// <summary>
    /// A public name whose operator and location cannot be told from the name
    /// alone. Most third-party and most European providers land here, and the
    /// operator has to answer it.
    /// </summary>
    Undetermined,

    /// <summary>
    /// A domain belonging to a provider subject to third-country access laws,
    /// such as the US CLOUD Act. Recognised by name, not by contract: a regional
    /// endpoint of such a provider is still that provider.
    /// </summary>
    ThirdCountryProvider
}

/// <summary>
/// What a dependency is, where that changes who has to answer for it.
/// </summary>
/// <remarks>
/// A separate axis from <see cref="Jurisdiction"/> on purpose: jurisdiction
/// answers <em>where</em>, this answers <em>what</em>. An inference endpoint in
/// Frankfurt and a mail relay in Frankfurt sit in the same jurisdiction and
/// carry different duties.
/// </remarks>
public enum DependencyKind
{
    /// <summary>Nothing about the destination suggests a particular regime.</summary>
    Ordinary,

    /// <summary>
    /// A model or inference endpoint, recognised by name.
    /// </summary>
    /// <remarks>
    /// Recognition is by host, so it finds the SDK someone added last week and
    /// misses a self-hosted model behind an ordinary name. Both directions are
    /// stated where the finding is shown, because a list that looks complete
    /// and is not is worse than one that admits its edge.
    /// </remarks>
    ArtificialIntelligence
}

/// <summary>What is known about one outbound dependency.</summary>
/// <param name="Name">What it is used for, such as "Database" or "Secrets".</param>
/// <param name="Host">The host, without credentials.</param>
/// <param name="Jurisdiction">What the host name allows us to conclude.</param>
/// <param name="Note">Why it was classified that way.</param>
public sealed record DependencyFinding(
    string Name,
    string? Host,
    Jurisdiction Jurisdiction,
    string Note)
{
    /// <summary>What kind of destination this is.</summary>
    public DependencyKind Kind { get; init; } = DependencyKind.Ordinary;
}

/// <summary>
/// What the running configuration says about where data can go.
/// </summary>
/// <remarks>
/// This is a report, not a guarantee. A host name cannot prove a jurisdiction,
/// and no library can. What it does is make every configured destination
/// visible in one place, so an unnoticed default becomes a decision.
/// </remarks>
public sealed record SovereigntyAssessment(
    IReadOnlyList<DependencyFinding> Dependencies,
    IReadOnlyCollection<string> DeclaredEgressHosts,
    bool EgressIsEnforced)
{
    /// <summary>Dependencies on providers subject to third-country access laws.</summary>
    public IReadOnlyList<DependencyFinding> ThirdCountryDependencies =>
        [.. Dependencies.Where(d => d.Jurisdiction == Jurisdiction.ThirdCountryProvider)];

    /// <summary>Dependencies whose operator cannot be told from the host name.</summary>
    public IReadOnlyList<DependencyFinding> UndeterminedDependencies =>
        [.. Dependencies.Where(d => d.Jurisdiction == Jurisdiction.Undetermined)];

    /// <summary>Dependencies that are model or inference endpoints.</summary>
    /// <remarks>
    /// The inventory an AI-Act deployer needs before they can answer anything
    /// else. Empty is a real answer here, and the common one.
    /// </remarks>
    public IReadOnlyList<DependencyFinding> ArtificialIntelligenceDependencies =>
        [.. Dependencies.Where(d => d.Kind == DependencyKind.ArtificialIntelligence)];

    /// <summary>
    /// True when nothing configured points at a provider known to be subject to
    /// third-country access laws. It says nothing about the undetermined ones.
    /// </summary>
    public bool NoKnownThirdCountryDependency => ThirdCountryDependencies.Count == 0;
}
