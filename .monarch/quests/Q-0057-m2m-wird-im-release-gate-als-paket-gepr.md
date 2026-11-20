---
id: Q-0057
gate: G-0017
kind: main
rank: B
state: in_review
dod:
  - "Der Demo-Stack liefert für noelia.m2m.caller-policy nicht überall NotApplicable"
  - "security-check-expectations.json erwartet dort Pass"
  - "Release-Skill nennt die Erwartung"
  - "CI grün"
scope:
  include: ["demo/**", "tests/Noelia.Infrastructure.Tests/Architecture/**", ".claude/skills/noelia-release/**", ".monarch/**"]
  exclude: []
estimate: { session_pct: 6, weekly_pct: 2, gold_usd: 0 }
boss: "python3 eng/security-checks.py meldet M2M Pass am Aussteller und am Empfänger der Demo"
---
# M2M wird im Release-Gate als Paket geprüft (#56)
