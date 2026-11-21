---
id: Q-0075
gate: G-0020
kind: main
rank: A
state: proposed
dod:
  - "Die roten F4-Tests (Deadlock x5, Generationen) bleiben unverändert und sind grün"
  - "Kontrollen und alle Sitzungs-Tests (SQLite, PostgreSQL) grün"
  - "MIGRATION.md nennt die Verhaltensänderung für Anwendungen mit eigener Transaktion; README-Rat angepasst; SQL Server als ungeprüft benannt"
scope:
  include: ["src/Noelia.Data.EntityFrameworkCore/**", "src/Noelia.Infrastructure/Security/Sessions/**", "src/Noelia.Abstractions/Security/Sessions/**", "tests/Noelia.Infrastructure.Tests/**", "MIGRATION.md", "README.md", "docs/adr/0008-sitzungsende-und-widerruf-je-person.md", "docs/adr/0009-*.md", "docs/befunde/postgresql-beweis-adr-0008.md"]
  exclude: []
estimate: { session_pct: 15, weekly_pct: 4, gold_usd: 0 }
boss: "dotnet test Noelia.slnx vollständig grün inkl. Category=Integration"
---
# F4 technisch schließen (Umsetzung nach ADR-0009)
