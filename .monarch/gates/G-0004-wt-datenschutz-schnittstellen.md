---
id: G-0004
type: standard
title: Workertransfer – Datenschutz-Schnittstellen, Verträge, Egress
rank: A
risk_tags: [pii, public-api]
repo: workertransfer
state: proposed
quests: [Q-0012, Q-0013, Q-0014]
boss: "ConsentCheck_DeniesUnrelatedCaller + Fehlercode-Vertragstest grün in CI"
estimate: { session_pct: 45, weekly_pct: 6, gold_usd: 0 }
board: { provider: github, milestone: 1 }
---
# Workertransfer – Datenschutz-Schnittstellen, Verträge, Egress

Consent-Orakel für jeden angemeldeten Nutzer (WT-11, Warden A/medium), API↔UI über Fehlertexte statt Codes, unversionierte Routen (WT-12, B/low), Egress-Allowlist ohne Jurisdiktionssperre (SC-2, B/low).

| Quest | Titel | Rank | Schwere |
|---|---|---|---|
| Q-0012 | Consent-Abfragen nur für berechtigte Aufrufer | A | medium |
| Q-0013 | Maschinenlesbare Fehlercodes, Versionspräfix | B | low |
| Q-0014 | Egress: Drittland nur mit ausdrücklicher Freigabe | B | low |
