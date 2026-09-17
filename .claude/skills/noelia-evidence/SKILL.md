---
name: noelia-evidence
description: Use when writing or reviewing anything that maps observations to regulation — a security check citing an article, an attestation, a compliance page, or marketing copy about it. Covers what may legally be claimed, the required wording, and which duties are machine-checkable at all.
---

# Evidence, not compliance

Noelia and the Control Plane produce **technical evidence**. They never state
conformity. This is a legal boundary, not a stylistic one.

## The words

| Never | Use instead |
|---|---|
| certified, certificate, certification (of our output) | attestation, evidence report, self-assessment output |
| hereby certifies, we certify | the control plane observed, this records |
| compliant, complies with, meets Art. X | evidence for Art. X, a fact Art. X asks about |
| audited, verified by | recorded, recomputed, observed |

Under **GDPR Art. 42/43**, only a supervisory authority or a body accredited to
EN ISO/IEC 17065 may certify. BSI C5 is attested only by a licensed auditor;
SecNumCloud is qualified only by ANSSI; EUCS permits self-assessment at "Basic"
and requires a conformity assessment body above it. A software vendor is none of
these, and no framing changes that.

Calling an unaccredited output "certified" in the EU is a misleading commercial
practice — Dir. 2005/29/EC for consumers, Dir. 2006/114/EC business to business.
This is a real exposure, not a theoretical one.

Vanta, Drata and Secureframe all say explicitly that SOC 2 is an *attestation*,
not a certification, for the same reason. Following them here is the safe path,
not the timid one.

## Every citation carries what it leaves open

`RegulatoryReference` has four fields and the fourth is load-bearing:

```csharp
new RegulatoryReference(
    RegulatoryRegime.Gdpr,
    "Chapter V (Art. 44–49)",
    Obligation: "A transfer to a third country requires a safeguard.",
    Reader:     "Which safeguard covers this destination, and whether a "
              + "transfer impact assessment exists for it.");
```

`Reader` is **never empty**. A reference that settled its own article would be
the exact overreach the vocabulary exists to avoid. Render it in the same row as
the citation, never as a footnote — a footnote does not survive a screenshot or
a paste into somebody else's spreadsheet.

## What a program can and cannot establish

**Can**: that a record exists, is produced automatically, covers a window and has
not been edited since; that a host resolves to a given jurisdiction; that a
dependency was declared or was not; that a boundary is enforced.

**Cannot**: that a processing agreement is adequate; that a risk assessment is
any good; that a payload contains personal data; that a use case is high-risk
under Annex III; that human oversight is effective.

If a proposed check would need the second column, it is not a check. It is a
prompt for a person, and belongs in `Reader` or in a caveat.

## The regimes, and what is actually checkable

| Regime | Checkable artefact | Not checkable |
|---|---|---|
| **GDPR** Art. 30 | Recipients a service can reach | Whether personal data goes there |
| **GDPR** Ch. V | Which recipients are in third countries | Which safeguard covers them |
| **GDPR** Art. 28 | That a processor relationship exists at all | Whether the DPA is adequate |
| **GDPR** Art. 32 | Integrity of the record (hash chain) | Whether measures match the risk |
| **AI Act** Art. 12 | Records are automatic, unbroken, retained | Whether the system is high-risk |
| **AI Act** Art. 26 | An inventory of model endpoints exists | Oversight, instructions, classification |
| **AI Act** Art. 50 | — | Whether a disclosure appears in the UI |
| **NIS2** Art. 21(3) | A supplier inventory | Criticality, contracts, governance |
| **DORA** Art. 8/28 | An asset and third-party register | Criticality classification |

## Dates matter and must be stated

The AI Act phases in. As of September 2026: prohibited practices, AI literacy,
GPAI rules and **Art. 50 transparency** are in force. **Art. 12 record-keeping
and Art. 26 deployer duties bind from 2 December 2027.** Annex I embedded
high-risk systems follow on 2 August 2028.

An evidence document that implies a 2027 duty is already binding invites the
reader to spend money early. Say the date.

## The AI inventory is a floor

Model endpoints are recognised **by host name**. That finds the SDK somebody
added last week; it misses a model behind `ml.internal`, a name the operator
chose, or a gateway. Every place the inventory is shown must say so. An
inventory read as exhaustive when it is not is the worst possible input to an
Article 26 assessment, and the hole is invisible precisely because the page
looked complete.

## Sovereignty is not a regime

`RegulatoryRegime` has no `Sovereignty` member and must not gain one. No
regulation requires digital sovereignty; it is a decision an operator makes. The
sovereignty evidence cites obligations it happens to support, and its attestation
says outright that it measures a choice rather than conformity with a rule.

## Test the negatives

The assertions that matter are the ones that fail when a forbidden word appears.
`ObligationsTests.No_document_claims_compliance_or_certification` scans every
statement and caveat. Extend it rather than trusting review — it is easy to write
a compliance view that tells a customer what they want to hear.
