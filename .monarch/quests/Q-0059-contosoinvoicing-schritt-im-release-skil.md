---
id: Q-0059
gate: G-0017
kind: main
rank: A
state: abandoned
dod:
  - "Design in einfacher Sprache im PR, Warden (Opus) gibt frei"
  - "Der Schritt nutzt Pin des Kandidaten, Source-Mapping auf den packenden Feed, Tool des Kandidaten und Operator__Secret"
  - "Der Schritt belegt die aufgelöste Version, sonst bricht er ab"
  - "CI grün"
scope:
  include: [".claude/skills/noelia-release/**", "demo/eng/**", "tests/Noelia.Infrastructure.Tests/Architecture/**", ".monarch/**"]
  exclude: []
estimate: { session_pct: 8, weekly_pct: 3, gold_usd: 0 }
boss: "Der Schritt bricht ab, wenn project.assets.json nicht die Kandidatenversion enthält"
---
# ContosoInvoicing-Schritt im Release-Skill prüft den Kandidaten (#54)
