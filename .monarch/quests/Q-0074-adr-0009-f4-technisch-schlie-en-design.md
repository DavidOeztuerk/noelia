---
id: Q-0074
gate: G-0020
kind: main
rank: A
state: in_progress
dod:
  - "ADR-0009 prüft Markierung über eigene Verbindung und Schließen außerhalb der Transaktion der Anwendung gegen den Beweis und wählt eins"
  - "Beide F4-Mechanismen (Deadlock, Generationen) sind im Design als grün nach dem Fix begründet; neue öffentliche API nur wenn nötig, klein und begründet"
  - "Owner-Entscheide wörtlich, einfache Sprache, Warden approve"
scope:
  include: ["docs/adr/0009-*.md", "docs/adr/0008-sitzungsende-und-widerruf-je-person.md", "docs/befunde/postgresql-beweis-adr-0008.md"]
  exclude: ["src/**"]
estimate: { session_pct: 10, weekly_pct: 2, gold_usd: 0 }
boss: "Warden-Design-Runde mit approve (höchstens eine Delta-Runde)"
---
# ADR-0009: F4 technisch schließen (Design)
