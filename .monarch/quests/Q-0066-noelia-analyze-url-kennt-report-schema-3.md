---
id: Q-0066
gate: G-0017
kind: main
rank: B
state: in_progress
dod:
  - "Roter Test zuerst: Schema 3 gilt nicht als neuer als das Tool; Konstante des Tools gleich OperatorReport.SchemaVersion"
  - "Egress-Befund stützt sich bei Schema 3 auf httpEgress.isEnforcing, nicht auf das Legacy-Feld"
  - "Schema 2 behält das alte Verhalten"
  - "CI grün"
scope:
  include: ["src/Noelia.Cli/**", "tests/Noelia.Infrastructure.Tests/Architecture/**", ".monarch/**"]
  exclude: []
estimate: { session_pct: 6, weekly_pct: 2, gold_usd: 0 }
boss: "Interpret eines Schema-3-Reports mit konfigurierter Policy und nicht installiertem Guard liefert einen Befund"
---
# noelia analyze --url kennt Report-Schema 3 und wertet egressIsEnforced nicht als Beleg (#53)
