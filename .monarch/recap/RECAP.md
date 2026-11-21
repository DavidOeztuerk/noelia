# RECAP

## Stand
- Zeitpunkt: 2026-11-21T12:10:23Z
- Plan: Pro
- Mana 5h: 79% (Limit 90%, Reset 2026-11-21T15:00:00Z)
- Mana 7d: 31% (Limit 96%, Reset 2026-11-25T11:00:00Z)
- Limits: 5h 90%, 7d 96% (plan default)

## Aktiv
- Gate G-0006 · Quest Q-0071 [A] · Phase in_progress

## Erledigt
- noch nichts

## Offen
- Q-0070: Umsetzung nach ADR-0007 Entscheidungen 1 bis 9; der Prüfplan des ADR ist die Definition of Done
- Q-0070: Rot-Tests vorher, grün nachher, je Zeile mit Mutationsprobe: Middleware und Handler, Matrix {permission, Rolle, *, cat:*, Permission mit Resource} x {unangemeldet vorn, hinten}: 403, nur unangemeldet 401
- Q-0070: Katalog- und Rollen-Policies, Datensatz-/Besitz-Handler, Kontostand, Mandant/Principal, Audit: je mit unangemeldeter Identität vorn/hinten
- Q-0070: Widerruf je Personen-Identität einzeln inkl. Sitzung, zwei Personen-Schemes; Datensatzprüfung fail-closed
- Q-0070: IClaimsTransformation: zusätzliche Identität ohne AuthenticationType zählt nicht, Weg über die Personen-Identität getestet, Dienst-Principal unberührt
- Q-0070: MIGRATION.md (Bruch, Weg, IPrincipalFactory, 401 bei zweiter Identität, eigener Code), README, Befund unter docs/befunde/; kein Release, keine Versionserhöhung
- Q-0071: Baut der Dienst seinen Schlüsselring still aus JWT_SECRET bzw. JwtSettings:Secret, schreibt er beim Start eine Warnung, die den Weg zu ausdrücklichen Schlüsseln nennt (ohne Geheimniswerte)
- Q-0071: Mit ausdrücklichen Schlüsseln oder Authority keine Warnung (Test für beide Seiten)
- Q-0071: Kein Bruch: Verhalten sonst unverändert; MIGRATION kündigt die Entfernung für 8.0 an (G-0019, Q-0026)

## Nächster konkreter Schritt
Nach Mana-Reset (15:00 UTC): Knight Q-0071 (Sonnet, ohne Commit) nach ADR-0008 Entscheidung 6 + Prüfplan Q-0071; dann Prüfen, Kritiker, quest submit, Commit; danach Q-0017, Q-0018, Gate-Schlussrunde Warden (Opus), gate merge, Nacharbeit-Issue, evidence sync, Aufräumen, Kurzbericht, STOPP. KEINE Release-Vorbereitung 7.2.0. Details: .monarch/knowledge/G-0006-stand-2026-11-21.md

## Offene Entscheidungen
- Q-0026 steht weiter in G-0006 (gate show); blockiert es gate merge: stoppen und den User fragen.
