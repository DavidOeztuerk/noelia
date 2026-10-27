---
id: G-0006
type: standard
title: Noelia – Sitzung, Widerruf und Schlüssel-Defaults
rank: A
risk_tags: [auth, public-api]
repo: Noelia
state: proposed
quests: [Q-0016, Q-0026, Q-0017, Q-0018]
boss: "E2E-Suite RevocationPipeline (Bearer → UseTokenRevocation → 401, SignOutEverywhere ohne Grace-Geschwister) grün in CI"
estimate: { session_pct: 60, weekly_pct: 8, gold_usd: 0 }
board: { provider: github, milestone: 2 }
---
# Noelia – Sitzung, Widerruf und Schlüssel-Defaults

Der Kern der ursprünglichen JWT-Behauptung (02-Z1/Z2/Z3) und was davon heute noch gilt: Noelia fällt ohne Keys auf HS256 mit `JWT_SECRET` zurück (02-N7, vom Orchestrator gegengelesen `KeyRingFactory.cs:38-45`); ein rotiertes Refresh-Token erzeugt im 15-s-Grace-Fenster nach SignOut noch ein Geschwister (NO-1); die Demo bewirbt Valkey-Widerruf, nutzt ihn nie (N1); es fehlen HTTP-E2E-Tests für Widerruf und Authority-Pfad (NO-13).

**Warden-Stand:** NO-1, N1, NO-13 bestätigt (`07-warden-noelia.md`); NO-15 (alg aus Header) verworfen → kein Algorithm-Confusion-Befund.
**Policy (Rank A):** Design → Review → Freigabe → Implementierung; Breaking Change der Defaults → Major-Version + MIGRATION.md.

| Quest | Titel | Rank | Schwere |
|---|---|---|---|
| Q-0016 | Grace-Fenster respektiert Sitzungsende | A | medium |
| Q-0026 | Shared-Secret-Fallback nur mit ausdrücklichem Opt-in | A | medium |
| Q-0017 | Demo: Widerruf wirklich nutzen oder README korrigieren | A | low |
| Q-0018 | E2E-Tests Widerruf und Authority-Pfad | D | low |
