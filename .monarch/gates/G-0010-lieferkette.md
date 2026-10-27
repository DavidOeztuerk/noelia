---
id: G-0010
type: standard
title: Lieferkette – Pins, Digests, Update-Bot, Release-Gate (Noelia, WT, CP)
rank: A
risk_tags: [infra]
repo: [Noelia, workertransfer, NoeliaControlPlane]
state: proposed
quests: [Q-0011, Q-0029]
boss: "Alle drei Repos: Actions per SHA, Images per Digest, Renovate aktiv, Vuln-Audit im Release-Pfad"
estimate: { session_pct: 30, weekly_pct: 4, gold_usd: 0 }
board: { provider: github, milestone: 6 }
---
# Lieferkette

Souveränitäts-Ampel „Lieferkette“: WT Rot, Noelia Gelb (05). Warden: WT-8 · SC-3 bestätigt (low; backmerge-Schreibrecht gerechtfertigt), SC-4 bestätigt (low; CPM mit transitivem Pinning mildert), NO-8 teilweise (publish testet selbst; es fehlen Vuln-Audit, Demo-Tests, CodeQL, Dependabot). CP-18 (Rank E, nicht Warden-geprüft) gehört inhaltlich dazu.

| Quest | Titel | Rank | Schwere |
|---|---|---|---|
| Q-0011 | WT: Actions per SHA, Images per Digest, Renovate | A | low |
| Q-0029 | Noelia + CP: Digests, Lockfiles, Renovate, Vuln-Audit vor Publish | A | low |
