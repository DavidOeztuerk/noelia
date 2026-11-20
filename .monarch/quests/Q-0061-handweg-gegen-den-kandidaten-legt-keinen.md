---
id: Q-0061
gate: G-0017
kind: main
rank: B
state: in_review
dod:
  - "CLAUDE.md, Skill und demo/README.md sagen dasselbe"
  - "Eindeutige Kandidatennummer und eigener Feed oder eigener Paketordner; rm -rf ~/.nuget/packages/noelia.* entfällt"
  - "CI grün"
scope:
  include: ["CLAUDE.md", "README.md", ".claude/skills/noelia-release/**", "demo/README.md", "demo/eng/**", ".monarch/**"]
  exclude: []
estimate: { session_pct: 5, weekly_pct: 2, gold_usd: 0 }
boss: "Nach dem Handweg enthält ~/.nuget/packages/noelia.* keine Quelle außer nuget.org"
board: { pr: 67 }
---
# Handweg gegen den Kandidaten legt keinen Testbau unter echter Nummer im globalen NuGet-Cache ab (#63)
