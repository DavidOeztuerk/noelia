---
id: Q-0014
gate: G-0004
kind: side
rank: B
severity: low
risk_tags: [public-api, jurisdiction]
state: proposed
findings: [SC-2]
scope:
  repo: [Noelia, workertransfer]
  include: ["Noelia/src/Noelia.Infrastructure/Sovereignty/**", "Noelia/src/Noelia.Abstractions/**/HostJurisdiction.cs", "workertransfer/src/shared/WorkerTransfer.ServiceDefaults/ServiceFoundation.cs"]
  exclude: []
estimate: { session_pct: 15, weekly_pct: 2, gold_usd: 0 }
dod:
  - Noelia-Policy-Option „ThirdCountry: deny | allowExplicitly(reason)“
  - WT gibt jeden Drittland-Anbieter mit Begründung frei statt implizit über jede URL in der Config
  - Report unterscheidet „durchgesetzt“ (Allowlist) von „souverän“ (Jurisdiktion)
boss: "EgressPolicy_DeniesUndeclaredThirdCountryHost grün in CI (Noelia) + WT-Startprüfung"
evidence: ["WT ServiceFoundation.cs:234-236,559-573", "Noelia HostJurisdiction.cs", "OperatorReport.cs:261", "07-warden-workertransfer.md SC-2"]
board: { provider: github, issue: 34 }
---
# Egress: Drittland nur mit ausdrücklicher Freigabe

## Objective
„Egress durchgesetzt“ bedeutet auch: kein Drittland-Ziel ohne bewusste, begründete Freigabe.

## Definition of Done
- [ ] Plan-Freigabe (Rank B, öffentliche API von Noelia)
- [ ] Option in Noelia + Nutzung in WT + Report-Feld
- [ ] Boss: `EgressPolicy_DeniesUndeclaredThirdCountryHost` grün in CI
