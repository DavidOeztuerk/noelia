---
id: Q-0017
gate: G-0006
kind: main
rank: A
severity: low
risk_tags: [auth]
state: proposed
findings: [N1]
scope:
  repo: Noelia
  include: ["demo/src/services/UserService/UserService.Api/Program.cs", "demo/src/monolith/Demo.Monolith.Api/Program.cs", "demo/src/services/TodoService/**/Program.cs", "demo/src/shared/Demo.Platform/DemoProviders.cs", "demo/README.md", "demo/tests/Demo.Tests/SignInTests.cs"]
  exclude: []
estimate: { session_pct: 10, weekly_pct: 1.5, gold_usd: 0 }
dod:
  - Entscheidung: Demo zeigt Widerruf (UseTokenRevocation + Revoke beim Sign-out) ODER README sagt klar „optional, in der Demo nicht aktiv“
  - SignInTests.cs:146-165 entsprechend angepasst (Token nach Sign-out → 401, falls aktiv)
boss: "Demo SignOut_RevokesAccessToken_InAllServices grün (falls aktiv) bzw. README-Prüfung im Release-Gate"
evidence: ["demo/src/shared/Demo.Platform/DemoProviders.cs:104-107,131-134", "demo/…/UserService.Api/Program.cs:72-93", "demo/README.md:76,83", "07-warden-noelia.md N1"]
board: { provider: github, issue: 36 }
---
# Demo: Widerruf wirklich nutzen oder README korrigieren

## Objective
Die Demo, die Noelia vorführt, behauptet nichts, was sie nicht tut.

## Definition of Done
- [ ] Review + Freigabe (Rank A wegen auth; reine README-Variante wäre E)
- [ ] Umsetzung der gewählten Variante
- [ ] Boss: siehe Frontmatter
