---
id: Q-0060
gate: G-0017
kind: main
rank: B
state: in_review
dod:
  - "Skript in demo/eng erzeugt eine Wegwerf-.env (nur Wegwerf-Schlüssel, gitignoriert)"
  - "Skill nennt den Schritt und docker compose down -v danach"
  - "CI grün"
scope:
  include: [".claude/skills/noelia-release/**", "demo/eng/**", "demo/README.md", ".monarch/**"]
  exclude: []
estimate: { session_pct: 5, weekly_pct: 2, gold_usd: 0 }
boss: "Frische Worktree: Skript, dann docker compose config ohne fehlende Variable"
board: { pr: 67 }
---
# Release-Skill legt für frische Kopien die demo/.env an (#55)
