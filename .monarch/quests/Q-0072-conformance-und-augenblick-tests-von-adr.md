---
id: Q-0072
gate: G-0020
kind: main
rank: A
state: in_review
dod:
  - "RefreshTokenStoreConformance läuft zusätzlich gegen PostgreSQL (Testcontainers, Category=Integration), wirft ohne Container-Runtime statt zu überspringen"
  - "Augenblick-Tests als abstrakte Basis mit SQLite- und PostgreSQL-Erbe; Zahl und Namen der SQLite-Tests unverändert"
  - "Unter src/ ändert sich nichts"
scope:
  include: ["tests/Noelia.Infrastructure.Tests/**", "Directory.Packages.props", "docs/befunde/postgresql-beweis-adr-0008.md"]
  exclude: ["src/**"]
estimate: { session_pct: 12, weekly_pct: 3, gold_usd: 0 }
boss: "dotnet test --filter Category=Integration gegen PostgreSQL: Ergebnis je Test festgehalten, rote Tests unverändert gemeldet"
---
# Conformance und Augenblick-Tests von ADR-0008 gegen PostgreSQL
