---
id: Q-0029
gate: G-0010
kind: daily
rank: A
severity: low
risk_tags: [infra]
state: proposed
findings: [SC-4, NO-8, CP-18]
scope:
  repo: [Noelia, NoeliaControlPlane]
  include: ["Noelia/.github/workflows/**", "Noelia/demo/Dockerfile", "Noelia/demo/docker-compose.yml", "Noelia/Directory.Build.props", "NoeliaControlPlane/.github/workflows/**", "NoeliaControlPlane/Dockerfile"]
  exclude: []
estimate: { session_pct: 15, weekly_pct: 2, gold_usd: 0 }
dod:
  - Noelia publish.yml führt zusätzlich Vuln-Audit (dotnet list package --vulnerable) und Demo-Build/-Tests aus
  - Demo- und CP-Images per Digest; packages.lock.json + RestoreLockedMode in CI
  - Renovate/Dependabot in beiden Repos; CP-Actions per SHA; CP-Secret-Scan kennt noelia-lic.v1.
boss: "Noelia publish blockiert bei bekannter Vulnerability (Probe mit Testpaket) + CP pin-check grün"
evidence: ["Noelia .github/workflows/publish.yml:3-10,74-77", "Noelia ci.yml:66-72", "Noelia demo/Dockerfile:1,21", "CP .github/workflows/ci.yml:16-71 (04-review-controlplane CP-18)", "07-warden-noelia.md SC-4, NO-8"]
board: { provider: github, issue: 47 }
---
# Noelia + CP: Digests, Lockfiles, Renovate, Vuln-Audit vor Publish

## Objective
Kein NuGet-Release von Noelia mit bekannter Schwachstelle; reproduzierbare Builds in Noelia und CP.

## Definition of Done
- [ ] Review + Freigabe (infra, Rank A)
- [ ] Workflows + Lockfiles + Renovate
- [ ] Boss: siehe Frontmatter
