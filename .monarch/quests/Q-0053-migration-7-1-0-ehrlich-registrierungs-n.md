---
id: Q-0053
gate: G-0014
kind: side
rank: A
state: in_review
dod:
  - MIGRATION 7.1.0 nennt zusätzlich: AddServiceCommunication überschreibt einen vorher registrierten eigenen IServiceTokenProvider nicht mehr; ServiceTokenCache in der API-Liste; Pipeline-Guard nur für Hosts mit Startup-Filtern (Grenze)
  - Nur Doku, kein Code; CI auf PR
scope:
  include: ["MIGRATION.md", "README.md"]
  exclude: []
estimate: { session_pct: 2, weekly_pct: 0.3, gold_usd: 0 }
boss: "CI auf PR #50 grün nach dem Doku-Commit"
---
# MIGRATION 7.1.0 ehrlich: Registrierungsänderung IServiceTokenProvider, ServiceTokenCache als API, Grenze Pipeline-Guard (Warden-Follow-ups)
