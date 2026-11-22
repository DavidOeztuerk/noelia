---
id: Q-0017
gate: G-0006
kind: main
rank: A
severity: low
risk_tags: [auth]
state: cleared
findings: [N1]
scope:
  repo: Noelia
  include: ["demo/**"]
  exclude: []
estimate: { session_pct: 12, weekly_pct: 2, gold_usd: 0 }
dod:
  - "Owner-Entscheid 21.11.2026: Demo zeigt echten Widerruf"
  - "Jeder Token-lesende Demo-Dienst führt UseTokenRevocation in der Kette"
  - "Abmelden beendet alle Sitzungen der Person und sperrt ihre Access-Tokens sofort (Owner-Entscheid 27.10.)"
  - "SignInTests: Access-Token nach dem Abmelden ergibt 401"
  - "README beschreibt die Grenze in Development bei den Microservices (Widerruf je Prozess)"
boss: "Demo SignOut_RevokesAccessToken grün"
evidence: ["demo/src/shared/Demo.Platform/DemoProviders.cs:104-107,131-134", "demo/…/UserService.Api/Program.cs:72-93", "demo/README.md:76,83", "07-warden-noelia.md N1"]
board: { provider: github, issue: 36, pr: 71 }
---
# Demo: Widerruf wirklich nutzen oder README korrigieren

## Objective
Die Demo, die Noelia vorführt, behauptet nichts, was sie nicht tut.

## Definition of Done
- [ ] Review + Freigabe (Rank A wegen auth; reine README-Variante wäre E)
- [ ] Umsetzung der gewählten Variante
- [ ] Boss: siehe Frontmatter
