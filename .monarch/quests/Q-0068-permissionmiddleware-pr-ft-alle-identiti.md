---
id: Q-0068
gate: G-0017
kind: main
rank: A
state: abandoned
dod:
  - "Design (ADR-0007) in einfacher Sprache im PR, Warden (Opus) gibt frei"
  - "Roter Test zuerst: unauthentifizierte Identity vorn neben authentifizierter führt nicht zu 401"
  - "Test An_identity_nobody_authenticated_next_to_a_service_is_not_a_person läuft mit PermissionEnforcement"
  - "Kommentar in PrincipalModule.UsePrincipal nach ADR-0006 Entscheidung 5"
  - "CI grün"
scope:
  include: ["src/Noelia.Infrastructure/Middleware/**", "src/Noelia.Infrastructure/Builder/Modules/PrincipalModule.cs", "tests/Noelia.Infrastructure.Tests/**", "docs/adr/**", "MIGRATION.md", ".monarch/**"]
  exclude: []
estimate: { session_pct: 6, weekly_pct: 2, gold_usd: 0 }
boss: "Der Test läuft mit eingeschaltetem Modul grün"
---
# PermissionMiddleware prüft alle Identities auf Authentifizierung (#59)
