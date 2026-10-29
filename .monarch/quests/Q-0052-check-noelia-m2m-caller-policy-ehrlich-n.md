---
id: Q-0052
gate: G-0014
kind: side
rank: A
state: cleared
dod:
  - Ohne Module und ohne Türen liefert der Check NotApplicable (Konvention aller Composition-Checks), Test vorher rot
  - Demo-Probe SecurityHeadersPackageTests erwartet noelia.m2m.caller-policy; security-check-expectations.json führt ihn als Composition; sonst keine Erwartung geändert
  - ADR, MIGRATION, README sagen NotApplicable statt Pass
  - CI auf PR
scope:
  include: ["src/Noelia.Infrastructure/Security/Checks/ServiceCallerPolicySecurityCheck.cs", "tests/Noelia.Infrastructure.Tests/Security/Checks/**", "tests/Noelia.Infrastructure.Tests/Dashboard/**", "demo/probes/Noelia.SecurityHeaders.Probe/SecurityHeadersPackageTests.cs", "demo/eng/security-check-expectations.json", "MIGRATION.md", "README.md", "docs/adr/0002-*.md"]
  exclude: []
estimate: { session_pct: 5, weekly_pct: 0.6, gold_usd: 0 }
boss: "CI-Job demo auf PR #50 grün"
board: { pr: 50 }
---
# Check noelia.m2m.caller-policy ehrlich: NotApplicable ohne Nutzung; Demo-Erwartungen (Probe, security-check-expectations.json) nachziehen
