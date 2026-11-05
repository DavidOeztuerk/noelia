---
id: Q-0055
gate: G-0016
kind: main
rank: A
state: in_progress
dod:
  - Roter Test zuerst: Tür mit echter Kette inklusive UseTokenRevocation und UseNoeliaPrincipal, ein berechtigter Dienst kommt vor dem Fix nicht durch
  - Beide Bausteine erkennen Dienst-Tokens an der geprüften Identität des Dienst-Schemes, nie am fehlenden sub
  - Personen-Token ohne sub wird weiter abgelehnt, mit Test
  - Verhalten von ICurrentPrincipal an einer Dienst-Tür ist entschieden, dokumentiert und getestet
  - Design-ADR in einfacher Sprache im PR, Warden-Runde über Design und Code
  - Version 7.1.1 in beiden Pins, README, MIGRATION und CLAUDE.md nachgezogen
  - CI grün auf dem PR, develop vorher und nachher grün
scope:
  include: ["src/Noelia.Infrastructure/Security/**", "src/Noelia.Core/Identity/**", "tests/Noelia.Infrastructure.Tests/Security/**", "tests/Noelia.Core.Tests/**", "docs/adr/**", "README.md", "MIGRATION.md", "CLAUDE.md", "Directory.Build.props", "demo/Directory.Packages.props", ".monarch/**"]
  exclude: []
estimate: { session_pct: 30, weekly_pct: 8, gold_usd: 0 }
boss: "HTTP-Test über die Kette von WT: berechtigter Dienst 200 an der Tür, Personen-Token ohne sub 401, widerrufenes Personen-Token 401"
---
# Widerruf und Principal lassen geprüfte Dienst-Identität durch, Personen-Token ohne sub bleibt abgelehnt (#57)
