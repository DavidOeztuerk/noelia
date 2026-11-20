---
id: Q-0070
gate: G-0018
kind: main
rank: A
state: proposed
dod:
  - "Rot-Tests vorher, grün nachher, für Middleware und Handler"
  - "Matrix {permission, Rolle, *, cat:*} × {unangemeldet vorn, hinten} ergibt 403, nur unangemeldet 401"
  - "Widerrufene Person kommt nicht durch (Widerruf und Prüfung je Datensatz)"
  - "Claims aus IClaimsTransformation ohne AuthenticationType zählen nicht; Test zeigt den richtigen Weg"
  - "MIGRATION.md (Bruch, Weg) und Befund unter docs/befunde/; kein Release, keine Versionserhöhung"
scope:
  include: ["src/Noelia.Infrastructure/Middleware/**", "src/Noelia.Infrastructure/Authorization/**", "src/Noelia.Infrastructure/Security/**", "src/Noelia.Infrastructure/Builder/**", "tests/Noelia.Infrastructure.Tests/**", "README.md", "MIGRATION.md", "docs/befunde/**"]
  exclude: []
estimate: { session_pct: 20, weekly_pct: 3, gold_usd: 0 }
---
# Fix #59: nur angemeldete Identitäten zählen in PermissionMiddleware und PermissionAuthorizationHandler (TDD)
