---
id: Q-0065
gate: G-0017
kind: side
rank: C
state: in_progress
dod:
  - "README: Prüfungen laufen zweimal, zweiter Lauf nach dem Start der Routen (#62)"
  - "MIGRATION (deutsch): Empfänger ohne Tür meldet Warning statt Pass, Aussteller Pass statt Fail (#62)"
  - "Kommentar in PrincipalModule.UsePrincipal nach ADR-0006 Entscheidung 5"
  - "CI grün"
scope:
  include: ["README.md", "MIGRATION.md", "src/Noelia.Infrastructure/Builder/Modules/PrincipalModule.cs", ".monarch/**"]
  exclude: []
estimate: { session_pct: 2, weekly_pct: 1, gold_usd: 0 }
boss: "README und MIGRATION nennen beide den zweiten Lauf"
---
# README und MIGRATION nennen den Verhaltenswechsel der Prüfläufe (#62)
