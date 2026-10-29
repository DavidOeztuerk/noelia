---
id: Q-0047
gate: G-0014
kind: main
rank: A
state: proposed
dod:
  - ADR-0002 Abschnitt 1 umgesetzt; Tests vorher rot, danach grün (TDD)
  - Unbekannte ID, falsches Secret, leerer Slot, verbotene Audience, kaputter Body: byte-gleiche 401 invalid_client mit no-store
  - Beide Slots gleichzeitig gültig; doppelte Hashes, falsches Hash-Format, symmetrischer oder fehlender Signierschlüssel: kein Start
  - Token: typ noelia-service+jwt, genau eine aud, client_id, kein sub, Laufzeit 1-15 min (Default 5); Personen-Scheme lehnt diesen typ ab
  - Registrierung über noelia.Use(...) mit Contract; Tests prüfen Wirkung, nicht Rückgabe
scope:
  include: ["src/Noelia.Abstractions/Hosting/NoeliaModule.cs", "src/Noelia.Infrastructure/Security/ServiceTokens/**", "src/Noelia.Infrastructure/Builder/**", "src/Noelia.Infrastructure/Extensions/ServiceCollectionExtensions.cs", "tests/Noelia.Infrastructure.Tests/Security/ServiceTokens/**", "tests/Noelia.Infrastructure.Tests/Builder/**", "tests/Noelia.Infrastructure.Tests/Architecture/**"]
  exclude: []
estimate: { session_pct: 25, weekly_pct: 3, gold_usd: 0 }
boss: "ServiceTokenIssuer-Suite grün in CI, dotnet build ohne Warnungen, PackageDependencyBudgetTests unverändert grün"
---
# Service-Token-Ausstellung: Client-Register mit gehashten Secrets, 2 Slots, Token-Endpoint-Baustein
