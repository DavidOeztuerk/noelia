---
id: Q-0051
gate: G-0014
kind: side
rank: A
state: in_review
dod:
  - demo/Directory.Packages.props NoeliaVersion 7.1.0 und die fünf 7.0.1-Pins in README „Consuming Noelia“ auf 7.1.0 (Muster ab78281/fd8c20c), sonst nichts; kein NoWarn, TreatWarningsAsErrors unverändert
  - CI auf PR
scope:
  include: ["demo/Directory.Packages.props", "README.md"]
  exclude: []
estimate: { session_pct: 2, weekly_pct: 0.3, gold_usd: 0 }
boss: "CI-Job demo auf PR #50 grün"
---
# Demo- und README-Pin auf 7.1.0 (zieht mit VersionPrefix mit, wie bei 7.0.0/7.0.1)
