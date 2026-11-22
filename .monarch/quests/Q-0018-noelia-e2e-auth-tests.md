---
id: Q-0018
gate: G-0006
kind: main
rank: D
severity: low
risk_tags: [auth]
state: cleared
findings: [NO-13]
scope:
  repo: Noelia
  include: ["tests/**"]
  exclude: ["src/**"]
estimate: { session_pct: 8, weekly_pct: 1, gold_usd: 0 }
dod:
  - "JwtBearer → UseTokenRevocation → widerrufenes Token → 401: schon durch G-0016 belegt (ServiceCallerFullChainTests.A_revoked_person_token_is_refused), nur verweisen"
  - "Authority-Pfad mit lokalem JWKS-Stub: falsches alg, unbekanntes kid, falscher iss/aud → 401"
  - "Sitzungs-Widerruf (RevokeSessionAsync) über die echte Kette → 401 (Regressionsschutz für 02-R14)"
  - "SignOutEverywhere ohne Grace-Geschwister über TokenSessionService"
  - "Gegenprobe gegen eine absichtlich kaputte Kette"
boss: "RevocationPipeline_E2E + AuthorityPath_RejectsForeignAlgKidIss grün in CI"
evidence: ["tests/…/Authorization/PermissionAuthorizationTests.cs:288-443", "tests/…/Builder/ExternalIdentityProviderTests.cs:26-98", "07-warden-noelia.md NO-13"]
board: { provider: github, issue: 37, pr: 71 }
---
# E2E-Tests Widerruf und Authority-Pfad

## Objective
Fehler wie 02-R14 (Claim-Name) oder N2 (Middleware fehlt) fallen in CI auf, nicht erst in einer Session.

## Definition of Done
- [ ] Standard-Shadow (Sentinel) + 1 Verifier (Rank D), nur `tests/**`
- [ ] Tests geschrieben, schlagen gegen eine absichtlich kaputte Pipeline fehl (Gegenprobe)
- [ ] Boss: beide Suiten grün in CI
