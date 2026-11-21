# RECAP

## Stand
- Zeitpunkt: 2026-11-21T11:43:49Z
- Plan: Pro
- Mana 5h: 59% (Limit 90%, Reset 2026-11-21T15:00:00Z)
- Mana 7d: 29% (Limit 96%, Reset 2026-11-25T11:00:00Z)
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
Knight Q-0016 (Sonnet) läuft/lief in der Worktree, ohne Commit: Ergebnis prüfen (git status/diff, dotnet test Category=Unit), Kritiker, quest submit Q-0016, Commit, dann Q-0071, Q-0017, Q-0018 (je Knight Sonnet), PR-Text auf Fassung 5 (zwischen zwei Quests, ohne aktive Quest), Gate-Schlussrunde Warden (Opus), gate merge, Nacharbeit-Issue, Aufräumen, Kurzbericht, STOPP. KEINE Release-Vorbereitung 7.2.0. ADR-0008 freigegeben: https://github.com/DavidOeztuerk/noelia/pull/71#issuecomment-7062043960 (Head fe35af6).

## Offene Entscheidungen
- Q-0026 steht weiter in G-0006 (gate show); blockiert es gate merge: stoppen und den User fragen.
