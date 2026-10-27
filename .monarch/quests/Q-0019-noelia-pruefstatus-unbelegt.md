---
id: Q-0019
gate: G-0007
kind: main
rank: A
warden_rank: C
severity: medium
risk_tags: [auth, public-api]
state: proposed
findings: [N2, NO-9, SC-1]
scope:
  repo: Noelia
  include: ["src/Noelia.Infrastructure/Security/Checks/**", "src/Noelia.Infrastructure/Builder/Modules/MiddlewarePipelineModule.cs", "src/Noelia.Abstractions/Operator/OperatorReport.cs", "src/Noelia.Infrastructure/Security/TokenRevocationMiddleware.cs", "tests/**/Checks/**", "MIGRATION.md"]
  exclude: []
estimate: { session_pct: 25, weekly_pct: 3, gold_usd: 0 }
dod:
  - "Neuer Status Declared/Unverified: Regeln, die nur Konfiguration sehen, melden nicht mehr Pass"
  - UseTokenRevocation setzt einen Pipeline-Marker; noelia.revocation.degradation meldet Pass nur mit Marker + Evaluator
  - Für Revocation, Cookie, AEAD mindestens eine echte Sonde (Round-Trip) statt Typnamen-Heuristik
  - Report-Schema-Erweiterung abwärtskompatibel (CP versteht neuen Status, siehe Q-0041)
boss: "Demo_WithoutUseTokenRevocation_ReportsNotPass + Checks_NeverPassOnTypeNameAlone grün in CI"
evidence: ["BuiltInSecurityChecks.cs:145,198,542,644-660", "ArtificialIntelligenceChecks.cs:302-318", "ServiceCollectionExtensions.cs:215-231", "MiddlewarePipelineModule.cs:273-278", "05-sovereignty-self-check.md Teil A", "07-warden-noelia.md N2"]
board: { provider: github, issue: 38 }
---
# Prüfstatus „unbelegt“ statt Pass

## Objective
Ein grünes Ergebnis der eigenen Prüfregeln bedeutet „Verhalten belegt“, nicht „etwas ist registriert“.

## Definition of Done
- [ ] Design → Review → Freigabe (Schema- und Verhaltensänderung, Rank A)
- [ ] Status, Marker, Sonden, Schema-Version
- [ ] Boss: beide Tests grün in CI; Dogfooding-Lauf gegen Demo und WT zeigt keine „falsche Sicherheit“ mehr bei den 8 Regeln aus 05
