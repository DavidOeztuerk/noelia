---
id: Q-0058
gate: G-0017
kind: main
rank: B
state: abandoned
dod:
  - "Roter Test zuerst: Schema 3 gilt nicht als neuer als das Tool"
  - "Egress-Befund stützt sich auf httpEgress.isEnforcing, nicht auf das Legacy-Feld"
  - "CI grün"
scope:
  include: ["src/Noelia.Cli/**", "tests/Noelia.Cli.Tests/**", ".monarch/**"]
  exclude: []
estimate: { session_pct: 6, weekly_pct: 2, gold_usd: 0 }
boss: "analyze --url gegen einen Schema-3-Report ohne installierten Guard liefert einen Befund"
---
# noelia analyze --url kennt Report-Schema 3 und wertet egressIsEnforced nicht als Beleg (#53)
