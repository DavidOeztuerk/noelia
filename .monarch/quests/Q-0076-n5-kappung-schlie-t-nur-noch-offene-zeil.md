---
id: Q-0076
gate: G-0020
kind: main
rank: A
state: in_review
dod:
  - "CapAsync schließt nur Zeilen mit RevokedAt == null und ReplacedBy == null"
  - "N5_ACapAfterAConcurrentRotation_KeepsTheRotationTime unverändert und grün"
  - "Alle Sitzungs-Tests grün"
scope:
  include: ["src/Noelia.Data.EntityFrameworkCore/Sessions/EntityFrameworkRefreshTokenStore.cs", "tests/Noelia.Infrastructure.Tests/**"]
  exclude: []
estimate: { session_pct: 5, weekly_pct: 1, gold_usd: 0 }
boss: "Der rote N5-Test aus Q-0073 wird grün, ohne dass er geändert wurde"
board: { pr: 76 }
---
# N5: Kappung schließt nur noch offene Zeilen
