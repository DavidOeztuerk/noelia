---
id: G-0013
type: standard
title: Verträge und Versionen zwischen Noelia, Workertransfer und Control Plane
rank: B
risk_tags: [public-api]
repo: [Noelia, workertransfer, NoeliaControlPlane]
state: proposed
quests: [Q-0041]
boss: "Gemeinsame Vertrags-Fixture läuft in allen drei CI-Pipelines grün"
estimate: { session_pct: 20, weekly_pct: 3, gold_usd: 0 }
board: { provider: github, milestone: 7 }
---
# Verträge und Versionen

Der Session-Claim-Fehler 02-R14 (`session_id` geschrieben, `sid` gelesen) hätte ein gemeinsamer Vertragstest gefunden. **Warden:** X-1 bestätigt (B/low; Typ-Kopplung über `Noelia.Abstractions` ist kein Roundtrip), X-2 teilweise (Header + 15 min dupliziert; `session_id` hat bereits eine öffentliche Konstante, WT nutzt bewusst `sid`), X-7 bestätigt (CP 7.0.0 vs. WT/Demo 7.0.1), X-8 bestätigt (drei Severity-Vokabulare ohne Mapping-Doku). **X-12 verworfen** (Präfixwechsel in `MIGRATION.md:1050-1062` dokumentiert).
