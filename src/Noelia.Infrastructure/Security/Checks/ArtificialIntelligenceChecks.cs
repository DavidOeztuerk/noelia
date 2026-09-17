using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Audit;
using Noelia.Abstractions.Compliance;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Abstractions.Sovereignty;

namespace Noelia.Infrastructure.Security.Checks;

/// <summary>
/// Which model endpoints this service is configured to reach.
/// </summary>
/// <remarks>
/// <para>The first thing an AI-Act deployer needs and the thing least often
/// written down: a list of the model endpoints a service actually talks to. An
/// SDK added for one feature becomes a processor relationship, a cross-border
/// transfer and — if the use case turns out to be high-risk — a set of duties
/// under Article 26, all without anyone filing a form.</para>
///
/// <para><strong>Floor, not ceiling.</strong> Recognition is by host name, so
/// this finds a declared call to a known provider and misses a model served
/// from a name the operator chose. The summary says which of the two it is
/// reporting, because an inventory read as exhaustive when it is not would send
/// a reader into an Article 26 assessment with a hole in it.</para>
/// </remarks>
internal sealed class ArtificialIntelligenceInventoryCheck(
    IServiceProvider services) : SecurityCheckBase
{
    public override string Id => "noelia.ai.inventory";
    public override NoeliaModule Module => NoeliaModule.Composition;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.Informational;

    public override string Remediation =>
        "List every model endpoint the service may reach as a DeclaredDependency, so that "
        + "the inventory an AI-Act assessment starts from is produced by the running "
        + "configuration rather than assembled from memory.";

    public override IReadOnlyList<RegulatoryReference> References =>
    [
        RegulatoryReferences.AiActDeployerDuties,
        RegulatoryReferences.GdprRecordsOfProcessing
    ];

    public override Task<SecurityCheckResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var report = services.GetService<ISovereigntyReport>();

        if (report is null)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.NotApplicable,
                "No sovereignty report is registered, so no destination register exists to "
                + "read model endpoints out of."));
        }

        var endpoints = report.Assess().ArtificialIntelligenceDependencies;

        if (endpoints.Count == 0)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Pass,
                "No configured destination is a recognised model or inference endpoint. A "
                + "model served under a name of the operator's own choosing would not be "
                + "recognised here."));
        }

        var names = string.Join(", ", endpoints.Select(e => e.Name).Order(StringComparer.Ordinal));

        return Task.FromResult(Result(
            SecurityCheckStatus.Pass,
            $"{endpoints.Count} configured destination(s) are recognised model or inference "
            + $"endpoints: {names}. Whether any of them makes this a high-risk AI system "
            + "under Annex III is a classification only a person can make."));
    }
}

/// <summary>
/// Whether a model endpoint sits under a third country's access law.
/// </summary>
/// <remarks>
/// Two facts the register already holds, stated together because the pair is
/// what makes them consequential: a destination that is both a model endpoint
/// and a third-country provider is a cross-border transfer at the moment a
/// prompt leaves — and prompts carry whatever the caller put in them.
/// </remarks>
internal sealed class ArtificialIntelligenceTransferCheck(
    IServiceProvider services) : SecurityCheckBase
{
    public override string Id => "noelia.ai.transfer";
    public override NoeliaModule Module => NoeliaModule.Composition;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.Medium;

    public override string Remediation =>
        "For each model endpoint outside the Union, record the transfer safeguard and the "
        + "processing agreement that cover it, or move the inference to a provider that "
        + "does not need one.";

    public override IReadOnlyList<RegulatoryReference> References =>
    [
        RegulatoryReferences.GdprThirdCountryTransfer,
        RegulatoryReferences.GdprProcessorContract
    ];

    public override Task<SecurityCheckResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var report = services.GetService<ISovereigntyReport>();

        if (report is null)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.NotApplicable,
                "No sovereignty report is registered, so destinations cannot be classified."));
        }

        var endpoints = report.Assess().ArtificialIntelligenceDependencies;

        if (endpoints.Count == 0)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.NotApplicable,
                "No recognised model endpoint is configured."));
        }

        var abroad = endpoints
            .Where(e => e.Jurisdiction == Jurisdiction.ThirdCountryProvider)
            .ToArray();

        var unknown = endpoints
            .Where(e => e.Jurisdiction == Jurisdiction.Undetermined)
            .ToArray();

        if (abroad.Length > 0)
        {
            var names = string.Join(", ", abroad.Select(e => e.Name).Order(StringComparer.Ordinal));

            return Task.FromResult(Result(
                SecurityCheckStatus.Warning,
                $"{abroad.Length} model endpoint(s) belong to providers subject to "
                + $"third-country access law: {names}. Every prompt sent to one is a "
                + "cross-border transfer of whatever the prompt contains."));
        }

        if (unknown.Length > 0)
        {
            var names = string.Join(", ", unknown.Select(e => e.Name).Order(StringComparer.Ordinal));

            return Task.FromResult(Result(
                SecurityCheckStatus.Warning,
                $"{unknown.Length} model endpoint(s) are public names whose operator cannot "
                + $"be told from the name: {names}. Someone has to answer where inference "
                + "runs before a transfer basis can be chosen.",
                SecurityCheckSeverity.Low));
        }

        return Task.FromResult(Result(
            SecurityCheckStatus.Pass,
            "Every recognised model endpoint resolves to infrastructure the operator "
            + "controls."));
    }
}

/// <summary>
/// Whether what the service records about its AI use can be shown to hold.
/// </summary>
/// <remarks>
/// <para>Article 12 asks for records that are produced automatically and cover
/// the system's lifetime; Article 26(6) makes the deployer keep them for at
/// least six months. Neither asks for tamper evidence. A hash-chained trail is
/// therefore more than the Act demands — and it is what turns "we have logs"
/// into something that survives being disputed, which is the only situation in
/// which the logs are ever read.</para>
///
/// <para>Reported only where a model endpoint exists. A service with no AI in
/// it has no Article 12 duty, and a green tick against an article that does not
/// apply is noise in the one document meant to cut through it.</para>
/// </remarks>
internal sealed class ArtificialIntelligenceRecordKeepingCheck(
    IServiceProvider services) : SecurityCheckBase
{
    public override string Id => "noelia.ai.record-keeping";
    public override NoeliaModule Module => NoeliaModule.Composition;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.Medium;

    public override string Remediation =>
        "Register a chained audit sink — UseRedisAudit() or the in-memory sink for a single "
        + "process — so that what the service records about its model calls is written "
        + "automatically and can be shown not to have been edited afterwards.";

    public override IReadOnlyList<RegulatoryReference> References =>
    [
        RegulatoryReferences.AiActRecordKeeping,
        RegulatoryReferences.GdprIntegrityOfProcessing
    ];

    public override Task<SecurityCheckResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var report = services.GetService<ISovereigntyReport>();

        if (report is null || report.Assess().ArtificialIntelligenceDependencies.Count == 0)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.NotApplicable,
                "No recognised model endpoint is configured, so no record-keeping duty "
                + "follows from the AI Act."));
        }

        var probe = services.GetService<IServiceProviderIsService>();

        if (probe?.IsService(typeof(ISovereignAuditSink)) != true)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Warning,
                "A model endpoint is configured and no audit sink is registered, so nothing "
                + "the service does with it is recorded anywhere it could later be read."));
        }

        var chained = probe.IsService(typeof(IChainedSovereignAuditSink));
        var verifiable = probe.IsService(typeof(IReadableSovereignAuditSink));

        if (chained && verifiable)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Pass,
                "Records are written automatically to a chained sink and can be read back "
                + "and verified, so a claim that an entry was added or altered afterwards "
                + "can be answered. How long they are kept is a property of the store, not "
                + "of this process."));
        }

        if (chained)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Warning,
                "Records are written to a chained sink, but nothing can read them back, so "
                + "the chain cannot be verified from outside the process that wrote it.",
                SecurityCheckSeverity.Low));
        }

        return Task.FromResult(Result(
            SecurityCheckStatus.Warning,
            "Records are written to a sink that does not own the chain head. With more than "
            + "one replica that produces a trail a verifier reports as broken on a system "
            + "where nothing was tampered with."));
    }
}
