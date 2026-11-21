---
id: Q-0073
gate: G-0020
kind: main
rank: A
state: in_progress
dod:
  - "F4-Test deterministisch über Interceptoren und pg_stat_activity, vier Fälle plus Kontrolle ohne Klammer, Deadlock-Zähler belegt den Zyklus"
  - "N5 und Index-Test auf PostgreSQL"
  - "Befund unter docs/befunde, ADR-0008 Nachtrag nur bei durchweg grünen Tests; offen für SQL Server benannt"
scope:
  include: ["tests/Noelia.Infrastructure.Tests/**", "docs/befunde/postgresql-beweis-adr-0008.md", "docs/adr/0008-sitzungsende-und-widerruf-je-person.md"]
  exclude: ["src/**"]
estimate: { session_pct: 12, weekly_pct: 3, gold_usd: 0 }
boss: "Alle neuen Tests ausgeführt; bei Rot Stopp und Befund mit Testname und Ausgabe an den Owner"
---
# F4-Test, N5 und Index (SubjectId, RevokedAt) auf PostgreSQL
