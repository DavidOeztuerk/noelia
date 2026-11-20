---
id: Q-0063
gate: G-0017
kind: main
rank: A
state: proposed
dod:
  - "Roter Test zuerst: unauthentifizierte Identity vorn neben authentifizierter führt nicht zu 401"
  - "Test An_identity_nobody_authenticated_next_to_a_service_is_not_a_person läuft mit PermissionEnforcement"
  - "CI grün"
scope:
  include: ["src/Noelia.Infrastructure/Middleware/**", "tests/Noelia.Infrastructure.Tests/**", ".monarch/**"]
  exclude: []
estimate: { session_pct: 5, weekly_pct: 2, gold_usd: 0 }
boss: "Der Test läuft mit eingeschaltetem Modul grün"
---
# PermissionMiddleware prüft alle Identities auf Authentifizierung (#59)
