# RECAP

## Stand
- Zeitpunkt: 2026-11-20T22:55:11Z
- Plan: Pro
- Mana 5h: 55% (Limit 90%, Reset 2026-11-21T02:20:00Z)
- Mana 7d: 7% (Limit 96%, Reset 2026-11-25T11:00:00Z)
- Limits: 5h 90%, 7d 96% (plan default)

## Aktiv
- keine aktive Quest

## Erledigt
- Q-0069 [A] cleared: ADR-0007: Rechte und Rollen nur aus angemeldeten Identitäten (Design, Owner-Entscheid wörtlich) - https://github.com/DavidOeztuerk/noelia/pull/69 - Evidence: https://github.com/DavidOeztuerk/noelia/actions/runs/44829405864/job/136610243441, https://github.com/DavidOeztuerk/noelia/actions/runs/44829405864/job/136610243731, https://github.com/DavidOeztuerk/noelia/actions/runs/44829405864/job/136610243728, https://github.com/DavidOeztuerk/noelia/pull/69
- Q-0070 [A] cleared: Fix #59: nur angemeldete Identitäten zählen in PermissionMiddleware und PermissionAuthorizationHandler (TDD) - https://github.com/DavidOeztuerk/noelia/pull/69 - Evidence: https://github.com/DavidOeztuerk/noelia/actions/runs/44829405864/job/136610243441, https://github.com/DavidOeztuerk/noelia/actions/runs/44829405864/job/136610243731, https://github.com/DavidOeztuerk/noelia/actions/runs/44829405864/job/136610243728, https://github.com/DavidOeztuerk/noelia/pull/69

## Offen
- keine offenen DoD-Punkte

## Nächster konkreter Schritt
G-0018 abgeschlossen und gemergt (PR #69, 2c1d36d0). Owner: Worktree entfernen, 7.2.0 veröffentlichen (Release-Gate mit Docker-Stack-Lauf, contoso-candidate-check.sh gegen echte Pakete); Nacharbeit #70 und #68, #64-#66.

## Offene Entscheidungen
- ADR-0007 Fassung 4: Warden gab nach Runde 1, Delta-Runde und schmaler Prüfung kein approve; offen war nur ein Formulierungsfehler, behoben (Handler: keine Person = nicht erfüllt). ADR hält sich an den Owner-Entscheid und gilt nach Prompt als freigegeben. Warden prüft die Umsetzung in der Gate-Runde.
- Nacharbeit-Issue für Entscheidung 8 (ClaimsPrincipalExtensions, RateLimiting, Telemetry, ResponseCaching, Dashboard, InitCommand) ist noch nicht angelegt.
