---
id: Q-0022
gate: G-0008
kind: side
rank: A
severity: low
risk_tags: [auth, pii]
state: proposed
findings: [NO-10, NO-2, Warden-A-Neu-1]
scope:
  repo: Noelia
  include: ["src/Noelia.Infrastructure/Extensions/ServiceCollectionExtensions.cs", "src/Noelia.Infrastructure/Security/Audit/SecurityAuditMiddleware.cs", "src/Noelia.Infrastructure/Security/SecurityAuditMiddleware.cs", "src/Noelia.Infrastructure/Builder/Modules/MiddlewarePipelineModule.cs", "tests/**"]
  exclude: []
estimate: { session_pct: 12, weekly_pct: 1.5, gold_usd: 0 }
dod:
  - access_token per Query nur bei StartsWithSegments auf exakt konfigurierten WebSocket-Pfaden
  - Nur noch eine SecurityAuditMiddleware; Query-String redigiert (access_token, E-Mail), Session-Id nicht aus Client-Header
  - MIGRATION-Hinweis für Konsumenten des Audit-Namespace
boss: "QueryToken_OnlyOnConfiguredHubPaths + SecurityAudit_RedactsQueryTokens grün in CI"
evidence: ["ServiceCollectionExtensions.cs:415-416,451-461", "Security/Audit/SecurityAuditMiddleware.cs:72,76,125,485-488", "MiddlewarePipelineModule.cs:8,248", "07-warden-noelia.md NO-10, NO-2, Neu-1"]
board: { provider: github, issue: 41 }
---
# Token-Leckfläche verkleinern

## Objective
Access-Tokens landen weder auf Nicht-WebSocket-Pfaden in URLs noch im Audit; welche Audit-Middleware greift, hängt nicht mehr vom Namespace ab.

## Definition of Done
- [ ] Review + Freigabe (auth/pii, Rank A)
- [ ] Pfadprüfung, Zusammenführung der Middleware, Redaktion
- [ ] Boss: beide Tests grün in CI
