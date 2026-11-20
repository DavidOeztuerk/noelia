# RECAP

## Stand
- Zeitpunkt: 2026-11-20T03:03:55Z
- Plan: Pro
- Mana 5h: 83% (Limit 90%, Reset 2026-11-20T05:40:00Z)
- Mana 7d: 80% (Limit 96%, Reset 2026-11-25T11:00:00Z)
- Limits: 5h 90%, 7d 96% (plan default)

## Aktiv
- keine aktive Quest

## Erledigt
- noch nichts

## Offen
- keine offenen DoD-Punkte

## Nächster konkreter Schritt
Q-0061 (#63), Q-0060 (#55), Q-0062 (#58), Q-0063 (#59), Q-0065 (README/MIGRATION zu #62) abarbeiten; dann PR nach develop (ready), Folge-Issues anlegen (Dashboard-Standalone-Bericht, release-gate.mjs-Einbindung ContosoInvoicing, RequireRateLimiting braucht UseRateLimiter), eine Warden-Runde (Opus), gate verdict/merge/check, evidence sync, Aufräumen. Skill noelia-release am Ende gesamt abgleichen (Schritte 3,4,5 + rm -rf-Zeile entfaellt).

## Offene Entscheidungen
- Docker war aus: Stack-Lauf (security-checks.py mit M2M-Pass am UserService, E2E) steht aus, Owner startet Docker
- demo/.env braucht NOELIA_M2M_CLIENT_TODO_SERVICE_SECRET_HASH (Rezept demo/README.md), sonst bricht der UserService ab; Q-0060 soll das in das .env-Skript aufnehmen
- Control-Plane-Repo: Kopie der demo composition-baselines um ServiceTokenIssuing nachziehen
- Q-0058 wurde wegen falschem Test-Scope durch Q-0066 ersetzt, Q-0059 durch Q-0067 (ADR-Scope)
