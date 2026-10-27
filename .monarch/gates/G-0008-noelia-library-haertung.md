---
id: G-0008
type: standard
title: Noelia – Härtung der Library-Defaults und Opt-in-Pfade
rank: A
risk_tags: [auth, infra, pii]
repo: Noelia
state: proposed
quests: [Q-0021, Q-0022, Q-0023, Q-0024]
boss: "Noelia-Security-Suite (ReservedClaims, QueryTokenPaths, AuditRedaction, EgressIpGuard, NoExceptionLeak) grün in CI"
estimate: { session_pct: 50, weekly_pct: 7, gold_usd: 0 }
board: { provider: github, milestone: 4 }
---
# Noelia – Härtung der Library-Defaults und Opt-in-Pfade

Alle Befunde sind vom Warden bestätigt bzw. teilweise bestätigt und durchweg **low**, weil sie Opt-in-Pfade ohne bekannten Konsumenten betreffen (`07-warden-noelia.md`). Für eine Open-Source-Library, die mit Souveränität und Sicherheit wirbt, sind sichere Defaults trotzdem Teil des Versprechens.

| Quest | Titel | Befunde | Rank | Schwere |
|---|---|---|---|---|
| Q-0021 | Sperrliste reservierter Claim-Namen | NO-4 | A | low |
| Q-0022 | Token-Leckfläche: Query-Token nur exakte WS-Pfade, eine Audit-Middleware mit Redaktion | NO-10, NO-2, Neu-1 | A | low |
| Q-0023 | Egress-Guard prüft aufgelöste IP; OpenBao nur https außerhalb localhost | NO-5, NO-6 | A | low |
| Q-0024 | Keine Fehlertexte nach außen (M2M-Log, Health-Description) | NO-11, NO-12, Neu-2 | A | low |
