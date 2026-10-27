---
id: Q-0023
gate: G-0008
kind: side
rank: A
severity: low
risk_tags: [infra]
state: proposed
findings: [NO-5, NO-6]
scope:
  repo: Noelia
  include: ["src/Noelia.Infrastructure/Sovereignty/EgressPolicy.cs", "src/Noelia.Infrastructure/Sovereignty/EgressPolicyBuilder.cs", "src/Noelia.Infrastructure/Security/Secrets/Providers/OpenBaoSecretProvider.cs", "tests/**/Sovereignty/**", "tests/**/Secrets/**"]
  exclude: []
estimate: { session_pct: 15, weekly_pct: 2, gold_usd: 0 }
dod:
  - SocketsHttpHandler.ConnectCallback prüft die aufgelöste Adresse (keine privaten/Link-Local/Metadata-IPs, außer ausdrücklich erlaubt)
  - OpenBaoSecretProvider verlangt https außer für localhost; Konstruktor ohne Client entfernt oder intern
  - Doku: Guard ist Allowlist + IP-Schutz, nicht vollständiger SSRF-Schutz
boss: "EgressGuard_BlocksAllowedNameResolvingToPrivateIp grün in CI"
evidence: ["EgressPolicy.cs:43-71", "EgressPolicyBuilder.cs:59,119-141", "OpenBaoSecretProvider.cs:51,63,76-78", "SecretProviderRegistration.cs:32-36", "07-warden-noelia.md NO-5, NO-6"]
board: { provider: github, issue: 42 }
---
# Egress-Guard prüft aufgelöste IP; OpenBao nur https

## Objective
Ein erlaubter Wildcard-Host, der auf eine interne Adresse zeigt, wird nicht mehr verbunden.

## Definition of Done
- [ ] Design → Review → Freigabe (infra, Rank A)
- [ ] ConnectCallback + Schema-Guard + Tests
- [ ] Boss: `EgressGuard_BlocksAllowedNameResolvingToPrivateIp` grün in CI
