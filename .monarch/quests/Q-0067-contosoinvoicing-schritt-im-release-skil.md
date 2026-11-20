---
id: Q-0067
gate: G-0017
kind: main
rank: A
state: in_progress
dod:
  - "Design (ADR-0005) in einfacher Sprache im PR, Warden (Opus) gibt frei"
  - "Skript demo/eng/contoso-candidate-check.sh: Wegwerf-Kopie, eindeutige Kandidatennummer, NUGET_PACKAGES für alles, Source-Mapping per --configfile, Tool des Kandidaten"
  - "Das Skript belegt Version und Commit am Bau und bricht sonst ab; im globalen NuGet-Ordner entsteht nichts"
  - "Skill ruft das Skript auf"
  - "CI grün"
scope:
  include: ["docs/adr/**", ".claude/skills/noelia-release/**", "demo/eng/**", "tests/Noelia.Infrastructure.Tests/Architecture/**", ".monarch/**"]
  exclude: []
estimate: { session_pct: 8, weekly_pct: 3, gold_usd: 0 }
boss: "Das Skript bricht ab, wenn ein anderer Bau unter der Kandidatennummer im Feed liegt"
---
# ContosoInvoicing-Schritt im Release-Skill prüft den Kandidaten (#54)
