---
id: G-0007
type: standard
title: Ehrliche Prüfregeln und Attestation (Noelia + Control Plane)
rank: A
risk_tags: [auth, public-api]
repo: [Noelia, NoeliaControlPlane]
state: proposed
quests: [Q-0019, Q-0020, Q-0030, Q-0031]
boss: "Dogfooding-Lauf: Demo ohne UseTokenRevocation zeigt NICHT Pass; leerer Dienst-Report zeigt NICHT Complete; /attestation/verify ohne Pin zeigt NICHT valid"
estimate: { session_pct: 60, weekly_pct: 8, gold_usd: 0 }
board: { provider: github, milestone: 3 }
---
# Ehrliche Prüfregeln und Attestation

Das Produktversprechen der Control Plane – „wie souverän arbeitet ein Kunde?“ – hängt daran, dass ein grünes Ergebnis etwas bedeutet. Der Selbstcheck (05) zeigt: 16 von 17 Regeln messen nur Konfiguration, 8 melden Pass trotz abweichender Realität; die CP hat kein Soll je Dienst und signiert Aussagen, die sie nicht geprüft hat.

**Warden-Stand:** N2 · NO-9 · SC-1 bestätigt (Warden-Rank C, medium); NO-3 · X-6 bestätigt (B, low; Ergebnis NotApplicable, nicht Pass); CP-12 bestätigt (C, low); CP-13 bestätigt (B, medium); CP-15 bestätigt (B, medium, dokumentiert, aber Falle).
**Rank-Hinweis:** Gate auf A angehoben (Policy: Security-Prüfcode = Auth-/Security-Code), Warden-Ranks je Quest vermerkt.

| Quest | Titel | Rank | Schwere |
|---|---|---|---|
| Q-0019 | Prüfstatus „unbelegt“ statt Pass; Widerrufsregel erkennt Pipeline | A | medium |
| Q-0031 | Attestation: nur Geprüftes signieren, verify nur mit Vertrauen | B | medium |
| Q-0020 | Refresh-Cookie-Regel sieht Konsumenten-Cookies | B | low |
| Q-0030 | Soll an Checks je Dienst in der CP | C | low |
