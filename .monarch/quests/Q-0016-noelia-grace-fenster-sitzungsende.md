---
id: Q-0016
gate: G-0006
kind: main
rank: A
severity: medium
risk_tags: [auth]
state: in_progress
findings: [NO-1]
scope:
  repo: Noelia
  include: ["src/Noelia.Data.EntityFrameworkCore/Sessions/**", "src/Noelia.InMemory/Sessions/**", "src/Noelia.Infrastructure/Security/Sessions/**", "src/Noelia.Abstractions/Security/Sessions/**", "tests/Noelia.Infrastructure.Tests/**", "docs/adr/**", "MIGRATION.md", "README.md"]
  exclude: []
estimate: { session_pct: 15, weekly_pct: 2, gold_usd: 0 }
dod:
  - "Design-ADR 0008 (Sitzung und Widerruf je angemeldeter Personen-Identität, auf ADR-0007), Warden-Approve vor der Umsetzung"
  - "Ein rotiertes Refresh-Token einer geschlossenen Sitzung erzeugt im Grace-Fenster kein Geschwister mehr (Ergebnis Revoked), in EF- und InMemory-Store"
  - "Auch ein Abmelden zwischen Prüfung und Schreiben des Geschwisters lässt keine offene Sitzung zurück"
  - "Gemeinsamer Conformance-Test für beide Stores, vorher rot und danach grün"
  - "MIGRATION- und README-Eintrag"
boss: "RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded grün in CI (EF + InMemory)"
evidence: ["EntityFrameworkRefreshTokenStore.cs:56-59,146-196", "InMemoryRefreshTokenStore.cs:66-75", "TokenSessionService.cs:127-142", "TokenSessionOptions.cs:43 (Grace 15 s)", "07-warden-noelia.md NO-1"]
board: { provider: github, issue: 35, pr: 71 }
---
# Grace-Fenster respektiert Sitzungsende

## Objective
Abmelden und „überall abmelden“ lassen sich nicht im 15-s-Grace-Fenster mit einem rotierten Refresh-Token aushebeln. Voraussetzung für WT Q-0008.

## Definition of Done
- [ ] Design → Review → Freigabe (auth, Rank A); Patch-Release + MIGRATION-Eintrag
- [ ] Fix in beiden Stores + Conformance-Test
- [ ] Boss: `RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded` grün in CI
