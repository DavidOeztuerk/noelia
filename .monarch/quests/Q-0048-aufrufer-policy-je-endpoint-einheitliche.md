---
id: Q-0048
gate: G-0014
kind: main
rank: A
state: proposed
dod:
  - ADR-0002 Abschnitt 2 umgesetzt; Tests vorher rot, danach grün (TDD)
  - Matrix Aufrufer x Tür: berechtigt 2xx; kein/kaputtes/abgelaufenes Token, falsche aud, Personen-Token (auch mit client_id-Claim), falsche Methode, kaputter Body: 404 byte-gleich zu nicht existierendem Pfad (auch mit UseStatusCodePages)
  - Leere Aufruferliste wirft beim Mappen; kein Wildcard; client_id nur aus der NoeliaService-Identity
  - Check noelia.m2m.caller-policy: AllowAnonymous+ServiceCaller und fremder Result-Handler ergeben Fail; Summary ohne Werte
scope:
  include: ["src/Noelia.Abstractions/Hosting/NoeliaModule.cs", "src/Noelia.Infrastructure/Security/ServiceTokens/**", "src/Noelia.Infrastructure/Security/Checks/**", "src/Noelia.Infrastructure/Builder/**", "src/Noelia.Infrastructure/Extensions/ServiceCollectionExtensions.cs", "tests/Noelia.Infrastructure.Tests/Security/ServiceTokens/**", "tests/Noelia.Infrastructure.Tests/Security/Checks/**", "tests/Noelia.Infrastructure.Tests/Builder/**", "tests/Noelia.Infrastructure.Tests/Architecture/**"]
  exclude: []
estimate: { session_pct: 25, weekly_pct: 3, gold_usd: 0 }
boss: "Integrationstest Empfänger-Matrix + 404-Vertrag grün in CI"
---
# Aufrufer-Policy je Endpoint (einheitliches 404), Client-Anbindung ServiceTokenProvider, Doku und 7.1.0
