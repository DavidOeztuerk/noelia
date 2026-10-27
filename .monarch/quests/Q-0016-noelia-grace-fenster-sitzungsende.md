---
id: Q-0016
gate: G-0006
kind: main
rank: A
severity: medium
risk_tags: [auth]
state: proposed
findings: [NO-1]
scope:
  repo: Noelia
  include: ["src/Noelia.Data.EntityFrameworkCore/Sessions/EntityFrameworkRefreshTokenStore.cs", "src/Noelia.InMemory/Sessions/InMemoryRefreshTokenStore.cs", "src/**/TokenSessionService.cs", "tests/**/Sessions/**"]
  exclude: []
estimate: { session_pct: 15, weekly_pct: 2, gold_usd: 0 }
dod:
  - AlreadyRotatedAsync prüft, ob die Sitzung geschlossen ist; geschlossene Sitzung → kein Grace-Erfolg
  - CloseSession/CloseAllSessions markieren auch rotierte Vorgänger
  - Gleiche Semantik in EF- und InMemory-Store (gemeinsamer Conformance-Test)
boss: "RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded grün in CI (EF + InMemory)"
evidence: ["EntityFrameworkRefreshTokenStore.cs:56-59,146-196", "InMemoryRefreshTokenStore.cs:66-75", "TokenSessionService.cs:127-142", "TokenSessionOptions.cs:43 (Grace 15 s)", "07-warden-noelia.md NO-1"]
board: { provider: github, issue: 35 }
---
# Grace-Fenster respektiert Sitzungsende

## Objective
Abmelden und „überall abmelden“ lassen sich nicht im 15-s-Grace-Fenster mit einem rotierten Refresh-Token aushebeln. Voraussetzung für WT Q-0008.

## Definition of Done
- [ ] Design → Review → Freigabe (auth, Rank A); Patch-Release + MIGRATION-Eintrag
- [ ] Fix in beiden Stores + Conformance-Test
- [ ] Boss: `RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded` grün in CI
