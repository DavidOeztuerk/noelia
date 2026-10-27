---
id: Q-0028
gate: G-0009
kind: side
rank: B
severity: low
risk_tags: [public-api, pii]
state: proposed
findings: [NO-14]
scope:
  repo: Noelia
  include: ["src/Noelia.Core/Compliance/**", "src/Noelia.Abstractions/Security/DegradingTokenRevocationEvaluator.cs", "src/**/Builder/**", "SOVEREIGNTY.md", "README.md"]
  exclude: []
estimate: { session_pct: 12, weekly_pct: 1.5, gold_usd: 0 }
dod:
  - "Entscheidung je Port: Referenzimplementierung liefern ODER XML-Doku + README: „Seam, Konsument implementiert“"
  - Registrierungshelfer für DegradingTokenRevocationEvaluator, der den Check noelia.revocation.degradation korrekt bedient
boss: "PublicApi_CompliancePorts_DocumentedOrImplemented (API-Approval-Test) grün in CI"
evidence: ["src/Noelia.Core/Compliance/{IDataErasureService,IDataExportService,IConsentManager}.cs", "07-warden-noelia.md NO-14"]
board: { provider: github, issue: 46 }
---
# Compliance-Ports implementieren oder als Seam kennzeichnen

## Objective
Kein Konsument liest „DSGVO Art. 17 Port“ als fertige Funktion.

## Definition of Done
- [ ] Plan-Freigabe (Rank B, öffentliche API)
- [ ] Umsetzung der Entscheidung
- [ ] Boss: API-Approval-Test grün in CI
