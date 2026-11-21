# RECAP

## Stand
- Zeitpunkt: 2026-11-21T01:12:25Z
- Plan: Pro
- Mana 5h: 96% (Limit 90%, Reset 2026-11-21T02:20:00Z)
- Mana 7d: 13% (Limit 96%, Reset 2026-11-25T11:00:00Z)
- Limits: 5h 90%, 7d 96% (plan default)

## Aktiv
- Gate G-0006 · Quest Q-0016 [A] · Phase in_progress

## Erledigt
- noch nichts

## Offen
- Q-0016: Design → Review → Freigabe (auth, Rank A); Patch-Release + MIGRATION-Eintrag
- Q-0016: Fix in beiden Stores + Conformance-Test
- Q-0016: Boss: `RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded` grün in CI
- Q-0070: Umsetzung nach ADR-0007 Entscheidungen 1 bis 9; der Prüfplan des ADR ist die Definition of Done
- Q-0070: Rot-Tests vorher, grün nachher, je Zeile mit Mutationsprobe: Middleware und Handler, Matrix {permission, Rolle, *, cat:*, Permission mit Resource} x {unangemeldet vorn, hinten}: 403, nur unangemeldet 401
- Q-0070: Katalog- und Rollen-Policies, Datensatz-/Besitz-Handler, Kontostand, Mandant/Principal, Audit: je mit unangemeldeter Identität vorn/hinten
- Q-0070: Widerruf je Personen-Identität einzeln inkl. Sitzung, zwei Personen-Schemes; Datensatzprüfung fail-closed
- Q-0070: IClaimsTransformation: zusätzliche Identität ohne AuthenticationType zählt nicht, Weg über die Personen-Identität getestet, Dienst-Principal unberührt
- Q-0070: MIGRATION.md (Bruch, Weg, IPrincipalFactory, 401 bei zweiter Identität, eigener Code), README, Befund unter docs/befunde/; kein Release, keine Versionserhöhung

## Nächster konkreter Schritt
ADR-0008 Fassung 2 nach .monarch/knowledge/G-0006-warden-r1-korrektur.md (M1 Zeilensperren, M2 Demo-Tests, Minors), Kritiker, Commit, eine Delta-Runde Warden (Opus); danach TDD Q-0016, Q-0071, Q-0017, Q-0018

## Offene Entscheidungen
- Neue Majors der Delta-Runde außerhalb der Korrektur: dem User zeigen, bevor das ADR erweitert wird
