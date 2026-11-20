---
id: Q-0062
gate: G-0017
kind: main
rank: A
state: in_review
dod:
  - "Roter Test zuerst: gültige Person an ActingAsSelf bekommt mit UseAuth().UsePrincipal() 200"
  - "Dienst-Identität gehört nicht in den Principal (ADR-0003)"
  - "CI grün"
scope:
  include: ["src/Noelia.Infrastructure/Security/**", "src/Noelia.Core/Identity/**", "tests/Noelia.Infrastructure.Tests/Security/**", "tests/Noelia.Core.Tests/**", "docs/adr/**", "README.md", "MIGRATION.md", ".monarch/**"]
  exclude: []
estimate: { session_pct: 8, weekly_pct: 3, gold_usd: 0 }
boss: "HTTP-Test über die README-Kette: Person 200, fremde Person 403"
board: { pr: 67 }
---
# ActingForCompany und ActingAsSelf lesen die Person unabhängig von der Reihenfolge (#58)
