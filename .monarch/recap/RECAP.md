# RECAP

## Stand
- Zeitpunkt: 2026-11-21T11:28:12Z
- Plan: Pro
- Mana 5h: 45% (Limit 90%, Reset 2026-11-21T15:00:00Z)
- Mana 7d: 27% (Limit 96%, Reset 2026-11-25T11:00:00Z)
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
Schmale Warden-Prüfung F4-F6 (ADR-0008 Fassung 5, Head 00c23fc) lesen: PR #71 Kommentar 'Warden: schmale Prüfung F4–F6'. Bei approve: PR-Text auf Fassung 5, dann TDD Q-0016 (Knight, Sonnet), Q-0071, Q-0017, Q-0018, Gate-Schlussrunde Warden, gate merge, Aufräumen, Kurzbericht, STOPP. KEINE Release-Vorbereitung 7.2.0 (Owner 21.11.: erst nach eigenem Gate mit PostgreSQL-Beweis und F4-Test). Nacharbeit-Issue G-0006: PG-Gate vor 7.2.0 mit F4-Test (rot -> F4 technisch schließen, eigene Verbindung), N5, Release-Gate-Widerrufsschritt.

## Offene Entscheidungen
- Kein approve der schmalen Prüfung F4-F6: Frage an den User (Regel 3).
- Q-0026 steht weiter in G-0006 (gate show); blockiert es gate merge: stoppen und den User fragen.
