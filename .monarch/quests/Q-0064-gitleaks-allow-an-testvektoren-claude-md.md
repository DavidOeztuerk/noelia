---
id: Q-0064
gate: G-0017
kind: main
rank: C
state: in_progress
dod:
  - "Kommentar gitleaks:allow mit einem Satz Begründung an den Konstanten, Umbenennung bleibt"
  - "CLAUDE.md-Pin stimmt mit VersionPrefix überein"
  - "dashboard-mono-dev.png per normalem Commit gelöscht, nicht aus der History entfernt"
  - "CI grün"
scope:
  include: ["tests/Noelia.Infrastructure.Tests/Security/ServiceTokens/**", "CLAUDE.md", "**/dashboard-mono-dev.png", ".monarch/**"]
  exclude: []
estimate: { session_pct: 3, weekly_pct: 1, gold_usd: 0 }
boss: "gitleaks git --staged meldet nichts"
---
# gitleaks:allow an Testvektoren, CLAUDE.md-Pin, dashboard-mono-dev.png löschen (#51)
