---
id: Q-0070
gate: G-0018
kind: main
rank: A
state: in_progress
dod:
  - "Umsetzung nach ADR-0007 Entscheidungen 1 bis 9; der Prüfplan des ADR ist die Definition of Done"
  - "Rot-Tests vorher, grün nachher, je Zeile mit Mutationsprobe: Middleware und Handler, Matrix {permission, Rolle, *, cat:*, Permission mit Resource} x {unangemeldet vorn, hinten}: 403, nur unangemeldet 401"
  - "Katalog- und Rollen-Policies, Datensatz-/Besitz-Handler, Kontostand, Mandant/Principal, Audit: je mit unangemeldeter Identität vorn/hinten"
  - "Widerruf je Personen-Identität einzeln inkl. Sitzung, zwei Personen-Schemes; Datensatzprüfung fail-closed"
  - "IClaimsTransformation: zusätzliche Identität ohne AuthenticationType zählt nicht, Weg über die Personen-Identität getestet, Dienst-Principal unberührt"
  - "MIGRATION.md (Bruch, Weg, IPrincipalFactory, 401 bei zweiter Identität, eigener Code), README, Befund unter docs/befunde/; kein Release, keine Versionserhöhung"
scope:
  include: ["src/Noelia.Infrastructure/Middleware/**", "src/Noelia.Infrastructure/Authorization/**", "src/Noelia.Infrastructure/Security/**", "src/Noelia.Infrastructure/Builder/**", "tests/Noelia.Infrastructure.Tests/**", "README.md", "MIGRATION.md", "docs/befunde/**"]
  exclude: []
estimate: { session_pct: 25, weekly_pct: 4, gold_usd: 0 }
---
# Fix #59: nur angemeldete Identitäten zählen in PermissionMiddleware und PermissionAuthorizationHandler (TDD)
