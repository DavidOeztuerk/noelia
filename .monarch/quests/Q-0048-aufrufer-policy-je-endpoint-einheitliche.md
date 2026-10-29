---
id: Q-0048
gate: G-0014
kind: main
rank: A
state: in_review
dod:
  - ADR-0002 Abschnitt 2 (Fassung 2) umgesetzt; Tests vorher rot, danach grün (TDD)
  - Matrix mit voller UseNoelia-Kette: berechtigt 2xx; alle Ablehnungsfälle des Testplans 404 byte-gleich zu nicht existierendem Pfad derselben Gruppe (auch mit UseStatusCodePages), Minimal API und Controller
  - Leere Aufruferliste wirft; kein Wildcard; client_id nur aus der NoeliaService-Identity; Audit mit Grundklasse, nie Token-Werte
  - Check noelia.m2m.caller-policy: ohne Module Pass; AllowAnonymous, fremder Handler, Metadaten-Abweichung Tür/Fallback Fail; Token-Endpoint ohne Rate-Limit Warn
scope:
  include: ["src/Noelia.Abstractions/Hosting/NoeliaModule.cs", "src/Noelia.Infrastructure/Security/ServiceTokens/**", "src/Noelia.Infrastructure/Security/Checks/**", "src/Noelia.Infrastructure/Builder/**", "src/Noelia.Infrastructure/Extensions/ServiceCollectionExtensions.cs", "tests/Noelia.Infrastructure.Tests/Security/ServiceTokens/**", "tests/Noelia.Infrastructure.Tests/Security/Checks/**", "tests/Noelia.Infrastructure.Tests/Dashboard/**", "tests/Noelia.Infrastructure.Tests/Builder/**", "tests/Noelia.Infrastructure.Tests/Architecture/**"]
  exclude: []
estimate: { session_pct: 25, weekly_pct: 3, gold_usd: 0 }
boss: "Integrationstest Empfänger-Matrix + 404-Vertrag grün in CI"
---
# Aufrufer-Policy je Endpoint (einheitliches 404), Client-Anbindung ServiceTokenProvider, Doku und 7.1.0
