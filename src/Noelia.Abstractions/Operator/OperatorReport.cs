using System.Text.Json.Serialization;

namespace Noelia.Abstractions.Operator;

/// <summary>
/// Everything the operator view knows about one running instance, as data.
/// </summary>
/// <remarks>
/// <para>The dashboard renders this; so does the JSON endpoint. That is the
/// whole point of the type existing. Until 6.0.0 the page wrote HTML straight
/// out of the registered services, so a second reader — a fleet view, a
/// verifier, anything not a browser — had no choice but to query those services
/// again along a second code path. Two paths for one statement drift, and they
/// drift silently, because nobody looks at both at once.</para>
///
/// <para>This describes <strong>one instance at one moment</strong>. It carries
/// no history and makes no claim about any other replica, because a process
/// cannot honestly make either. Aggregating across a fleet and remembering what
/// changed belong to whatever collects these.</para>
///
/// <para><strong>Nothing here is a secret.</strong> Configuration values,
/// tokens, keys and connection strings never enter this report — the same rule
/// the rendered page follows. Shapes, names and fingerprints only.</para>
/// </remarks>
public sealed record OperatorReport
{
    /// <summary>The shape of this document, so a reader need not guess.</summary>
    /// <remarks>
    /// <para>A fleet view reads instances running different Noelia versions side
    /// by side. Without a version it has to infer the format from its contents,
    /// and inference about a format is where quiet misreading starts.</para>
    ///
    /// <para>Version 2 added regulatory references to a check result and a kind
    /// to a dependency. Both are additions with defaults, so a reader written
    /// for version 1 still parses a version 2 document and simply sees no
    /// citations — which is why the number went up rather than the shape
    /// changing.</para>
    /// </remarks>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>When this report was taken.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The composed service name.</summary>
    public string Service { get; init; } = string.Empty;

    /// <summary>The instance, usually the host name of this process.</summary>
    public string Instance { get; init; } = string.Empty;

    /// <summary>The environment this instance believes it is running in.</summary>
    public string Environment { get; init; } = string.Empty;

    /// <summary>
    /// The deployment this instance belongs to, or <c>null</c> when none was
    /// configured.
    /// </summary>
    /// <remarks>
    /// Without it a collector has seventeen services and no way to tell that
    /// they are one system. Set through <c>Dashboard:Fleet</c>; it is a label
    /// the operator chooses, and Noelia never invents one.
    /// </remarks>
    public string? Fleet { get; init; }

    /// <summary>Which modules run, and what each one promised.</summary>
    public CompositionView Composition { get; init; } = new();

    /// <summary>Configuration keys and value shapes — never values.</summary>
    public ConfigurationView Configuration { get; init; } = new();

    /// <summary>The latest run of the executable security assertions.</summary>
    public SecurityCheckView SecurityChecks { get; init; } = new();

    /// <summary>Where this instance is allowed to reach, and under whose law.</summary>
    public SovereigntyView Sovereignty { get; init; } = new();

    /// <summary>The audit trail as this instance can account for it.</summary>
    public AuditView Audit { get; init; } = new();

    /// <summary>Sessions this instance has observed.</summary>
    public SessionView Sessions { get; init; } = new();

    /// <summary>Rate-limit counters the active store will show.</summary>
    public RateLimitView RateLimits { get; init; } = new();

    /// <summary>Readiness and liveness as the registered checks answer it.</summary>
    public HealthView Health { get; init; } = new();
}

/// <summary>
/// Why a section carries no data — or that it does.
/// </summary>
/// <remarks>
/// Four states and not two, because the difference between them is the finding.
/// A service with no rate-limit store and a service whose store stopped
/// answering look identical in a report that only knows "empty", and they call
/// for opposite reactions: one is a composition decision, the other is an
/// outage.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OperatorSectionState>))]
public enum OperatorSectionState
{
    /// <summary>Nothing of this kind is registered. Usually a decision.</summary>
    Absent,

    /// <summary>
    /// Registered, but this provider exposes no read model an operator may see.
    /// </summary>
    Unavailable,

    /// <summary>Registered, and reading it failed. Always a finding.</summary>
    Faulted,

    /// <summary>Data follows.</summary>
    Present
}

/// <summary>The modules that make up this instance, and their contracts.</summary>
public sealed record CompositionView
{
    /// <summary>Every module, running or deliberately left out.</summary>
    public IReadOnlyList<ModuleView> Modules { get; init; } = [];

    /// <summary>What each running module requires and provides.</summary>
    public IReadOnlyList<ContractView> Contracts { get; init; } = [];
}

/// <summary>One module and whether it runs.</summary>
/// <param name="Name">The module id, as the catalogue names it.</param>
/// <param name="IsRunning">Whether it was composed.</param>
/// <param name="Decision">
/// <c>selected</c> for a running module, otherwise the reason it was left out.
/// </param>
public sealed record ModuleView(string Name, bool IsRunning, string Decision);

/// <summary>What a module promised, and whether the promise holds.</summary>
/// <param name="Module">The module this contract belongs to.</param>
/// <param name="RegistersNothingBecause">
/// The stated reason a module registers no service, or <c>null</c>. A module
/// may legitimately be a pipeline step and nothing else; saying so is different
/// from having no contract at all, and only the second is a finding.
/// </param>
/// <param name="Requirements">Services this module reads.</param>
/// <param name="Provisions">Services this module registers.</param>
public sealed record ContractView(
    string Module,
    string? RegistersNothingBecause,
    IReadOnlyList<RequirementView> Requirements,
    IReadOnlyList<ProvisionView> Provisions);

/// <summary>A service a module needs before it can work.</summary>
/// <param name="ServiceType">The port's type name.</param>
/// <param name="IsPresent">Whether something registered it.</param>
/// <param name="Providers">
/// The exact packages and calls that would satisfy this, so an unmet
/// requirement names its own remedy.
/// </param>
public sealed record RequirementView(
    string ServiceType,
    bool IsPresent,
    IReadOnlyList<string> Providers);

/// <summary>A service a module registers for others.</summary>
/// <param name="ServiceType">The port's type name.</param>
/// <param name="IsRegistered">Whether it really ended up in the container.</param>
/// <param name="ReadBy">
/// Active modules that declare they read it. Empty does not mean unused — the
/// application itself may read it — only that no other module says so.
/// </param>
public sealed record ProvisionView(
    string ServiceType,
    bool IsRegistered,
    IReadOnlyList<string> ReadBy);

/// <summary>Configuration keys and the shape of their values.</summary>
public sealed record ConfigurationView
{
    /// <summary>One entry per key, or one per section that has no explicit value.</summary>
    public IReadOnlyList<ConfigurationShapeView> Shapes { get; init; } = [];

    /// <summary>
    /// The reason given for exposing this in production, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The wording, not a character count. Whoever reads this report is the
    /// person who has to judge whether the stated reason still holds.
    /// </remarks>
    public string? ProductionReason { get; init; }
}

/// <summary>One configuration key, without its value.</summary>
/// <param name="Section">The configuration section.</param>
/// <param name="Key">The key relative to the section, or <c>null</c> for none.</param>
/// <param name="Shape">
/// What kind of value is set — derived in memory, with the value discarded.
/// </param>
public sealed record ConfigurationShapeView(string Section, string? Key, string Shape);

/// <summary>The executable security assertions and how they came out.</summary>
public sealed record SecurityCheckView
{
    /// <summary>Whether a report exists to read.</summary>
    public OperatorSectionState State { get; init; } = OperatorSectionState.Absent;

    /// <summary>Why, when there is nothing to show.</summary>
    public string? Note { get; init; }

    /// <summary>One result per check that applies to an active module.</summary>
    public IReadOnlyList<SecurityCheckResultView> Results { get; init; } = [];
}

/// <summary>One check, its verdict and what to do about it.</summary>
/// <param name="Id">The stable check id, such as <c>noelia.audit.chain-scope</c>.</param>
/// <param name="Module">The module it belongs to.</param>
/// <param name="Status">Pass, Warning, Fail or NotApplicable.</param>
/// <param name="Severity">How much a failure would matter.</param>
/// <param name="Summary">What was found, in shapes and actions only.</param>
/// <param name="Remediation">What would resolve it.</param>
public sealed record SecurityCheckResultView(
    string Id,
    string Module,
    string Status,
    string Severity,
    string Summary,
    string Remediation)
{
    /// <summary>The obligations this result is evidence for, if any.</summary>
    public IReadOnlyList<RegulatoryReferenceView> References { get; init; } = [];
}

/// <summary>One obligation an observation speaks to.</summary>
/// <param name="Regime">The body of law — <c>Gdpr</c>, <c>AiAct</c>, <c>Nis2</c>, <c>Dora</c>.</param>
/// <param name="Citation">How it is cited, such as <c>GDPR Art. 30(1)(d), (e)</c>.</param>
/// <param name="Obligation">What that article asks for.</param>
/// <param name="Reader">What a person still has to decide.</param>
public sealed record RegulatoryReferenceView(
    string Regime,
    string Citation,
    string Obligation,
    string Reader);

/// <summary>Where this instance may reach, and under whose jurisdiction.</summary>
/// <remarks>
/// This is the section a fleet view exists for. It is complete rather than
/// observed: every outbound destination is declared before the service starts
/// and an undeclared call fails, so what is absent here is unreachable — which
/// is the difference between a proof and an estimate.
/// </remarks>
public sealed record SovereigntyView
{
    /// <summary>Whether a sovereignty report is registered at all.</summary>
    public OperatorSectionState State { get; init; } = OperatorSectionState.Absent;

    /// <summary>Why, when there is nothing to show.</summary>
    public string? Note { get; init; }

    /// <summary>Whether undeclared outbound calls actually fail.</summary>
    public bool EgressIsEnforced { get; init; }

    /// <summary>The hosts and ranges this instance declared.</summary>
    public IReadOnlyList<string> DeclaredHosts { get; init; } = [];

    /// <summary>One entry per declared dependency.</summary>
    public IReadOnlyList<DependencyView> Dependencies { get; init; } = [];
}

/// <summary>One declared dependency and its jurisdiction.</summary>
/// <param name="Name">What this dependency is for.</param>
/// <param name="Host">The host, or <c>null</c> when none is configured.</param>
/// <param name="Jurisdiction">
/// <c>SelfHosted</c>, <c>Undetermined</c> or <c>ThirdCountryProvider</c>.
/// Undetermined is not a pass: a hostname cannot prove jurisdiction.
/// </param>
/// <param name="Note">Why it was assessed that way.</param>
public sealed record DependencyView(
    string Name,
    string? Host,
    string Jurisdiction,
    string Note)
{
    /// <summary>
    /// <c>Ordinary</c>, or <c>ArtificialIntelligence</c> for a recognised model
    /// or inference endpoint.
    /// </summary>
    public string Kind { get; init; } = "Ordinary";
}

/// <summary>The audit trail, as far as this instance can account for it.</summary>
public sealed record AuditView
{
    /// <summary>Whether an audit trail is registered and readable.</summary>
    public OperatorSectionState State { get; init; } = OperatorSectionState.Absent;

    /// <summary>Why, when there is nothing to show.</summary>
    public string? Note { get; init; }

    /// <summary>Whether the chain was intact as this instance wrote it.</summary>
    public bool IsChainValidAtWriteTime { get; init; }

    /// <summary>
    /// Whether the provider re-read the persisted sink, or only its own writes.
    /// </summary>
    public bool VerifiesPersistedSink { get; init; }

    /// <summary>How many entries this instance accounts for.</summary>
    public long Length { get; init; }

    /// <summary>
    /// Whether the stored chain can be read back and recomputed.
    /// </summary>
    /// <remarks>
    /// Only whether, never the result. Recomputing a trail of millions of
    /// entries on every page load would make opening the dashboard an attack on
    /// the store it reports about — so verification is its own request, at
    /// <c>{dashboard}/audit-chain.json</c>, and this flag is what tells a reader
    /// that asking is worthwhile.
    /// </remarks>
    public bool CanBeVerified { get; init; }

    /// <summary>The most recent entries.</summary>
    public IReadOnlyList<AuditEntryView> Latest { get; init; } = [];
}

/// <summary>One recorded action.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="ActorId">Who acted.</param>
/// <param name="Capacity">In what role.</param>
/// <param name="Action">What they did.</param>
/// <param name="Resource">To what.</param>
public sealed record AuditEntryView(
    DateTimeOffset Timestamp,
    string ActorId,
    string Capacity,
    string Action,
    string Resource);

/// <summary>Sessions this instance has observed.</summary>
/// <remarks>
/// Observed by this instance, which is not a cluster-wide inventory. Tokens and
/// raw device fingerprints never appear here.
/// </remarks>
public sealed record SessionView
{
    /// <summary>Whether a session service is registered and readable.</summary>
    public OperatorSectionState State { get; init; } = OperatorSectionState.Absent;

    /// <summary>Why, when there is nothing to show.</summary>
    public string? Note { get; init; }

    /// <summary>How many sessions this instance has observed.</summary>
    public int Count { get; init; }

    /// <summary>The sessions themselves.</summary>
    public IReadOnlyList<SessionEntryView> Sessions { get; init; } = [];
}

/// <summary>One active session, with nothing presentable in it.</summary>
/// <param name="Subject">Whose session it is.</param>
/// <param name="Session">The session id.</param>
/// <param name="StartedAt">When the sign-in happened.</param>
/// <param name="LastUsedAt">When it was last refreshed.</param>
/// <param name="ExpiresAt">When it ends regardless of refreshing.</param>
/// <param name="ClientFingerprintShape">
/// The shape of the device fingerprint — <c>set</c> or <c>not set</c> — never
/// the fingerprint.
/// </param>
public sealed record SessionEntryView(
    string Subject,
    string Session,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset ExpiresAt,
    string ClientFingerprintShape);

/// <summary>What the rate-limit store is counting.</summary>
public sealed record RateLimitView
{
    /// <summary>Whether a store is registered and readable.</summary>
    public OperatorSectionState State { get; init; } = OperatorSectionState.Absent;

    /// <summary>Why, when there is nothing to show.</summary>
    public string? Note { get; init; }

    /// <summary>The counters the store will disclose.</summary>
    public IReadOnlyList<RateLimitCounterView> Counters { get; init; } = [];
}

/// <summary>One counter.</summary>
/// <param name="KeyFingerprint">
/// A one-way 12-character fingerprint. Store keys can contain subjects or
/// addresses, so the key itself is never shown.
/// </param>
/// <param name="CurrentCount">How many requests fell in this window.</param>
/// <param name="Limit">The ceiling, or <c>null</c> when the store did not record one.</param>
/// <param name="IsRejected">Whether this counter is currently refusing.</param>
/// <param name="ObservedAt">When the counter was last touched.</param>
public sealed record RateLimitCounterView(
    string KeyFingerprint,
    long CurrentCount,
    long? Limit,
    bool IsRejected,
    DateTimeOffset ObservedAt);

/// <summary>Readiness and liveness.</summary>
public sealed record HealthView
{
    /// <summary>Whether a health-check service is registered and answered.</summary>
    public OperatorSectionState State { get; init; } = OperatorSectionState.Absent;

    /// <summary>Why, when there is nothing to show.</summary>
    public string? Note { get; init; }

    /// <summary>The overall verdict.</summary>
    public string? Overall { get; init; }

    /// <summary>One entry per registered check.</summary>
    public IReadOnlyList<HealthEntryView> Entries { get; init; } = [];
}

/// <summary>One health check.</summary>
/// <param name="Name">The check's name.</param>
/// <param name="Status">Healthy, Degraded or Unhealthy.</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="Tags">
/// Its tags. <c>ready</c> marks a check that gates readiness; a backing service
/// usually adds one naming its kind.
/// </param>
public sealed record HealthEntryView(
    string Name,
    string Status,
    double DurationMs,
    IReadOnlyList<string> Tags);
