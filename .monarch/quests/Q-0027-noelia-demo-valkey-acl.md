---
id: Q-0027
gate: G-0009
kind: side
rank: A
severity: low
risk_tags: [infra]
state: proposed
findings: [NO-7]
scope:
  repo: Noelia
  include: ["demo/docker-compose.yml", "demo/src/shared/Demo.Platform/DemoProviders.cs", "demo/eng/**"]
  exclude: []
estimate: { session_pct: 10, weekly_pct: 1.5, gold_usd: 0 }
dod:
  - Valkey mit ACL-Datei; Verifier-Dienste nur lesen auf noelia:revocation:*, Aussteller schreiben; Audit-Chain nur durch den Eigentümer
  - Zugangsdaten über Env, nicht im Compose-Klartext
boss: "Demo-Probe: todo-service kann noelia:revocation:* nicht löschen (NOPERM)"
evidence: ["demo/docker-compose.yml:211-229,336-345,247-447", "07-warden-noelia.md NO-7"]
board: { provider: github, issue: 45 }
---
# Demo-Valkey mit ACL je Dienst

## Objective
Die Demo zeigt das Least-Privilege-Muster, das Noelia empfiehlt: Ein übernommener Verifier kann Widerrufe nicht aufheben.

## Definition of Done
- [ ] Review + Freigabe (infra, Rank A)
- [ ] ACL + Compose + Probe
- [ ] Boss: Probe grün im Release-Gate
