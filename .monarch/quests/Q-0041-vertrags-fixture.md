---
id: Q-0041
gate: G-0013
kind: main
rank: B
severity: low
risk_tags: [public-api]
state: proposed
findings: [X-1, X-2, X-7, X-8]
blocked_by: []
scope:
  repo: [Noelia, workertransfer, NoeliaControlPlane]
  include: ["Noelia/src/Noelia.Abstractions/**", "Noelia/tests/Contracts/**", "workertransfer/tests/**/Contracts/**", "NoeliaControlPlane/tests/**/Contracts/**", "NoeliaControlPlane/src/Noelia.ControlPlane/Noelia.ControlPlane.csproj", "Noelia/README.md", "Noelia/MIGRATION.md"]
  exclude: []
estimate: { session_pct: 20, weekly_pct: 3, gold_usd: 0 }
dod:
  - Noelia veröffentlicht Konstanten (OperatorHeader, Default-Lebensdauer) in Noelia.Abstractions; CP-Literal und WT-Konstante nutzen sie
  - Golden-Fixtures (Report v3, Access-Token-Claims inkl. session_id/sid, Widerruf-Roundtrip) in Noelia; WT und CP testen dagegen
  - Kompatibilitätsmatrix Noelia ↔ CP im Noelia-README; CP-Default-Version bei Noelia-Release angehoben
  - Severity-Mapping (Report → CP → CLI) dokumentiert und getestet
boss: "ContractFixtures_v3 grün in Noelia-, WT- und CP-CI"
evidence: ["CP FleetCollector.cs:215", "Noelia Cli/LiveScan.cs:34", "WT EvidenceSetup.cs:42, Revocation.cs:80-84", "Noelia SessionClaims.cs:17-26", "CP Noelia.ControlPlane.csproj:10", "CP Severity.cs:4-14, Noelia Cli/Findings.cs:12-21", "07-warden-*.md X-1, X-2, X-7, X-8"]
board: { provider: github, issue: 48 }
---
# Gemeinsame Vertrags-Fixture

## Objective
Ein Vertragsbruch zwischen den drei Repos fällt in CI auf, bevor eine Session ihn findet.

## Definition of Done
- [ ] Plan- und Merge-Freigabe (Rank B, service-übergreifend)
- [ ] Konstanten, Fixtures, Matrix, Mapping
- [ ] Boss: `ContractFixtures_v3` grün in allen drei CIs
