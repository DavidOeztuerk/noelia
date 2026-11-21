# G-0006 · Korrekturplan nach Warden-Runde 1 (Design)

Stand 21.11.2026 01:15 UTC. PR #71, Head 01377a5 (nur ADR-0008 Fassung 1 + Monarch-Stand). Review: https://github.com/DavidOeztuerk/noelia/pull/71#issuecomment-7054852190 — changes-requested, 2 major, 11 minor, 4 kosmetisch. Gestoppt wegen Mana (5h 95 % > 90 %, Reset 02:20 UTC).

Owner-Entscheide 21.11.: Q-0026 → 7.2.0 nur Startwarnung (neue Quest Q-0071), Bruch in G-0019 (8.0); Q-0017 → Demo zeigt echten Widerruf.

## Nächster Schritt
ADR-0008 Fassung 2 schreiben (Korrekturen unten), Kritiker, Commit, dann **eine** Delta-Runde Warden (Opus) nur über die Korrektur. Neue Majors außerhalb der Korrektur dem User zeigen, bevor das ADR erweitert wird. Danach TDD (Sonnet-Subagenten, model explizit), dann Gate-Schlussrunde Warden.

## M1 (zwei Geschwister bezeugen sich) → Entscheidung 3 neu: Zeilensperren statt Nachprüfung
- Grace-Zweig (EF) in einer Transaktion (eigene oder die des Aufrufers):
  1. No-op-Update auf alle offenen Zeilen der Sitzung (`SetProperty(t => t.RevokedAt, t => t.RevokedAt)` WHERE Session && RevokedAt null && ReplacedBy null) → sperrt sie bis Commit.
  2. Trifft es 0 Zeilen → Sitzung beendet → `Revoked`, nichts schreiben.
  3. Obergrenze erreicht → `SessionExpired`.
  4. Geschwister schreiben, Kappung, Commit.
- Schließen: bedingtes Update wiederholen, bis 0 Zeilen; Obergrenze 32 Durchläufe, dann werfen (fail closed, Abmelden meldet Fehler, idempotent wiederholbar).
- Beweis für n Geschwister: Ein Grace-Tx G sperrt vor dem Schreiben eine offene Zeile O. Sperrt G zuerst, blockiert der Schließ-Durchlauf j auf O bis G committet; Durchlauf j+1 (neuer Snapshot) sieht S_G. Schließt Durchlauf j zuerst, findet G nach dem Warten O geschlossen (Neubewertung der WHERE-Bedingung) → 0 → Revoked. Gilt unabhängig davon, wer die Transaktion führt → die Grenze „Aufrufer-Transaktion“ entfällt; „Verworfen: Sperrzeile/neue Tabelle“ anpassen (Sperren auf bestehenden Zeilen reichen).
- REPEATABLE READ/SNAPSHOT: Konflikt wird zum Fehler (laut), nie zu offener Sitzung — als Erwartung formulieren, nur SQLite ist getestet (m4 → Folge-Issue PostgreSQL-Conformance per Testcontainers).
- Test: zwei Geschwister deterministisch (Interceptor), dazu Abmelden zwischen Sperre und Schreiben.

## M2 (Demo-Wirkung ungeprüft)
- Test: Stichtag im eigenen Speicher von TodoService → 401 (beweist UseTokenRevocation dort).
- Test: gemeinsame InMemoryTokenRevocationStore-Instanz in beiden Fabriken (MicroserviceEndToEndTests) → Abmelden am UserService → 401 am TodoService.
- „Release-Gate sieht ihn“ nur über die Demo-Tests (dotnet test im Skill noelia-release); laufender Stack mit Valkey ist nicht abgedeckt → offen benennen, ggf. Folge-Issue.

## Minor
- m1: entfällt durch M1-Design (keine Nachprüfung); trotzdem Test mit Erlaubnis 1.
- m2: Re-Read-Pfad `EntityFrameworkRefreshTokenStore.cs:85–90`: Zeile mit RevokedAt, ohne ReplacedBy → `Revoked` (zweiter NO-1-Weg), Test.
- m3: Obergrenze der Schließ-Schleife: 32, dann werfen.
- m4: PostgreSQL nicht getestet → benennen + Folge-Issue.
- m5: README-Abschnitt Transaktionen (`README.md:851–872`): Isolation nennen.
- m6: Stichtag `now` nach dem Schließen lesen; Rest-Rennen (Refresh vor dem Schließen prägt Access-Token danach) dokumentieren, begrenzt durch Lebensdauer des Access-Tokens.
- m7: Monolith-Endpunkte `Demo.Monolith.Api/Program.cs:180–203` nennen; Gateway (Ocelot) braucht nichts.
- m8: Q-0071: „still“ = KeyRingFactory-Zweig über JWT_SECRET/JwtSettings:Secret, egal über welchen Einstieg (`services.AddJwtAuthentication(config, env)`, JwtModule ohne configure); ausdrücklich = übergebener SigningKey inkl. FromSharedSecret. Marker an der Aufrufstelle, eine Warnung (`TryAddEnumerable`).
- m9: „vorher rot“ nur für fehlerbelegende Tests; Negativtests sichern ab; Q-0018 mit Gegenprobe gegen kaputte Ketten.
- m10: Vertrag in XML-Remarks von `IRefreshTokenStore` (Suite wird nicht ausgeliefert).
- m11: zwei Personen-Identitäten: Abmelden beendet die Sitzungen der ersten (PrincipalFactory FirstOrDefault) — ausdrücklich sagen.
- c1: alte DoD-Zeile als Zitat aus #35 kennzeichnen. c2: Boss-Name bleibt (Q-0016 in_progress, gate_plan überschreibt nicht), Ergebnis heißt `Revoked`; Boss-Tests Q-0071/Q-0017/Q-0018 in den Prüfplan. c3: „paralleles Einlösen“ statt „Fenster offen halten“. c4: Minor wegen 7.2.0 (G-0018), Fix allein wäre Patch.

## Reibung bisher (für den Kurzbericht)
1. 21.11. `for f in …; do cat …` über .monarch/quests → Hook Regel 2 (ADR-0011) Fehlalarm → Read-Werkzeug.
2. 21.11. gate_plan verschiebt Q-0026 nach G-0019, G-0006 listet Q-0026 weiter (gate show) → nichts erzwungen; bei gate merge beobachten.
3. 21.11. `gh pr create --body-file .monarch/knowledge/…` → Deny Regel 2 → PR-Text inline.
4. 21.11. Warden: `gh pr comment --body "…"` inline vom Hook als „too complex“ abgelehnt → Warden schrieb Text ins Scratchpad und nutzte --body-file.
5. 21.11. Mana: ein Warden-Lauf (Opus) kostete ~18 pp in einem Schritt (77 % → 95 %), über die Grenze 90 % hinaus; der Guard prüft nur vor dem Schritt.
