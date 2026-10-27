---
id: Q-0026
gate: G-0006
kind: main
rank: A
severity: medium
risk_tags: [auth, public-api]
state: proposed
findings: [02-Z1, 02-N7]
verified_by: orchestrator (nicht Warden; Original-Rank D lag unter der Warden-Schwelle)
scope:
  repo: Noelia
  include: ["src/Noelia.Infrastructure/Security/Keys/KeyRingFactory.cs", "src/Noelia.Infrastructure/Security/Keys/KeyRing.cs", "src/**/JwtModule.cs", "src/**/Builder/JwtBuilder.cs", "tests/**/Keys/**", "MIGRATION.md", "README.md"]
  exclude: []
estimate: { session_pct: 18, weekly_pct: 2.5, gold_usd: 0 }
dod:
  - Ohne übergebene Keys und ohne ausdrückliches FromSharedSecret startet die App mit klarer Fehlermeldung (kein stiller HS256-Fallback aus JWT_SECRET)
  - KeyRing verweigert gemischte symmetrische/asymmetrische Schlüssel
  - Breaking Change → Major-Version, MIGRATION-Abschnitt, Check noelia.jwt.key-separation unverändert grün für ES256-Konsumenten
boss: "AddJwtAuthentication_WithoutKeys_FailsFast + KeyRing_RejectsMixedKeyTypes grün in CI"
evidence: ["src/Noelia.Infrastructure/Security/Keys/KeyRingFactory.cs:38-45 (gegengelesen 2026-10-26)", "KeyRing.cs:27-47", "02-jwt-claim.md Z1, N7"]
board: { provider: github, issue: 44 }
---
# Shared-Secret-Fallback nur mit ausdrücklichem Opt-in

## Objective
Das Muster aus der ursprünglichen Behauptung („jeder Dienst kann Tokens ausstellen“) kann bei einem neuen Noelia-Konsumenten nicht mehr aus Versehen entstehen. WT und Demo sind heute nicht betroffen (ES256, nur ein Aussteller).

## Definition of Done
- [ ] Design → Review → Freigabe (auth + public-api Breaking, Rank A)
- [ ] Fail-fast + Mixed-Key-Guard + Doku
- [ ] Boss: beide Tests grün in CI
