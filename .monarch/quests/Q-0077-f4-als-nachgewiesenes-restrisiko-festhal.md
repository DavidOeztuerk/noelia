---
id: Q-0077
gate: G-0020
kind: main
rank: A
state: cleared
dod:
  - "ADR-0009 hat den Status Verworfen (Owner 22.11.) mit dem Grund; ADR-0008 hat den Nachtrag 'auf PostgreSQL geprüft' (F4 rot, Restrisiko angenommen, Rat im README gilt), Wortlaut der Owner-Entscheide unverändert"
  - "README und MIGRATION nennen F4 als auf PostgreSQL nachgewiesen und SQL Server als ungeprüft"
  - "Die roten F4-Tests mit Klammer sind aus dem Branch genommen (bleiben in Commit 4abaf8b und im Befund); Kontrollen, Markierungskonflikt und Index-Test bleiben"
scope:
  include: ["docs/adr/0008-sitzungsende-und-widerruf-je-person.md", "docs/adr/0009-*.md", "docs/befunde/postgresql-beweis-adr-0008.md", "README.md", "MIGRATION.md", "tests/Noelia.Infrastructure.Tests/Security/Sessions/PostgresRefreshTokenStoreProofTests.F4.cs"]
  exclude: ["src/**"]
estimate: { session_pct: 6, weekly_pct: 1, gold_usd: 0 }
boss: "dotnet test Noelia.slnx vollständig grün inkl. Category=Integration"
board: { pr: 76 }
---
# F4 als nachgewiesenes Restrisiko festhalten (ADR-0008 Nachtrag, ADR-0009 verworfen, README, MIGRATION)
