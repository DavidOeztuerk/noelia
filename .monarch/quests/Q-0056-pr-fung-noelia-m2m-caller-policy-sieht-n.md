---
id: Q-0056
gate: G-0017
kind: main
rank: A
state: cleared
dod:
  - "Design-ADR in einfacher Sprache im PR, Warden (Opus) gibt frei"
  - "Roter Test zuerst: Bericht nach echtem Start zeigt für Aussteller Pass und für reinen Empfänger die tatsächliche Türenzahl, nie Pass über 0 Türen"
  - "Der Bericht (ISecurityCheckReport.Latest) spiegelt den Stand nach dem Aufbau der Routen"
  - "Demo bekommt einen echten Aussteller, security-check-expectations erwartet Pass für M2M"
  - "CI grün"
scope:
  include: ["src/Noelia.Infrastructure/**", "src/Noelia.Application/**", "src/Noelia.Abstractions/**", "tests/Noelia.Infrastructure.Tests/**", "demo/**", "docs/adr/**", ".monarch/**"]
  exclude: []
estimate: { session_pct: 12, weekly_pct: 4, gold_usd: 0 }
boss: "Test startet eine echte WebApplication und liest /noelia/report.json: Aussteller Pass, Empfänger mit AllowAnonymous-Tür Fail"
board: { pr: 67 }
---
# Prüfung noelia.m2m.caller-policy sieht nach einem echten Start die gemappten Türen (#62)
