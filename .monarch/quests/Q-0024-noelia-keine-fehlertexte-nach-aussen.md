---
id: Q-0024
gate: G-0008
kind: side
rank: A
severity: low
risk_tags: [infra, pii]
state: proposed
findings: [NO-11, NO-12, Warden-A-Neu-2]
scope:
  repo: Noelia
  include: ["src/Noelia.Infrastructure/Security/M2M/ServiceTokenProvider.cs", "src/Noelia.Infrastructure/HealthChecks/**", "src/Noelia.Infrastructure/Extensions/ServiceCollectionExtensions.cs", "src/Noelia.Infrastructure/Builder/Modules/MiddlewarePipelineModule.cs", "tests/**"]
  exclude: []
estimate: { session_pct: 10, weekly_pct: 1.5, gold_usd: 0 }
dod:
  - ServiceTokenProvider loggt nur Statuscode; Token-Endpunkt muss https sein außerhalb Development
  - Health-Checks setzen keine ex.Message in description/data; toter ConfigureHealthCheckEndpoints entfernt
  - UseEnhancedHealthChecks nicht anonym oder ohne data
boss: "Health_AnonymousResponse_ContainsNoExceptionText + M2M_LogsNoResponseBody grün in CI"
evidence: ["ServiceTokenProvider.cs:138-163", "EnhancedHealthCheckExtensions.cs:40-71,107-132", "RateLimitingHealthCheck.cs:92,99,102", "ApplicationHealthCheck.cs:89,184", "07-warden-noelia.md NO-11, NO-12, Neu-2"]
board: { provider: github, issue: 43 }
---
# Keine Fehlertexte nach außen

## Objective
Weder Logs noch der anonyme `/health`-Endpunkt verraten interne Fehlermeldungen oder Antwortkörper von Token-Endpunkten.

## Definition of Done
- [ ] Review + Freigabe (Rank A wegen infra)
- [ ] Änderungen + Tests
- [ ] Boss: beide Tests grün in CI
