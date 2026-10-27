---
id: Q-0021
gate: G-0008
kind: side
rank: A
severity: low
risk_tags: [auth]
state: proposed
findings: [NO-4]
scope:
  repo: Noelia
  include: ["src/Noelia.Infrastructure/Security/JwtService.cs", "src/**/Security/Identity/PrincipalFactory.cs", "src/**/Extensions/ClaimsPrincipalExtensions.cs", "tests/**/Security/**"]
  exclude: []
estimate: { session_pct: 8, weekly_pct: 1, gold_usd: 0 }
dod:
  - CustomClaims/CustomClaimArrays verweigern reservierte Namen (sub, tenant, role, permission, email_verified, account_status, session_id, sid, jti, iat, exp, iss, aud)
  - Klare Exception beim Ausstellen statt stiller Doppelung
boss: "JwtService_RejectsReservedCustomClaimNames grün in CI"
evidence: ["JwtService.cs:183-203", "PrincipalFactory.cs:18-26", "ClaimsPrincipalExtensions.cs:7-17", "07-warden-noelia.md NO-4"]
board: { provider: github, issue: 40 }
---
# Sperrliste reservierter Claim-Namen

## Objective
Ein Konsument, der Nutzereingaben in Custom-Claims durchreicht, kann damit nicht `tenant`, `email_verified` o. ä. setzen.

## Definition of Done
- [ ] Review + Freigabe (auth, Rank A)
- [ ] Guard + Tests
- [ ] Boss: `JwtService_RejectsReservedCustomClaimNames` grün in CI
