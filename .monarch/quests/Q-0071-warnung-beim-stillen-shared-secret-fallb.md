---
id: Q-0071
gate: G-0006
kind: main
rank: A
state: in_review
dod:
  - "Baut der Dienst seinen Schlüsselring still aus JWT_SECRET bzw. JwtSettings:Secret, schreibt er beim Start eine Warnung, die den Weg zu ausdrücklichen Schlüsseln nennt (ohne Geheimniswerte)"
  - "Mit ausdrücklichen Schlüsseln oder Authority keine Warnung (Test für beide Seiten)"
  - "Kein Bruch: Verhalten sonst unverändert; MIGRATION kündigt die Entfernung für 8.0 an (G-0019, Q-0026)"
scope:
  include: ["src/Noelia.Infrastructure/Security/Keys/**", "src/Noelia.Infrastructure/**/JwtModule.cs", "src/Noelia.Infrastructure/**/JwtBuilder.cs", "src/Noelia.Infrastructure/Extensions/**", "tests/Noelia.Infrastructure.Tests/**", "docs/adr/**", "MIGRATION.md", "README.md"]
  exclude: []
estimate: { session_pct: 6, weekly_pct: 1, gold_usd: 0 }
boss: "SharedSecretFallback_LogsWarningAtStart grün in CI"
---
# Warnung beim stillen Shared-Secret-Fallback (7.2.0)
