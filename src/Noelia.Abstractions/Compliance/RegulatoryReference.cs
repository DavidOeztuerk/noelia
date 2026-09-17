namespace Noelia.Abstractions.Compliance;

/// <summary>
/// A body of law an observation can serve as evidence for.
/// </summary>
/// <remarks>
/// <para>Deliberately short. Each entry is a regulation whose text names a
/// technical artefact — an inventory, a register, a log, a transfer record —
/// that a running system either has or does not have. Regulations that only
/// impose governance duties are absent, because nothing here could honestly
/// speak to them.</para>
///
/// <para>Digital sovereignty is not on this list. No regulation requires it;
/// it is a decision an operator makes, and the sovereignty evidence therefore
/// cites obligations it happens to support rather than one that demands it.</para>
/// </remarks>
public enum RegulatoryRegime
{
    /// <summary>Regulation (EU) 2016/679 — General Data Protection Regulation.</summary>
    Gdpr,

    /// <summary>Regulation (EU) 2024/1689 — Artificial Intelligence Act.</summary>
    AiAct,

    /// <summary>Directive (EU) 2022/2555 — NIS2.</summary>
    Nis2,

    /// <summary>Regulation (EU) 2022/2554 — DORA.</summary>
    Dora
}

/// <summary>
/// The obligation an observation is evidence for.
/// </summary>
/// <remarks>
/// <para><strong>Evidence, not compliance.</strong> A reference says "this
/// observation is the kind of fact that article asks about", never "this
/// article is satisfied". Every obligation cited here also carries duties no
/// program can observe: whether a processing agreement is adequate, whether a
/// risk assessment is any good, whether a use case is high-risk. Those are the
/// reader's, and <see cref="Reader"/> says so in the document.</para>
/// </remarks>
/// <param name="Regime">Which body of law.</param>
/// <param name="Article">The article, as it is cited in that law — "Art. 30(1)(d)".</param>
/// <param name="Obligation">What that article asks for, in one line.</param>
/// <param name="Reader">
/// What a person still has to decide once they have this evidence. Never empty:
/// a reference that claimed to settle its article by itself would be the
/// overreach this whole vocabulary exists to avoid.
/// </param>
public sealed record RegulatoryReference(
    RegulatoryRegime Regime,
    string Article,
    string Obligation,
    string Reader)
{
    /// <summary>How the regime is written out for a reader.</summary>
    public string RegimeName => Regime switch
    {
        RegulatoryRegime.Gdpr => "GDPR",
        RegulatoryRegime.AiAct => "EU AI Act",
        RegulatoryRegime.Nis2 => "NIS2",
        RegulatoryRegime.Dora => "DORA",
        _ => Regime.ToString()
    };

    /// <summary>The citation, as it would appear in a footnote.</summary>
    public string Citation => $"{RegimeName} {Article}";
}

/// <summary>
/// The obligations Noelia cites, written once.
/// </summary>
/// <remarks>
/// <para>A shared list rather than strings at each call site, because the same
/// article is evidenced from several places and a citation that drifts between
/// them is worse than none: the reader cannot tell whether two findings are
/// about one duty or two.</para>
///
/// <para>Dates are stated where an obligation is not yet in force. An evidence
/// document that quietly implies a 2027 duty is already binding invites the
/// reader to spend money early, which is its own kind of dishonesty.</para>
/// </remarks>
public static class RegulatoryReferences
{
    /// <summary>Records of processing must name recipients, including in third countries.</summary>
    public static RegulatoryReference GdprRecordsOfProcessing { get; } = new(
        RegulatoryRegime.Gdpr,
        "Art. 30(1)(d), (e)",
        "The record of processing names the recipients of personal data and any "
        + "transfer to a third country.",
        "Whether personal data actually reaches this destination, and under which "
        + "purpose it is recorded.");

    /// <summary>A transfer to a third country needs a legal basis.</summary>
    public static RegulatoryReference GdprThirdCountryTransfer { get; } = new(
        RegulatoryRegime.Gdpr,
        "Chapter V (Art. 44–49)",
        "A transfer of personal data to a third country requires an adequacy "
        + "decision, appropriate safeguards, or a derogation.",
        "Which safeguard covers this destination, and whether a transfer impact "
        + "assessment exists for it.");

    /// <summary>Processing by a processor rests on a contract.</summary>
    public static RegulatoryReference GdprProcessorContract { get; } = new(
        RegulatoryRegime.Gdpr,
        "Art. 28(3)",
        "Processing by a processor is governed by a contract that binds it to the "
        + "controller's instructions.",
        "Whether a processing agreement is on file for this provider, and whether "
        + "its terms match what the service actually sends.");

    /// <summary>Integrity and confidentiality of processing.</summary>
    public static RegulatoryReference GdprIntegrityOfProcessing { get; } = new(
        RegulatoryRegime.Gdpr,
        "Art. 32(1)",
        "Measures appropriate to the risk, including the ability to ensure the "
        + "ongoing integrity of processing systems.",
        "Whether the measures observed here are the ones the risk assessment "
        + "assumed.");

    /// <summary>High-risk AI systems record automatically over their lifetime.</summary>
    public static RegulatoryReference AiActRecordKeeping { get; } = new(
        RegulatoryRegime.AiAct,
        "Art. 12",
        "A high-risk AI system records events automatically over its lifetime; "
        + "the deployer keeps those logs for at least six months (Art. 26(6)).",
        "Whether this system is high-risk at all — a classification under Annex III "
        + "that only a person can make. The record-keeping duty binds from "
        + "2 December 2027.");

    /// <summary>Deployers of high-risk systems have duties of their own.</summary>
    public static RegulatoryReference AiActDeployerDuties { get; } = new(
        RegulatoryRegime.AiAct,
        "Art. 26",
        "A deployer follows the instructions for use, assigns human oversight to a "
        + "competent person, and monitors operation.",
        "Whether the use case is high-risk, who holds oversight, and whether the "
        + "provider's instructions are being followed. Binds from 2 December 2027.");

    /// <summary>Some AI interactions must be disclosed to the person.</summary>
    public static RegulatoryReference AiActTransparency { get; } = new(
        RegulatoryRegime.AiAct,
        "Art. 50",
        "A person interacting with an AI system is told so; synthetic content is "
        + "marked as machine-generated.",
        "Whether the destination observed here serves a user-facing interaction, "
        + "and whether the disclosure exists in the interface. In force since "
        + "2 August 2026.");

    /// <summary>Supply-chain risk begins with knowing the suppliers.</summary>
    public static RegulatoryReference Nis2SupplyChain { get; } = new(
        RegulatoryRegime.Nis2,
        "Art. 21(2)(d), (3)",
        "Risk management covers the security of the supply chain, including the "
        + "direct suppliers and service providers a service depends on.",
        "How critical each supplier is to an essential function, and what the "
        + "contract requires of it.");

    /// <summary>An identified asset base is DORA's first requirement.</summary>
    public static RegulatoryReference DoraAssetIdentification { get; } = new(
        RegulatoryRegime.Dora,
        "Art. 8(1), (4)",
        "A maintained inventory of ICT assets and their interdependencies, "
        + "reviewed at least yearly.",
        "The criticality classification of each asset, which is a business "
        + "judgement rather than a technical one.");

    /// <summary>Third-party arrangements are registered and reported.</summary>
    public static RegulatoryReference DoraThirdPartyRegister { get; } = new(
        RegulatoryRegime.Dora,
        "Art. 28(3)",
        "A register of information on every contractual arrangement for ICT "
        + "services, distinguishing those supporting critical functions.",
        "Which arrangements are contractual at all, and which support a critical "
        + "or important function.");

    /// <summary>Every reference above, for a mapping view.</summary>
    public static IReadOnlyList<RegulatoryReference> All { get; } =
    [
        GdprRecordsOfProcessing,
        GdprThirdCountryTransfer,
        GdprProcessorContract,
        GdprIntegrityOfProcessing,
        AiActRecordKeeping,
        AiActDeployerDuties,
        AiActTransparency,
        Nis2SupplyChain,
        DoraAssetIdentification,
        DoraThirdPartyRegister
    ];
}
