---
id: Q-0020
gate: G-0007
kind: side
rank: B
severity: low
risk_tags: [public-api]
state: proposed
findings: [NO-3, X-6]
scope:
  repo: Noelia
  include: ["src/Noelia.Infrastructure/Security/Checks/BuiltInSecurityChecks.cs", "src/**/TokenSessions/**", "demo/src/services/UserService/UserService.Api/RefreshCookie.cs", "tests/**/Checks/**"]
  exclude: []
estimate: { session_pct: 10, weekly_pct: 1.5, gold_usd: 0 }
dod:
  - Konsumenten deklarieren ihr Refresh-Cookie (Name, Flags) über eine Noelia-API; die Regel prüft die Deklaration und eine Sonde der Set-Cookie-Antwort
  - Ein dokumentiertes empfohlenes Cookie-Modell in Noelia (WT- und Demo-Modell darauf abbilden)
boss: "RefreshCookieCheck_DetectsInsecureConsumerCookie grün in CI"
evidence: ["BuiltInSecurityChecks.cs:440-460", "demo/…/RefreshCookie.cs:22-41", "WT …/AuthEndpoints.cs:603-623", "07-warden-noelia.md NO-3 · X-6"]
board: { provider: github, issue: 39 }
---
# Refresh-Cookie-Regel sieht Konsumenten-Cookies

## Objective
Die Regel meldet für WT und Demo nicht mehr NotApplicable, sondern bewertet die tatsächlich gesetzten Cookies.

## Definition of Done
- [ ] Plan-Freigabe (Rank B)
- [ ] Deklarations-API + Regel + Doku
- [ ] Boss: `RefreshCookieCheck_DetectsInsecureConsumerCookie` grün in CI
