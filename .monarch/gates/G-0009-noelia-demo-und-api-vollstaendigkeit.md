---
id: G-0009
type: standard
title: Noelia – Demo-Infrastruktur und API-Vollständigkeit
rank: B
risk_tags: [infra, public-api]
repo: Noelia
state: proposed
quests: [Q-0027, Q-0028]
boss: "Demo-Compose mit Valkey-ACL startet in allen Profilen; Compliance-Ports implementiert oder als Seam dokumentiert"
estimate: { session_pct: 22, weekly_pct: 3, gold_usd: 0 }
board: { provider: github, milestone: 5 }
---
# Noelia – Demo-Infrastruktur und API-Vollständigkeit

Valkey in der Demo ohne ACL, alle Dienste schreiben (NO-7, A/low, nur Demo); öffentliche Compliance-Ports ohne Implementierung, Degradations-Evaluator ohne Registrierungshelfer (NO-14, B/low). Gate-Rank B (höchster Rank der Quests wäre A wegen infra bei Q-0027; Gate-Policy folgt dem höchsten Quest-Rank → **A für Q-0027**, B für Q-0028).

| Quest | Titel | Rank | Schwere |
|---|---|---|---|
| Q-0027 | Demo-Valkey mit ACL je Dienst | A | low |
| Q-0028 | Compliance-Ports implementieren oder als Seam kennzeichnen | B | low |
