---
id: Q-0049
gate: G-0014
kind: main
rank: A
state: cleared
dod:
  - ADR-0002 Abschnitte 3-5 umgesetzt; Tests vorher rot, danach grün (TDD)
  - Ein Token je Audience, nur im Prozess gecacht, nie IDistributedCache; Fehlschlag wirft, kein unauthentifizierter Aufruf
  - Legacy unverändert Default; bestehende ServiceTokenProvider-Tests unverändert grün
  - README (NOELIA_M2M_*, Rotation, Grenzen), MIGRATION 7.1.0 additiv, VersionPrefix 7.1.0; End-to-end-Test Aussteller+Client+Empfänger
scope:
  include: ["src/Noelia.Abstractions/Hosting/NoeliaModule.cs", "src/Noelia.Infrastructure/Security/M2M/**", "src/Noelia.Infrastructure/Security/ServiceTokens/**", "src/Noelia.Infrastructure/Communication/**", "src/Noelia.Infrastructure/Builder/**", "tests/Noelia.Infrastructure.Tests/Security/M2M/**", "tests/Noelia.Infrastructure.Tests/Security/ServiceTokens/**", "tests/Noelia.Infrastructure.Tests/Communication/**", "tests/Noelia.Infrastructure.Tests/Builder/**", "README.md", "MIGRATION.md", "Directory.Build.props", "docs/adr/0002-*.md"]
  exclude: []
estimate: { session_pct: 20, weekly_pct: 2.5, gold_usd: 0 }
boss: "End-to-end M2M-Test (Client holt Token, Empfänger lässt nur Aufrufer X durch) grün in CI; develop vor und nach gate merge grün"
board: { pr: 50 }
---
# Client: ServiceTokenProvider im Modus ClientCredentials, IServiceTokenSource, AddServiceToken, Doku, MIGRATION, 7.1.0
