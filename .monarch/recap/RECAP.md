# RECAP

## Stand
- Zeitpunkt: 2026-11-21T17:27:14Z
- Plan: Pro
- Mana 5h: 52% (Limit 90%, Reset 2026-11-21T20:00:00Z)
- Mana 7d: 40% (Limit 96%, Reset 2026-11-25T11:00:00Z)
- Limits: 5h 90%, 7d 96% (plan default)

## Aktiv
- keine aktive Quest

## Erledigt
- Q-0016 [A] cleared: Grace-Fenster respektiert Sitzungsende - https://github.com/DavidOeztuerk/noelia/pull/71 - Evidence: https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348094, https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348060, https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348005, https://github.com/DavidOeztuerk/noelia/pull/71
- Q-0017 [A] cleared: Demo: Widerruf wirklich nutzen oder README korrigieren - https://github.com/DavidOeztuerk/noelia/pull/71 - Evidence: https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348094, https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348060, https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348005, https://github.com/DavidOeztuerk/noelia/pull/71
- Q-0018 [D] cleared: E2E-Tests Widerruf und Authority-Pfad - https://github.com/DavidOeztuerk/noelia/pull/71 - Evidence: https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348094, https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348060, https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348005, https://github.com/DavidOeztuerk/noelia/pull/71
- Q-0071 [A] cleared: Warnung beim stillen Shared-Secret-Fallback (7.2.0) - https://github.com/DavidOeztuerk/noelia/pull/71 - Evidence: https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348094, https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348060, https://github.com/DavidOeztuerk/noelia/actions/runs/44929349027/job/136922348005, https://github.com/DavidOeztuerk/noelia/pull/71

## Offen
- Q-0070: Umsetzung nach ADR-0007 Entscheidungen 1 bis 9; der Prüfplan des ADR ist die Definition of Done
- Q-0070: Rot-Tests vorher, grün nachher, je Zeile mit Mutationsprobe: Middleware und Handler, Matrix {permission, Rolle, *, cat:*, Permission mit Resource} x {unangemeldet vorn, hinten}: 403, nur unangemeldet 401
- Q-0070: Katalog- und Rollen-Policies, Datensatz-/Besitz-Handler, Kontostand, Mandant/Principal, Audit: je mit unangemeldeter Identität vorn/hinten
- Q-0070: Widerruf je Personen-Identität einzeln inkl. Sitzung, zwei Personen-Schemes; Datensatzprüfung fail-closed
- Q-0070: IClaimsTransformation: zusätzliche Identität ohne AuthenticationType zählt nicht, Weg über die Personen-Identität getestet, Dienst-Principal unberührt
- Q-0070: MIGRATION.md (Bruch, Weg, IPrincipalFactory, 401 bei zweiter Identität, eigener Code), README, Befund unter docs/befunde/; kein Release, keine Versionserhöhung

## Nächster konkreter Schritt
G-0006 ist gemergt (develop 1947b99e, gruen) und gecleart. Naechstes Gate: PostgreSQL-Beweis vor Release 7.2.0 mit F4-Test (Issue #73, Punkt 1); erst danach Release-Vorbereitung 7.2.0 (Owner). Algorithmus-Pin Authority-Pfad nach 7.2.0 (#72, G-0008).

## Offene Entscheidungen
- Umbrarch: Gate-Datei G-0006 listete die nach G-0019 verschobene Q-0026 weiter; kein Befehl zum Verschieben, gate_plan lehnt bei in_review-Quests ab; Owner hat die Gate-Datei von Hand korrigiert (26e10a7). Fuer Umbrarch v0.2 notieren.
