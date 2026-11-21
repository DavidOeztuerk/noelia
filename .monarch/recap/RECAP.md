# RECAP

## Stand
- Zeitpunkt: 2026-11-21T10:19:38Z
- Plan: Pro
- Mana 5h: 19% (Limit 90%, Reset 2026-11-21T15:00:00Z)
- Mana 7d: 23% (Limit 96%, Reset 2026-11-25T11:00:00Z)
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
Schmale Warden-Prüfung der Markierungszeile (ADR-0008 Fassung 4, Head 9d245f1) lesen: PR #71 Kommentar 'Warden: schmale Prüfung Markierungszeile'. Bei approve: PR-Text auf Fassung 4 (Scope-Hook verweigerte Scratchpad-Write; erst ohne aktive Quest), dann TDD Q-0016 (Knight, Sonnet, model explizit), Q-0071, Q-0017, Q-0018, Gate-Schlussrunde Warden. Owner-Entscheid F1 21.11.: Markierungszeile. Nacharbeit-Issue G-0006: PG-Conformance (Testcontainers), N5 Kappung ohne Bedingung, Release-Gate-Widerrufsschritt.

## Offene Entscheidungen
- Kein approve der schmalen Prüfung der Markierungszeile: Frage an den User (Regel 3), nichts erzwingen.
- Q-0026 steht weiter in G-0006 (gate show), obwohl nach G-0019 verschoben; blockiert es gate merge: stoppen und den User fragen.
