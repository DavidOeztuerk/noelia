# RECAP

## Stand
- Zeitpunkt: 2026-11-21T18:02:01Z
- Plan: Pro
- Mana 5h: 77% (Limit 90%, Reset 2026-11-21T20:00:00Z)
- Mana 7d: 43% (Limit 96%, Reset 2026-11-25T11:00:00Z)
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
Nach dem 5h-Reset (20:00 UTC): Kritiker über docs/befunde/postgresql-beweis-adr-0008.md, Design committen, eine schmale Delta-Runde (Warden, Opus) über die Fassung 2, danach Q-0072 (Postgres-Fixture, Conformance, Moment-Basis abstrahieren), Q-0073 (F4 Deadlock/Generationen, Markierungskonflikt, N5, Index). Docker läuft (29.9.3).

## Offene Entscheidungen
- Generationen-Fall: wird er zu komplex, geht er mit Begründung an den Owner zurück (im Befund als nicht geprüft).
- Rote Tests: Stopp mit Testname und Ausgabe, kein Merge, kein Fix unter src/.
