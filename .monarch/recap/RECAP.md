# RECAP

## Stand
- Zeitpunkt: 2026-11-22T00:32:18Z
- Plan: Pro
- Mana 5h: 74% (Limit 90%, Reset 2026-11-22T01:00:00Z)
- Mana 7d: 54% (Limit 96%, Reset 2026-11-25T11:00:00Z)
- Limits: 5h 90%, 7d 96% (plan default)

## Aktiv
- keine aktive Quest

## Erledigt
- noch nichts

## Offen
- Q-0070: Umsetzung nach ADR-0007 Entscheidungen 1 bis 9; der Prüfplan des ADR ist die Definition of Done
- Q-0070: Rot-Tests vorher, grün nachher, je Zeile mit Mutationsprobe: Middleware und Handler, Matrix {permission, Rolle, *, cat:*, Permission mit Resource} x {unangemeldet vorn, hinten}: 403, nur unangemeldet 401
- Q-0070: Katalog- und Rollen-Policies, Datensatz-/Besitz-Handler, Kontostand, Mandant/Principal, Audit: je mit unangemeldeter Identität vorn/hinten
- Q-0070: Widerruf je Personen-Identität einzeln inkl. Sitzung, zwei Personen-Schemes; Datensatzprüfung fail-closed
- Q-0070: IClaimsTransformation: zusätzliche Identität ohne AuthenticationType zählt nicht, Weg über die Personen-Identität getestet, Dienst-Principal unberührt
- Q-0070: MIGRATION.md (Bruch, Weg, IPrincipalFactory, 401 bei zweiter Identität, eigener Code), README, Befund unter docs/befunde/; kein Release, keine Versionserhöhung

## Nächster konkreter Schritt
Nach dem 5h-Reset (01:00 UTC = 02:00 lokal) und Bestätigung des Guards: Warden-Schlussrunde (Opus) über PR #76 (Head d79934d), dann gate verdict, gate merge G-0020 (develop vorher und nachher grün), Quests Q-0072, Q-0073, Q-0076, Q-0077 clearen, Aufräumen ohne --force, Kurzbericht, stopp. Vorher CI von PR #76 prüfen (gh pr checks).

## Offene Entscheidungen
- Blockiert gate merge, weil Q-0074 und Q-0075 abandoned sind: dann stoppen und den Owner fragen.
