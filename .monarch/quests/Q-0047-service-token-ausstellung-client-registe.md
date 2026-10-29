---
id: Q-0047
gate: G-0014
kind: main
rank: A
state: cleared
dod:
  - ADR-0002 Abschnitt 1 (Fassung 2) umgesetzt; Tests vorher rot, danach grün (TDD)
  - Jede Ablehnung (ID, Secret, Slot, Audience, Form, Content-Type, Größe, doppelte Felder, grant_type): byte-gleiche 401 invalid_client mit no-store; Längen-/Formprüfung vor dem Hashen
  - Beide Slots gültig; doppelte Hashes, falsches Hash-Format, leere Audience-Menge, Audience = Personen-Audience, symmetrischer oder fehlender Signierschlüssel: kein Start
  - Token: typ noelia-service+jwt, eine aud aus der Code-Menge, client_id, kein sub, Laufzeit 1-15 min (Default 5); typ-Sperrliste in allen Personen-Validierungspfaden
  - Registrierung über noelia.Use(...) mit Contract; Tests prüfen Wirkung, nicht Rückgabe
scope:
  include: ["src/Noelia.Abstractions/Hosting/NoeliaModule.cs", "src/Noelia.Infrastructure/Security/ServiceTokens/**", "src/Noelia.Infrastructure/Security/JwtService.cs", "src/Noelia.Infrastructure/Builder/**", "src/Noelia.Infrastructure/Extensions/ServiceCollectionExtensions.cs", "tests/Noelia.Infrastructure.Tests/Security/ServiceTokens/**", "tests/Noelia.Infrastructure.Tests/Security/JwtService*.cs", "tests/Noelia.Infrastructure.Tests/Builder/**", "tests/Noelia.Infrastructure.Tests/Architecture/**"]
  exclude: []
estimate: { session_pct: 25, weekly_pct: 3, gold_usd: 0 }
boss: "ServiceTokenIssuer-Suite grün in CI, dotnet build ohne Warnungen, PackageDependencyBudgetTests unverändert grün"
board: { pr: 50 }
---
# Service-Token-Ausstellung: Client-Register mit gehashten Secrets, 2 Slots, Token-Endpoint-Baustein
