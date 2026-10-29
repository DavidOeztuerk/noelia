---
id: Q-0046
gate: G-0014
kind: main
rank: A
state: cleared
dod:
  - ADR deckt Ausstellung ab: Client-ID + gehashtes Secret je Dienst, 2 Rotations-Slots, kurze Laufzeit, Audience, Aufrufer-Claim, Token-Endpoint als einbindbarer Baustein
  - ADR deckt Empfänger-Policy je Endpoint ab: nur Aufrufer X, Y; einheitliches 404, für Nicht-Berechtigte nicht unterscheidbar (Lücke 'nur Rolle Service' ausgeschlossen)
  - ADR deckt Client-Seite über ServiceTokenProvider, Infisical-taugliche Env-Namen, rein additiv (7.1.0) und offen benanntes Bedrohungsmodell ab
  - Warden-Design-Review freigegeben (max. 1 Delta-Runde), User-Freigabe über projects-74
scope:
  include: ["docs/adr/0002-*.md", ".monarch/knowledge/**"]
  exclude: []
estimate: { session_pct: 15, weekly_pct: 2, gold_usd: 0 }
boss: "ADR docs/adr/0002 im Draft-PR nach develop, Warden-Verdict approve, User-Freigabe dokumentiert"
board: { pr: 50 }
---
# Design-ADR M2M: Ausstellung, Empfänger-Policy, Client, Env-Namen, Bedrohungsmodell
