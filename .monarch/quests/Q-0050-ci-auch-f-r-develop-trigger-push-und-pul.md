---
id: Q-0050
gate: G-0014
kind: side
rank: A
state: cleared
dod:
  - In ci.yml stehen nur in den Triggern push und pull_request jeweils branches: [main, develop]; kein Schritt, kein Job geändert (Diff zeigt genau diese zwei Zeilen)
  - PR
  - Warden prüft die Änderung in der Code-Phase mit (Entscheid projects-74 im Auftrag des Users, 29.10.2026)
scope:
  include: [".github/workflows/ci.yml"]
  exclude: []
estimate: { session_pct: 3, weekly_pct: 0.5, gold_usd: 0 }
boss: "CI-Lauf auf PR #50 (pull_request nach develop) grün"
board: { pr: 50 }
---
# CI auch für develop: Trigger push und pull_request auf [main, develop]
