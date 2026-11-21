# RECAP

## Stand
- Zeitpunkt: 2026-11-21T20:50:03Z
- Plan: Pro
- Mana 5h: 24% (Limit 90%, Reset 2026-11-22T01:00:00Z)
- Mana 7d: 47% (Limit 96%, Reset 2026-11-25T11:00:00Z)
- Limits: 5h 90%, 7d 96% (plan default)

## Aktiv
- Gate G-0020 · Quest Q-0073 [A] · Phase in_progress

## Erledigt
- noch nichts

## Offen
- Q-0070: Umsetzung nach ADR-0007 Entscheidungen 1 bis 9; der Prüfplan des ADR ist die Definition of Done
- Q-0070: Rot-Tests vorher, grün nachher, je Zeile mit Mutationsprobe: Middleware und Handler, Matrix {permission, Rolle, *, cat:*, Permission mit Resource} x {unangemeldet vorn, hinten}: 403, nur unangemeldet 401
- Q-0070: Katalog- und Rollen-Policies, Datensatz-/Besitz-Handler, Kontostand, Mandant/Principal, Audit: je mit unangemeldeter Identität vorn/hinten
- Q-0070: Widerruf je Personen-Identität einzeln inkl. Sitzung, zwei Personen-Schemes; Datensatzprüfung fail-closed
- Q-0070: IClaimsTransformation: zusätzliche Identität ohne AuthenticationType zählt nicht, Weg über die Personen-Identität getestet, Dienst-Principal unberührt
- Q-0070: MIGRATION.md (Bruch, Weg, IPrincipalFactory, 401 bei zweiter Identität, eigener Code), README, Befund unter docs/befunde/; kein Release, keine Versionserhöhung
- Q-0073: F4-Test deterministisch über Interceptoren und pg_stat_activity, vier Fälle plus Kontrolle ohne Klammer, Deadlock-Zähler belegt den Zyklus
- Q-0073: N5 und Index-Test auf PostgreSQL
- Q-0073: Befund unter docs/befunde, ADR-0008 Nachtrag nur bei durchweg grünen Tests; offen für SQL Server benannt

## Nächster konkreter Schritt
Owner entscheidet: F4 rot -> Punkt 6c (technisch schließen, rote F4-Tests als TDD-Start), N5 rot -> Fix vor 7.2.0. Danach Gate G-0020 entweder in 6c weiterführen oder mit dem Befund ablegen. Nicht mergen, solange 7 Tests rot sind.

## Offene Entscheidungen
- Wohin mit dem Branch monarch/G-0020-postgresql-beweis (lokal committet, nicht gepusht, 7 Tests rot): als Grundlage für 6c nutzen oder ablegen?
