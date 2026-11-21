# ADR-0008 — Sitzungsende und Widerruf gelten je angemeldeter Person

**Status:** Freigegeben, Fassung 5 (Fassung 3: Fassung 2 nach Warden-Runde 1, [Review](https://github.com/DavidOeztuerk/noelia/pull/71#issuecomment-7054852190), dazu der Fix von N1 aus der [Delta-Runde](https://github.com/DavidOeztuerk/noelia/pull/71#issuecomment-7056522373); Fassung 4: Markierungszeile nach der [schmalen Prüfung](https://github.com/DavidOeztuerk/noelia/pull/71#issuecomment-7056847467) und dem Owner-Entscheid F1; Fassung 5: F4 bis F6 nach der [Prüfung der Markierung](https://github.com/DavidOeztuerk/noelia/pull/71#issuecomment-7061382243) und dem Owner-Entscheid F4). **Freigegeben** vom Warden am 21.11. ([approve](https://github.com/DavidOeztuerk/noelia/pull/71#issuecomment-7062043960)); die Minors daraus (M1–M4, K7, K8) sind eingearbeitet. Release 7.2.0 erst nach einem eigenen Gate mit PostgreSQL-Beweis (Owner 21.11.). · **Datum:** 2026-11-21 · **Gate:** G-0006 (Rank A, Anmeldung und Sitzung) · **Quests:** Q-0016 (Grace-Fenster), Q-0071 (Warnung Shared Secret), Q-0017 (Demo), Q-0018 (Tests) · **Issues:** #35, #36, #37 · **Baut auf:** ADR-0007 (Rechte, Rollen und Nutzer-ID nur aus angemeldeten Identitäten), ADR-0003 (Dienst-Tokens hinter der Tür) · **Ziel:** Minor 7.2.0, **keine** neue öffentliche API. Dieses Gate veröffentlicht nichts.

Minor, weil 7.2.0 schon die Änderungen aus G-0018 trägt; der Fix allein wäre ein Patch. Der Text von Q-0016 sagt noch „Patch-Release“; das ist durch die Owner-Entscheide vom 21.11. überholt.

## Was Fassung 2 ändert

- **Entscheidung 3 neu (M1):** Zeilensperren statt Nachprüfung. Das Schließen endet erst, wenn eine frische Zählung nichts Offenes mehr findet; nach 32 Durchläufen wirft es (m3). Die Grenze bei einer Transaktion des Aufrufers entfällt (m5); für das Ende des Schließens gilt seit Fassung 4 wieder eine (Entscheidung 3).
- **Entscheidung 2:** Ein Token, das ein Abmelden oder die Kappung zwischen Lesen und Claim geschlossen hat, ergibt `Revoked` (m2).
- **Entscheidung 4:** zwei Personen-Identitäten in einer Anfrage (m11).
- **Entscheidung 5:** Der Stichtag wird nach dem Schließen gelesen, das Rest-Rennen ist benannt (m6). Monolith und Gateway sind ausdrücklich genannt (m7). Demo-Tests prüfen die Wirkung im TodoService und über Dienste hinweg (M2).
- **Entscheidung 6:** was als „still“ gilt; der Marker sitzt an der Aufrufstelle (m8).
- **Entscheidung 7:** Der Vertrag steht in den Remarks von `IRefreshTokenStore` (m10).
- **Prüfplan:** zwei Geschwister, Abmelden zwischen Sperre und Schreiben, Weg über das erneute Lesen, Erlaubnis 1, Obergrenze der Schleife, Wirkung in der Demo, kaputte Ketten für Q-0018, alle Boss-Tests; „vorher rot“ nur für Tests, die einen Fehler belegen (M1, M2, m1, m2, m3, m9, c2).
- **Datenbanken:** Die Prämisse ist eingeschränkt, getestet ist nur SQLite (m4).

## Was Fassung 3 ändert (Delta-Runde)

- **N1 (major):** Der Grace-Zweig sperrt nicht mehr alle offenen Zeilen der Sitzung, sondern nur das offene **Ende der
  Kette** seines Vorgängers. Ist dieses Ende geschlossen, ergibt das `Revoked`, und das bleibt so. Nach dem ersten
  Schließ-Durchlauf erzeugt das alte Token darum keine Geschwister mehr, egal wie viele Anfragen kommen. Statt „fail
  closed“ heißt es jetzt „laut, die Sitzung kann offen bleiben“, und die Demo schreibt den Stichtag auch dann.
  (Entscheidungen 2, 3, 5)
- **Folge von N1 (vom Kritiker gefunden):** Lösen mehr Tabs dasselbe alte Token gleichzeitig ein, als die Erlaubnis
  zulässt, bekommen die überzähligen sofort `Revoked`. Der bestehende Test mit acht Einlösungen läuft darum mit
  Erlaubnis 8; seine Erwartungen bleiben wörtlich (Entscheidung 2).
- **N2:** Wartet die Sperre auf eine gleichzeitige Rotation des Kettenendes, folgt sie der Kette. `Revoked` heißt:
  Das Kettenende wurde geschlossen, nicht rotiert. Der Vertragstext von `TryConsumeAsync` wird angepasst.
- **N3:** Deadlocks sind als neue, laute Fehlerart benannt (Entscheidung 3, 7, Folgen).
- **N4:** Die Erwartung für REPEATABLE READ gilt nur für PostgreSQL und SQL Server SNAPSHOT; unter MySQL/InnoDB braucht
  das Schließen READ COMMITTED oder keine Transaktion des Aufrufers.
- **N5:** Die Kappung prüft beim Schließen nicht, ob die Zeile noch offen ist. Das lässt sich auf SQLite nicht rot
  testen und kommt deshalb mit der PostgreSQL-Conformance in die Nacharbeit G-0006.
- **K1, K2:** Beweissatz berichtigt; der Monolith-Test kommt in das bestehende `demo/tests/Demo.Monolith.Tests`.

## Was Fassung 4 ändert (schmale Prüfung, Owner-Entscheid F1)

- **F1 (major):** Das Abmelden schreibt zuerst je Sitzung eine geschlossene Markierungszeile in die bestehende Tabelle.
  Rotation und Grace-Zweig lesen sie und schreiben danach nichts mehr. Das Schließen endet nach höchstens zwei
  Durchläufen nach der Markierung, gleich was ein Akteur tut. Ein Deadlock beim Schließen wird bis zu dreimal
  wiederholt. Das Restrisiko aus Fassung 3 (ein Akteur, der je Durchlauf eine Generation rotiert, treibt das
  Schließen bis zur Grenze) bleibt nur für ein Schließen in einer Transaktion des Aufrufers
  (Entscheidung 3).
- **F2:** Der Test für mehr Einlösungen, als die Erlaubnis zulässt, sichert ab, statt einen Fehler zu belegen.
- **F3:** `CloseAllSessionsAsync` zählt nur die Sitzungen, die es zu Beginn gesehen hat.
- **K3:** Das Kettenende ist nicht sicher das älteste Token; Überzählige „können“ `Revoked` bekommen.

## Nach der Freigabe eingearbeitet (Minors)

- **M1:** Der MIGRATION-Text zu Deadlocks nennt das sofortige Scheitern in einer Transaktion der Anwendung.
- **M2:** Der README-Rat sagt, dass der Angreifer die Last selbst erzeugt.
- **M3:** `CloseAllSessionsAsync` markiert und schließt auch Sitzungen mit offener Zeile nach ihrer Obergrenze; so
  bleiben Rückgabe und Verhalten wie heute und wie im InMemory-Store.
- **M4:** In einer Klammer aus `BeginTransaction` fängt der Savepoint von EF Core den Schlüsselkonflikt ab; nur ohne
  Savepoint bricht er die Transaktion ab.
- **K7, K8, kosmetisch:** `TransactionScope` auch beim Beitreten; Vorab-Prüfung in der Schrittliste; Testvektor;
  nur der Schlüsselkonflikt wird abgefangen; die Grenze steht auch in „In einfacher Sprache“.
- In die Nacharbeit (Gate mit PostgreSQL-Beweis): unter sperrendem READ COMMITTED wandert eine Zeile im Index
  `(SubjectId, RevokedAt)` beim Schließen und Rotieren; rückwärts nur über N5.

## Was Fassung 5 ändert (Prüfung der Markierung, Owner-Entscheid F4)

- **F4 (major):** Die Grenze „Transaktion des Aufrufers“ gilt für Abmelden **und** Wiedergebrauchszweig und für beide
  Mechanismen (Generationen, Deadlock). Owner-Entscheid: README-Rat ändern, Restrisiko annehmen; vor Release 7.2.0
  ein eigenes Gate mit PostgreSQL-Beweis und einem Test, der F4 nachstellt; ist er rot, wird F4 technisch geschlossen.
- **F5 (major):** Schritt 1 wählt die zu markierenden Sitzungen über `SessionExpiresAt` statt über „offen“, damit
  sperrendes READ COMMITTED keine Sitzung übersieht.
- **F6:** Nach einem Schlüsselkonflikt löst der Store die Markierung aus dem ChangeTracker; einzeln, nach `SessionId`
  geordnet; der Effekt von `SaveChanges` steht in Entscheidung 7.
- **K4–K8:** feste Bytefolge der Id mit Testvektor; Wartezeiten bei der Wiederholung; Update und Zählung von
  `CloseAllSessionsAsync` nur über die Sitzungen aus Schritt 1; `TransactionScope` gilt als Transaktion des Aufrufers;
  Zeilenlängen.

## Owner-Entscheide (wörtlich)

Wortlaut aus den Owner-Antworten der Sitzungen vom 27.10. und 21.11.2026, so in Fassung 1 übernommen; im Repository
sonst nicht festgehalten (sinngemäß: `.monarch/quests/Q-0017-…md`, DoD).

- **27.10.2026:** „Abmelden beendet alle Sitzungen der Person, mehrere Tabs und Geräte sind erlaubt.“
- **21.11.2026, Q-0026:** „7.2.0: nur Warnung“. Ausgeführt hieß die Option: 7.2.0 bricht nichts; der stille Fallback bleibt, loggt beim Start aber eine Warnung, und `MIGRATION.md` kündigt die Entfernung für 8.0 an. Der Startabbruch kommt in einem eigenen 8.0-Gate (G-0019, Q-0026). Gemischte Schlüssel bleiben für den Umzug erlaubt.
- **21.11.2026, Q-0017:** „Echter Widerruf“. Ausgeführt hieß die Option: Abmelden beendet alle Sitzungen und sperrt sofort alle Access-Tokens der Person; jeder Dienst prüft den Widerruf; dienstübergreifend in Staging und Production (Valkey) und im Monolithen, in Development bei den Microservices nur je Prozess, und das README sagt das.
- **21.11.2026, F1 (Abmelden gegen einen Token-Dieb):** „Markierungszeile (Empfohlen)“. Ausgeführt hieß die Option:
  Das Abmelden schreibt zuerst je Sitzung eine geschlossene Markierungszeile in die bestehende Tabelle (keine
  Migration, aber neue Zeilenart = Datenformat). Refresh prüft sie und schreibt danach nichts mehr. Abmelden endet
  dann immer nach ~2 Durchläufen, ein Deadlock darf wiederholt werden. Die Gegenoption war, das Restrisiko aus
  Fassung 3 anzunehmen. (Wortlaut aus dieser Sitzung, Auswahl über die Rückfrage.)
- **21.11.2026, F4 (Diebstahl-Erkennung und Abmelden in einer Transaktion der Anwendung):** „Option 1 (Rat ändern +
  Rest annehmen). Zwei Ergänzungen: 1) Die eigene Verbindung für die Markierung ist kein 8.0-Kandidat. Vor Release
  7.2.0 kommt ein eigenes Gate mit PostgreSQL-Beweis (Testcontainers), darin ein Test, der F4 nachstellt. Ist er rot,
  schließen wir F4 technisch. Schreib das so ins ADR und ins Nacharbeit-Issue. 2) Nach dem Merge von G-0006 keine
  Release-Vorbereitung 7.2.0, die kommt erst nach dem PostgreSQL-Beweis.“ Option 1 hieß ausgeführt: README rät
  künftig, Refresh und Abmelden nicht in eine eigene Transaktion zu legen, wenn der Schutz gegen Token-Diebstahl
  unter Last zählt (Audit-Zeile danach schreiben); das Muster bleibt erlaubt, das Restrisiko steht in README,
  MIGRATION und Remarks und als Owner-Entscheid im ADR. (Wortlaut aus dieser Sitzung.)
- **21.11.2026, Algorithmus-Pin auf dem Authority-Pfad (Befund aus Q-0018):** „Option 1, mit einer Änderung: Das Gate
  für den Algorithmus-Pin auf dem Authority-Pfad kommt nach Release 7.2.0, nicht davor (Teil der Härtung). Grund: nur
  mit dem privaten Schlüssel des Anbieters ausnutzbar, none und der HMAC-Trick bleiben gesperrt. Lege das Issue mit
  Label Sicherheit an und nenne es im Nacharbeit-Issue. G-0006 läuft ohne den alg-Fall auf dem Authority-Pfad
  weiter.“ Option 1 hieß ausgeführt: Der Authority-Pfad setzt keine `ValidAlgorithms`
  (`ServiceCollectionExtensions.cs`, Zweig `keys is null`); ein Token mit dem Schlüssel des Anbieters, aber RS512 statt
  RS256, kommt durch. G-0006 testet `kid`, `iss` und `aud` auf dem Authority-Pfad und den Algorithmus-Pin auf dem
  Schlüsselring-Pfad; der Fall `alg` auf dem Authority-Pfad kommt im Härtungs-Gate zuerst rot zurück (#72).
  (Wortlaut aus dieser Sitzung.)

### Auslegung durch dieses ADR (nicht Teil des Wortlauts)

„Mehrere Tabs und Geräte sind erlaubt“ heißt: Noelia begrenzt die Zahl gleichzeitiger Sitzungen einer Person weiterhin nicht, und das Grace-Fenster für zwei Tabs einer Sitzung bleibt. „Abmelden beendet alle Sitzungen“ ist eine Entscheidung für Anwendungen, nicht für die Bibliothek: `ITokenSessionService.SignOutAsync(session)` bleibt für Konsumenten, die anders entscheiden. Die Demo setzt den Entscheid um (Entscheidung 5), WT in Q-0008.

## In einfacher Sprache

Wer sich anmeldet, bekommt ein kurzlebiges Access-Token und ein Refresh-Token. Mit dem Refresh-Token holt der Browser
neue Tokens. Jedes Refresh-Token gilt nur einmal: Beim Einlösen wird es durch ein neues ersetzt. Damit zwei Tabs, die
gleichzeitig einlösen, sich nicht gegenseitig abmelden, gibt es ein Fenster von 15 Sekunden: Kommt das alte Token in
dieser Zeit noch einmal, bekommt der zweite Tab ein eigenes neues Token für dieselbe Sitzung.

**Der Fehler (NO-1):** Dieses Fenster fragt nicht, ob die Sitzung inzwischen beendet wurde. Meldet sich die Person ab,
schließt Noelia nur die gerade gültigen Tokens. Das eben ersetzte Token bleibt unberührt, und wer es hat, bekommt in den
restlichen Sekunden ein neues, gültiges Token. Die Sitzung lebt weiter, obwohl die Person sich abgemeldet hat.

**Die Lösung:** Eine Sitzung gilt als beendet, wenn sie kein offenes Token mehr hat. Das Fenster gibt nur noch ein neues
Token aus, solange das jüngste Token der Kette, aus der das alte Token stammt, noch offen ist. Damit ein Abmelden im
selben Augenblick nicht verliert, hält der Refresh dieses jüngste Token fest, bis er fertig ist. Ein gleichzeitiges
Abmelden wartet darauf und schließt danach auch das neue Token. Ist das jüngste Token einmal geschlossen, gibt das alte
Token nie wieder ein neues her, auch nicht bei vielen gleichzeitigen Versuchen. Beim Abmelden legt der
Datenbank-Store zuerst eine Markierung an, dass diese Sitzung endet; jeder Refresh, der danach kommt, schaut nach und gibt nichts mehr aus. So ist
das Abmelden nach wenigen Schritten fertig, auch gegen einen schnellen Angreifer. Geht doch etwas schief, meldet es
laut einen Fehler. Eine Grenze bleibt: Legt eine Anwendung Abmelden oder Refresh in eine eigene
Datenbank-Transaktion, wirkt die Markierung erst an deren Ende; das README rät davon ab, und vor Version 7.2.0 prüft
ein eigener Test das gegen PostgreSQL. Die Datenbanktabelle der Anwendungen bekommt keine neue Spalte; die Markierung ist eine Zeile wie
die anderen.

Dazu: Die Demo sperrt beim Abmelden auch die Access-Tokens sofort und meldet alle Sitzungen der Person ab, und Tests
prüfen das in jedem Dienst. Ein Dienst, der still ein geteiltes Geheimnis als Schlüssel nimmt, schreibt beim Start eine
Warnung; in 8.0 startet er so nicht mehr.

## Frage

1. Wie erkennt der Refresh-Store im Grace-Fenster eine beendete Sitzung, ohne die Tabelle `noelia_refresh_tokens` der
   Konsumenten zu ändern, und so, dass ein gleichzeitiges Abmelden nicht verliert, auch bei mehreren gleichzeitigen
   Geschwistern?
2. Wessen Sitzungen beendet ein Abmelden, und woher kommt diese Person (ADR-0007)?
3. Was zeigt die Demo, und was meldet 7.2.0 zum stillen Shared-Secret-Fallback?

## Befund, im Quelltext belegt (Stand `2c1d36d`)

- `EntityFrameworkRefreshTokenStore.cs:56–59`: Ein Token mit `ReplacedBy` geht nach `AlreadyRotatedAsync`
  (`:146–170`). Liegt die Rotation weniger als `grace` zurück, entsteht ein Geschwister, **ohne** Blick auf den Rest der
  Sitzung. Das Geschwister wird ohne Transaktion geschrieben (`:165–167`), die Kappung läuft danach getrennt.
- **Zweiter Weg (Weg über das erneute Lesen):** Schließt ein Abmelden oder die Kappung das Token zwischen dem Lesen
  (`:48–49`) und dem Claim (`:115–123`), liest `:88–90` die Zeile neu und gibt sie an `AlreadyRotatedAsync`. Die Zeile
  trägt dann `RevokedAt`, aber **kein** `ReplacedBy`; sie liegt im Fenster (das `RevokedAt` ist eben erst gesetzt) und
  bekommt ein Geschwister.
- `CloseSessionAsync` (`:173–183`) und `CloseAllSessionsAsync` (`:186–196`) schließen nur Zeilen mit
  `RevokedAt == null && ReplacedBy == null`. Der rotierte Vorgänger trägt `RevokedAt` (Zeit der Rotation) und
  `ReplacedBy`; er bleibt, wie er ist, und öffnet im Fenster ein neues Geschwister.
- `InMemoryRefreshTokenStore.cs:66–78`: dieselbe Logik im Grace-Zweig. Den zweiten Weg gibt es dort nicht, weil ein
  Lock den ganzen Übergang umfasst.
- Derselbe Grace-Zweig prüft auch die Obergrenze der Sitzung nicht: Ist `SessionExpiresAt` erreicht, entsteht ein
  Geschwister mit `ExpiresAt` in der Vergangenheit, gemeldet als `RotatedWithinGrace`.
- Demo: `UserService.Api/Program.cs:224–237` und `Demo.Monolith.Api/Program.cs:180–192` lesen `session_id` über
  `http.User.FindFirst` aus **allen** Identitäten (die Fehlerklasse aus ADR-0007) und schließen nur diese eine Sitzung;
  `/auth/sign-out-everywhere` steht in `UserService.Api/Program.cs:239–248` und `Demo.Monolith.Api/Program.cs:194–203`.
  Kein Demo-Dienst führt `UseTokenRevocation`, und nichts schreibt in den registrierten Widerrufsspeicher
  (`DemoProviders.cs:104–107, 131–134`). `SignInTests.cs:146–165` hält fest, dass das Access-Token das Abmelden
  überlebt. Der Gateway liest keine Tokens: `Gateway.Api/Program.cs:41` übergibt kein `readsTokens`, es bleibt beim
  Standard `false` (`DemoProviders.cs:57`); `ocelot.json` leitet `/api/auth/{everything}` nur weiter.
- `KeyRingFactory.cs:38–45`: Ohne Schlüssel und ohne Authority entsteht der Ring still aus `JWT_SECRET` bzw.
  `JwtSettings:Secret` (erreicht über `InfrastructureBuilder.AddJwtAuthentication()` ohne `configure`,
  `JwtModule.cs:29–50`). Denselben Weg ohne Schlüssel geht öffentlich
  `services.AddJwtAuthentication(configuration, environment)` (`ServiceCollectionExtensions.cs:320–345`);
  `UseJwt(jwt => jwt.FromSharedSecret())` ruft diese Methode ausdrücklich auf (`JwtBuilder.cs:97–102`). Die Prüfung
  `noelia.jwt.key-separation` meldet ein geteiltes Geheimnis außerhalb von Development als Fail
  (`BuiltInSecurityChecks.cs:342–348`), unterscheidet aber nicht zwischen still und ausdrücklich.

## Entscheidungen

1. **Beendet heißt: kein offenes Token mehr.** Eine Sitzung ist beendet, wenn sie keine Zeile mit `RevokedAt == null`
   und `ReplacedBy == null` hat. Das ist ein abgeleiteter Zustand, keine neue Spalte. Er trägt, weil
   - jede Rotation den Nachfolger in derselben Transaktion schreibt, in der sie den Vorgänger schließt (`ClaimAsync`);
   - die Kappung in ihrer Transaktion mindestens ein Token offen lässt (`allowance >= 1` wird geprüft). Weil Schreiben
     und Kappen künftig auch im Grace-Zweig in einer Transaktion liegen (Entscheidung 3), sieht von zwei gleichzeitigen
     Kappungen höchstens eine das neue Token der anderen. Darum können zwei Kappungen nicht gemeinsam alle Tokens
     schließen, auch bei gleichem `IssuedAt`;
   - nur `CloseSessionAsync`, `CloseAllSessionsAsync` und die Erkennung eines Wiedergebrauchs eine Sitzung auf null
     offene Tokens bringen; ein abgelaufenes Token bleibt im Sinne dieser Regel „offen“.
   Der rotierte Vorgänger wird beim Abmelden **nicht** angefasst (siehe Verworfen). #35 hatte das als „CloseSession/
   CloseAllSessions markieren auch rotierte Vorgänger“ formuliert. Die heutigen DoD-Zeilen von Q-0016 lauten: „Ein
   rotiertes Refresh-Token einer geschlossenen Sitzung erzeugt im Grace-Fenster kein Geschwister mehr (Ergebnis
   Revoked), in EF- und InMemory-Store“ und „Auch ein Abmelden zwischen Prüfung und Schreiben des Geschwisters lässt
   keine offene Sitzung zurück“. Dieses ADR erfüllt die erste, indem das Fenster das Ende der Kette liest (Entscheidung
   2; mindestens so streng wie „Sitzung beendet“), und die zweite mit Entscheidung 3.

2. **Der Grace-Zweig fragt zuerst das Ende der Kette.** In `AlreadyRotatedAsync` (EF) bzw. im Zweig `ReplacedBy is not null`
   (InMemory), in dieser Reihenfolge:
   0. **Nur EF, Weg über das erneute Lesen:** Trägt die neu gelesene Zeile kein `ReplacedBy`, ergibt das
      **`ConsumeOutcome.Revoked`**. Sie wurde geschlossen, nicht rotiert, wie in `:61–64`. Damit lebt ein durch
      Abmelden oder Kappung geschlossenes Token auch auf diesem Weg nicht wieder auf.
   1. Fenster vorbei oder Vorgänger nie geschlossen → wie heute `ReuseDetected` mit Schließen der Sitzung.
   2. Das **Ende der Kette** des Vorgängers ist geschlossen → **`ConsumeOutcome.Revoked`**, kein Geschwister, kein
      Schreiben. Die Kette folgt `ReplacedBy` vom Vorgänger aus (R0 → R1 → … ) bis zur ersten Zeile ohne `ReplacedBy`;
      Geschwister gehören nicht dazu. Ist eine Sitzung beendet (Entscheidung 1), ist auch dieses Ende geschlossen. Das
      Kriterium ist also mindestens so streng wie „Sitzung beendet“, und es ist **klebrig**: Eine geschlossene Zeile
      öffnet sich nie wieder, und rotiert wird nur aus offenen Zeilen. Fehlt eine Zeile der Kette (Purge) oder ist die
      Kette länger als 32 Glieder, gilt das Ende als geschlossen. In beiden Stores gilt dasselbe Kriterium; in EF
      entscheidet es die Sperre aus Entscheidung 3, nicht ein Lesen davor.
   2b. **Nur EF:** Die Sitzung trägt die Markierung eines laufenden Abmeldens → **`ConsumeOutcome.Revoked`**
      (Entscheidung 3). Dieselbe Prüfung macht die Rotation vor ihrem Claim.
   3. Obergrenze der Sitzung erreicht (`now >= SessionExpiresAt`) → **`ConsumeOutcome.SessionExpired`**, kein Geschwister.
   4. Sonst Geschwister wie heute (`RotatedWithinGrace`), danach Kappung.
   `Revoked` statt eines neuen Werts: Der Enum-Wert sagt schon „The session was ended deliberately“. `ReuseDetected` wäre
   falsch: Ein zweiter Tab, der nach dem Abmelden sein altes Token zeigt, ist kein Diebstahl, und der Dienst würde einen
   Diebstahl ins Log schreiben. Reihenfolge 1 vor 2: Ein Wiedergebrauch nach dem Fenster bleibt ein Wiedergebrauch, auch
   in einer beendeten Sitzung (das Schließen ist idempotent).
   **Kappung und Kettenende:** Die Kappung schließt die ältesten offenen Tokens zuerst. Meist ist das Kettenende R1
   älter als seine Geschwister, sicher ist es nicht: `now` wird vor dem Store gelesen (`TokenSessionService.cs:71`
   gegen `:91`), unter Gleichzeitigkeit kann ein Geschwister ein früheres `IssuedAt` tragen, und bei Gleichstand ist die
   Reihenfolge offen. Schließt sie das
   Kettenende, ergibt jedes weitere Einlösen des alten Tokens `Revoked`, auch wenn die Sitzung über Geschwister noch
   offen ist. Das geschieht erst, wenn mehr als `allowance` Tokens der Sitzung offen sind (Standard 5), und es ist die
   geschlossene Richtung. **Verhaltensänderung:** Lösen mehr Tabs dasselbe alte Token gleichzeitig ein, als die
   Erlaubnis zulässt, können die überzähligen `Revoked` statt eines Geschwisters bekommen. Heute bekommen sie eines, aber die
   Kappung schließt die ältesten sofort wieder, und deren Tabs fliegen beim nächsten Refresh hinaus. Offen bleiben in
   beiden Fällen höchstens `allowance` Tokens; neu ist nur, dass die Abweisung sofort kommt. Der bestehende
   Conformance-Test `Concurrent_presentations_rotate_exactly_once_and_nobody_is_thrown_out` löst achtmal bei Erlaubnis 5
   ein und würde damit je nach Reihenfolge rot. Er bekommt eine Erlaubnis von 8. Sein Gegenstand bleibt unverändert:
   genau ein `Rotated`, und niemand wird sofort abgemeldet, solange die Erlaubnis reicht. Für mehr Einlösungen, als die
   Erlaubnis zulässt, kommt ein eigener Test dazu (Prüfplan).
   **Erlaubnis 1** (`MaxConcurrentTokensPerSession = 1`, zulässig): Der zweite Tab bekommt sein Geschwister, und die
   Kappung schließt das Kettenende, also das Token des ersten Tabs. Die Sitzung bleibt mit genau einem offenen Token
   offen. Jedes weitere Einlösen des alten Tokens, auch ein gleichzeitiges, ergibt `Revoked`. Bei Erlaubnis 1 kann
   ohnehin nur ein Tab weiterarbeiten.

3. **Abmelden gewinnt auch im selben Augenblick (EF): Zeilensperren auf bestehenden Zeilen.** Prüfen und Schreiben im
   Grace-Zweig sind mehrere Anweisungen; ein Abmelden dazwischen darf kein offenes Geschwister zurücklassen. Praktisch
   erreichbar ist dieser Augenblick durch paralleles Einlösen desselben alten Tokens und durch das Rotieren erhaltener
   Geschwister, auch ohne Angreifer: zwei weitere Tabs im Augenblick des Abmeldens. Zwei Hälften:
   - **Grace-Zweig: erst das Kettenende sperren, dann entscheiden, dann schreiben.** In einer Transaktion (eigene, oder
     die des Aufrufers, wenn eine offen ist, wie in `ClaimAsync`):
     0. Markierung der Sitzung lesen (schneller Ausstieg, unten): vorhanden → `Revoked`.
     1. Das Kettenende E suchen: von `ReplacedBy` des Vorgängers aus den Verweisen folgen bis zur ersten Zeile ohne
        `ReplacedBy` (Entscheidung 2, Schritt 2).
     2. E mit einem No-op-Update sperren
        (`SetProperty(t => t.RevokedAt, t => t.RevokedAt)` mit `Id == E && RevokedAt == null && ReplacedBy == null`).
        Es ändert nichts, sperrt E aber bis zum Commit.
     3. Trifft es **null** Zeilen, E frisch lesen: Trägt E jetzt ein `ReplacedBy`, wurde E gleichzeitig rotiert; dann mit
        dessen Nachfolger weiter bei Schritt 2. Sonst ist E geschlossen (Abmelden, Kappung, Wiedergebrauch) oder
        gelöscht → `Revoked`, nichts schreiben. Alle Glieder zusammen höchstens 32, darüber → `Revoked`.
     4. Markierung der Sitzung lesen (unten): vorhanden → `Revoked`, nichts schreiben.
     5. Obergrenze erreicht → `SessionExpired`, nichts schreiben.
     6. Geschwister schreiben, Kappung, Commit (bei eigener Transaktion).
     Gesperrt wird nur E, nicht jede offene Zeile der Sitzung. In Fassung 2 sperrte jede neue Grace-Anfrage alle
     offenen Zeilen, auch das jüngste Geschwister, bevor der nächste Durchlauf des Schließens es erreichte; ein stetiger
     Strom paralleler Anfragen trieb das Schließen so bis zur Grenze (Delta-Runde, N1).
   - **Schließen: zuerst markieren, dann wiederholen, bis eine frische Zählung nichts Offenes findet.**
     1. **Markierung (Owner 21.11., F1):** Zuerst bestimmt das Schließen die Sitzungen, die es markiert, über eine
        Bedingung, die kein Update ändert: `CloseSessionAsync` die eine Sitzung, wenn sie überhaupt eine Zeile hat;
        `CloseAllSessionsAsync` jede Sitzung der Person mit einer Zeile, deren `SessionExpiresAt` nach `at` liegt
        (offen oder nicht), **oder** mit einer offenen Zeile. Der zweite Teil hält Rückgabe und Verhalten wie heute
        und wie im InMemory-Store: Auch offene Zeilen von Sitzungen nach ihrer Obergrenze werden geschlossen (Freigabe,
        M3). Für jede schreibt es eine geschlossene Markierungszeile, falls es noch keine gibt (Aufbau
        unten), einzeln und nach `SessionId` geordnet. Dieselbe Regel gilt im Wiedergebrauchszweig, der
        `CloseSessionAsync` ruft. Fassung 4 wählte „Sitzungen mit offenem Token“; unter sperrendem READ COMMITTED
        (SQL Server ohne RCSI) kann dieses Lesen eine Sitzung übersehen, in der gerade rotiert wird, und mit F3 bliebe
        sie still offen (schmale Prüfung, F5). Eine Markierung auf einer schon beendeten Sitzung schadet nicht. Eine
        Sitzung, deren Obergrenze erreicht ist, kann kein brauchbares Token mehr bekommen (`SessionExpired`).
     2. Dann das bedingte Update, danach in einer eigenen Anweisung die Zählung der offenen Zeilen. Bei
        `CloseAllSessionsAsync` treffen Update und Zählung nur die Sitzungen aus Schritt 1: Eine Anmeldung, die während
        des Abmeldens beginnt, verlängert es nicht und bleibt offen; eine Anmeldung nach dem Abmelden ist zulässig
        (schmale Prüfung, F3). Ist die Zahl größer als null, folgt der nächste Durchlauf.
     3. Nach **32** Durchläufen wirft die Methode eine `InvalidOperationException`. Mit der Markierung ist das kein
        erwarteter Fall mehr, sondern ein Netz (Terminierung unten). Das ist **laut, nicht geschlossen**: Das Abmelden
        meldet einen Fehler, die Sitzung kann offen bleiben, und Wiederholen ist idempotent. Still zurückkehren wäre
        „vorhanden, aber nicht wirksam“. Die Meldung nennt die Sitzung bzw. die Person (Kennungen, keine Token-Werte),
        damit das Log zeigt, was offen geblieben sein kann; im Wiedergebrauchszweig steht sie an der Stelle der
        Warnung. Die Rückgabe ist die Summe der geschlossenen Zeilen.
     **Warum die Zählung und nicht „bis ein Update null Zeilen trifft“:** Wartet ein Durchlauf auf die Sperre einer
     gleichzeitigen **Rotation**, sieht er danach den Vorgänger geschlossen und überspringt ihn. Den Nachfolger sieht er
     nicht, weil der erst nach dem Start der Anweisung bestätigt wurde. Der Durchlauf trifft also null Zeilen, während
     der Nachfolger offen ist. Die Zählung danach ist eine neue Anweisung und sieht ihn.
   **Die Markierungszeile** ist eine gewöhnliche Zeile der bestehenden Tabelle `noelia_refresh_tokens`, ohne neue Spalte
   und ohne Migration (`NoeliaRefreshToken.cs:84–91` lässt sie zu):
   - `Id`: `new Guid(SHA256(UTF8("noelia.refresh-token.session-ended.v1") ‖ sessionId.ToByteArray())[0..16])`, also
     die Bytefolge von .NET für `ToByteArray` und den `Guid(ReadOnlySpan<byte>)`-Konstruktor; ein fester Testvektor
     sichert sie (`00112233-4455-6677-8899-aabbccddeeff` → `06463759-edbf-469d-0a5c-11a097d1db66`, in der Freigabe
     und in dieser Session unabhängig nachgerechnet). Deterministisch, darum höchstens eine je Sitzung.
   - Geschrieben wird über `Add` und `SaveChanges`, nachdem ein Lesen keine Markierung fand. Trifft ein gleichzeitiges
     zweites Schließen den Primärschlüssel (zwei Tabs melden ab), löst der Store die Entität sofort aus dem
     ChangeTracker und prüft frisch, ob die Zeile existiert; dann gilt sie als „schon markiert“, sonst wirft er.
     Abgefangen wird nur dieser Fall (die Markierung existiert danach); jeder andere Fehler wirft. Der
     Schlüsselkonflikt ist nicht transient und wird nicht wiederholt. In einer Transaktion des Aufrufers aus
     `BeginTransaction` setzt EF Core vor `SaveChanges` einen eigenen Savepoint und rollt bei einem Fehler darauf
     zurück (`AutoSavepointsEnabled`, Standard); die Transaktion bleibt dann brauchbar, und das frische Prüfen gelingt.
     Ohne Savepoint (umgebende `TransactionScope` oder `AutoSavepointsEnabled = false`) bricht der Konflikt unter
     PostgreSQL die ganze Transaktion ab; das ist laut, nur durch Rollback aufzuheben und fällt unter die Grenze aus
     F4. Ein zweites Abmelden wartet in der Klammer bis zum Commit der ersten Markierung. Der F4-Test prüft beide Arten
     der Klammer (Freigabe, M4).
   - Hat die Sitzung keine Zeile, gibt es nichts zu schließen und keine Markierung.
   - `TokenHash`: 32 Zufallsbytes, kein Hash eines ausgegebenen Tokens; `TryConsumeAsync` findet die Zeile darum nie.
   - `SessionId`, `SubjectId`, `SessionStartedAt`, `SessionExpiresAt` aus einer Zeile der Sitzung;
     `IssuedAt = ExpiresAt = RevokedAt = at`; `ReplacedBy` und `ClientFingerprint` leer.
   - Sie ist von Anfang an geschlossen und abgelaufen: Sie zählt nie als offen, erscheint nicht in
     `ActiveSessionsAsync`, zählt nicht in der Rückgabe des Schließens und fällt beim Purge wie jede geschlossene Zeile.
     `PurgeAsync` ruft die Anwendung selbst; sein Horizont muss weit hinter der Gegenwart liegen, sonst entfernt er
     die Markierung mitten im Schließen (Remarks).
   **Schreiber lesen sie.** Die Rotation prüft in ihrer Transaktion **vor** dem bedingten Update, der Grace-Zweig
   **nach** seiner letzten Sperre auf E, ob die Markierung ihrer Sitzung existiert (ein Lesen über den
   Primärschlüssel). Wenn ja, schreiben sie nichts, und das Ergebnis ist `Revoked`. Der Grace-Zweig prüft zusätzlich
   schon vor der Sperre, damit sich Anfragen nach der Markierung nicht an E anstellen; entscheidend ist die Prüfung
   danach.
   **Was die Datenbank dafür leisten muss** (READ COMMITTED): (a) Ein Update sperrt die getroffenen Zeilen bis zum
   Commit. (b) Ein Update, das auf eine Sperre wartet, prüft seine Bedingung danach am zuletzt bestätigten Stand der
   Zeile. (c) Eine Anweisung, die nach einem Commit beginnt, sieht ihn. Für PostgreSQL ist das dokumentiertes
   Verhalten. Für SQL Server mit und ohne READ_COMMITTED_SNAPSHOT erwarten wir es, geprüft ist es nicht. SQLite führt
   Schreiber ohnehin nacheinander aus. Fassung 1 stützte sich auf „eine Anweisung sieht nur Zeilen, die vor ihrem Start
   bestätigt waren“. Das gilt für das sperrende READ COMMITTED von SQL Server nicht, und der Beweis unten braucht es
   nicht.
   **Beweis für beliebig viele Geschwister.** Invariante: Jede Transaktion, die einer bestehenden Sitzung ein offenes
   Token hinzufügt, hält bis zu ihrem Commit eine Sperre auf einer Zeile derselben Sitzung, die beim Sperren offen war.
   Die Rotation sperrt so ihren Vorgänger über das bedingte Update in `ClaimAsync`, der Grace-Zweig über das No-op-Update
   das Kettenende E. Eine Anmeldung (`CreateAsync`) beginnt eine neue Sitzung und fällt nicht darunter.
   Angenommen, das Schließen endet, weil die Zählung zum Zeitpunkt σ null offene Zeilen sieht, und danach bestätigt eine
   Transaktion X ein offenes Token der Sitzung. X hielt eine Sperre auf einer Zeile O, die beim Sperren offen war. Ein
   Schließen von O wartet zwischen dieser Sperre und dem Commit von X auf X (a). Wäre O vor der Sperre schon
   geschlossen und bestätigt gewesen, hätte X das gesehen, nach (c) bzw. (b), und O nicht gesperrt. Also ist O bis zum
   Commit von X nicht geschlossen, und dieser Commit liegt nach σ. War O vor σ bestätigt, hätte die Zählung O als
   offen gesehen: Widerspruch. Sonst hat eine frühere Transaktion O nach σ bestätigt, und für sie gilt dasselbe. Diese
   Kette ist endlich und endet bei einer Zeile, die vor σ bestätigt war, denn die Sitzung bestand schon beim Abmelden.
   Wieder ein Widerspruch. Für `CloseSessionAsync` braucht der Beweis die Markierung nicht; sie sorgt für das Ende
   (Terminierung). Für `CloseAllSessionsAsync` hängt er zusätzlich an Schritt 1, weil Update und Zählung nur dessen
   Sitzungen treffen: Jede Sitzung, die noch ein brauchbares Token bekommen kann, hat eine Zeile mit `SessionExpiresAt`
   nach `at`, und diese Bedingung ändert kein Update. Sitzungen nach ihrer Obergrenze erfasst Schritt 1 nur über eine
   offene Zeile; sie bekommen kein brauchbares Token mehr (`SessionExpired`), und das Schließen muss dort nur ihre
   offenen Zeilen schließen, wie heute.
   Für den Grace-Zweig heißt das: Sperrt er zuerst, wartet das Schließen auf seinen Commit und schließt danach auch das
   Geschwister. Die Antwort an den Client lautet dann `RotatedWithinGrace`, aber das Token ist zu, sobald das Abmelden
   zurückkehrt. Schließt das Abmelden zuerst, findet die Sperre E geschlossen, und das Ergebnis ist `Revoked`. Für zwei
   Geschwister aus Runde 1 (M1) gilt das je Geschwister einzeln; ein Geschwister bezeugt das andere nicht mehr, weil
   keines mehr nachträglich zählt.
   **Terminierung.** Der Beweis zeigt, dass nach dem Ende des Schließens nichts offen ist; dass es endet, sichert die
   Markierung. Sei μ ihr Commit.
   - Ein Schreiber, der nach μ prüft, sieht sie (c) und schreibt nichts. Der Grace-Zweig hält dabei höchstens kurz die
     Sperre auf E, die Rotation sperrt gar nichts.
   - Ein Schreiber, der vor μ geprüft hat, stützt sich auf eine Zeile, die vor μ bestätigt war: die Rotation auf ihren
     Vorgänger, der Grace-Zweig auf E, das er vor der Prüfung gesperrt hat. Durchlauf 1 beginnt nach μ und sieht diese
     Zeile. Erreicht er sie zuerst, scheitert der Schreiber an ihr (`Revoked`). Sonst wartet Durchlauf 1 auf ihn, und
     die neue Zeile ist bestätigt, bevor Durchlauf 1 endet.
   - Durchlauf 2 sieht alle diese Zeilen. Halten kann sie nur noch ein Schreiber, der nach μ prüft, und der schreibt
     nichts. Durchlauf 2 schließt sie, die Zählung danach findet null.
   Das Schließen endet darum nach höchstens zwei Durchläufen nach der Markierung, gleich wie viele Anfragen kommen und
   wie schnell ein Akteur rotiert. Die Klebrigkeit des Kettenendes (Entscheidung 2) bleibt: Sie macht `Revoked` für
   spätere Grace-Anfragen auch ohne Blick auf die Markierung eindeutig und trägt den Beweis. Die Grenze von 32
   Durchläufen bleibt als Netz; wird sie erreicht, ist das ein Fehler im Store oder der Fall unten.
   **Grenze: Schließen in einer Transaktion des Aufrufers (Owner-Entscheid F4, 21.11.).** Das betrifft zwei Wege: das
   Abmelden und den Wiedergebrauchszweig eines Refresh, den der Aufrufer in seine Klammer legt (das Muster aus
   `README.md:853–866`, abgesichert in `TransactionBracketTests.cs:121–142`; das ist der Diebstahlsfall). Das Schließen
   im Wiedergebrauchszweig läuft außerhalb der eigenen Grace-Transaktion des Stores, die dort noch nicht offen ist,
   aber innerhalb der Klammer des Aufrufers. Andere Verbindungen sehen die Markierung dann erst mit deren Commit. Bis
   dahin bleiben beide Mechanismen aus F1 offen:
   - **Generationen:** Ein Akteur, der je Durchlauf eine Generation rotiert, kann das Schließen bis zur Grenze treiben.
   - **Deadlock:** Grace-Anfragen sehen die Markierung nicht, schreiben ein Geschwister und warten in ihrer Kappung auf
     Zeilen, die ein voriger Durchlauf gesperrt hat. Der nächste Durchlauf wartet auf E. Mit einer Flut lässt sich
     dieser Deadlock herbeiführen. Ein Treffer genügt, denn in der Klammer gibt es keine Wiederholung. Unter PostgreSQL
     ist danach die Transaktion des Aufrufers abgebrochen, und der Rollback nimmt Markierung und Schließungen mit.
     Unter sperrendem READ COMMITTED kommt ein zweiter Zyklus dazu: Die Grace-Anfrage hält E und wartet beim Lesen der
     Markierung auf deren unbestätigtes INSERT.
   Auf einer Verbindung lässt sich das nicht schließen, weil die Markierung erst mit dem Commit sichtbar wird. Der
   Owner hat am 21.11. entschieden („Rat ändern“): Das README, das das Muster heute ohne Einschränkung erlaubt,
   ergänzt einen Rat (Refresh und Abmelden nicht in eine eigene Transaktion
   legen, wenn der Schutz gegen Token-Diebstahl zählt; die Last, unter der er dort versagt, erzeugt der Angreifer
   selbst, mit einer Flut oder schnellen Rotationen; die Audit-Zeile danach schreiben). Das Muster bleibt
   erlaubt, und das Restrisiko steht in README, `MIGRATION.md` und den Remarks. **Vor Release 7.2.0** kommt ein eigenes
   Gate mit PostgreSQL-Beweis (Testcontainers). Darin stellt ein Test F4 nach; ist er rot, wird F4 technisch
   geschlossen, etwa mit einer eigenen Verbindung für die Markierung. Weder `TokenSessionService` noch die Demo
   öffnen eine Transaktion.
   **Transaktion des Aufrufers:** Unter READ COMMITTED gilt dasselbe. Die Sperren halten bis zum Commit des Aufrufers,
   ein gleichzeitiges Abmelden wartet so lange. Eine lange Transaktion verzögert also das Abmelden, öffnet aber kein
   Fenster. Die Grenze aus Fassung 1 (ein offenes Fenster für die Dauer der Transaktion) entfällt; zur Grenze für das
   Ende des Schließens siehe oben. Der README-Abschnitt „Inside a transaction you already have“
   (`README.md:851–874`) nennt künftig die Isolation, das Warten, die Grenze für Abmelden und Wiedergebrauch und den
   geänderten Rat; die Remarks von `IRefreshTokenStore` und `TokenSessionService` ebenso. Als Transaktion des
   Aufrufers soll künftig `Database.CurrentTransaction` gelten und ebenso eine umgebende `TransactionScope` (heute
   prüft `EntityFrameworkRefreshTokenStore.cs:111` nur das Erste), beim Beitreten
   (`ClaimAsync`, Grace-Zweig: keine eigene Transaktion öffnen) wie bei der Wiederholung
   (`System.Transactions.Transaction.Current`).
   **REPEATABLE READ, SERIALIZABLE oder SNAPSHOT** (vom Aufrufer gewählt): Unter PostgreSQL (REPEATABLE READ,
   SERIALIZABLE) und SQL Server (SNAPSHOT) erwarten wir bei einer Überschneidung einen Fehler der Datenbank
   (Serialisierungs- bzw. Update-Konflikt), also ein lautes Scheitern, nie still eine offene Sitzung. Geprüft ist das
   nicht.
   **Nicht** unter MySQL/InnoDB, wo REPEATABLE READ der Standard ist: Dort liest das Update den aktuellen Stand ohne
   Konflikt, die Zählung aber aus dem Snapshot der Transaktion, und kann in einer Transaktion des Aufrufers still null
   ergeben. Das Schließen braucht dort READ COMMITTED oder keine Transaktion des Aufrufers; Remarks und README sagen das.
   **Deadlocks** sind eine neue Fehlerart: Der Grace-Zweig sperrt E und dann in der Kappung überzählige Zeilen, die
   Rotation ihren Vorgänger und dann ebenso, das Schließen alle offenen Zeilen über `SessionId` bzw. `(SubjectId,
   RevokedAt)` (`NoeliaRefreshToken.cs:90–91`). Kreuzen sich diese Reihenfolgen, bricht die Datenbank eine Transaktion
   ab.
   - **Trifft es einen Refresh,** scheitert er laut, seine Transaktion wird zurückgerollt, und er hinterlässt nichts.
   - **Trifft es das Schließen,** wiederholt der Store die abgebrochene Anweisung bis zu dreimal, nach kurzen
     Wartezeiten (20, 100, 500 ms), wenn er keine Transaktion des Aufrufers hat und die Ausnahme eine `DbException`
     mit `IsTransient` ist (auch als innere Ausnahme; Npgsql setzt das für Deadlocks und Serialisierungsfehler, für
     andere Anbieter erwarten wir es, geprüft ist es nicht).
   - **Ohne Transaktion des Aufrufers** können neue Schreiber nach der Markierung an keinem Deadlock mehr teilnehmen:
     Der Grace-Zweig hält nur E und wartet dabei auf nichts, die Rotation sperrt gar nicht. Ein Deadlock beim Schließen
     kommt also nur mit Schreibern zustande, die vor der Markierung begonnen haben. Ihre Menge steht mit der Markierung
     fest, darum endet auch das Kreuzen. Ein Akteur kann Deadlocks nicht wiederholt herbeiführen. In einer Transaktion
     des Aufrufers gilt das nicht (Grenze oben).
   - Scheitert auch die dritte Wiederholung, oder liegt eine Transaktion des Aufrufers vor, scheitert das Abmelden
     bzw. der Refresh laut, und die Sitzung kann offen sein; ein erneutes Abmelden ist idempotent.
   Folgen und `MIGRATION.md` nennen das.
   **InMemory** braucht keine Sperren: Ein Lock umfasst Prüfen, Schreiben, Kappen und Schließen. Er entscheidet am
   selben Kriterium (Kettenende offen), damit beide Stores dieselbe Conformance-Suite bestehen.
   **Getestet ist nur SQLite.** Die Tests laufen gegen einen einzigen Schreiber. Damit belegen sie die Reihenfolge der
   Schritte, aber nicht (b) auf einer MVCC-Datenbank. Conformance- und Augenblick-Tests gegen PostgreSQL über
   Testcontainers (`Category=Integration`, wie bei Redis) kommen als Folge-Issue (Nacharbeit G-0006) in ein eigenes
   Gate vor Release 7.2.0, mit dem Test für F4 (Grenze oben).

4. **Wessen Sitzungen: nur die der angemeldeten Person (auf ADR-0007).** Die Person, deren Sitzungen ein Abmelden
   beendet, kommt aus `ICurrentPrincipal` (seit G-0018 nur aus angemeldeten Personen-Identitäten:
   `PrincipalFactory.cs:22` über `ServiceCallerIdentity.PersonsOf`, `ServiceCallerHandler.cs:46–47`), nie aus einem
   Claim irgendeiner Identität von `HttpContext.User`. Ein Dienst-Token nennt bewusst keine Person und trägt kein `sub`
   (ADR-0003, ADR-0002); dieses ADR folgert daraus, dass ihm keine Sitzung gehört. Mit einem Dienst-Token steht keine
   Person hinter der Anfrage, `ICurrentPrincipal.Require()` wirft (`ICurrentPrincipal.cs:41–45`), und es wird nichts
   beendet. Der Widerruf der Access-Tokens gilt **je Personen-Identität** wie in ADR-0007 Entscheidung 4: Die Middleware
   prüft jede angemeldete Personen-Identität einzeln, der Stichtag einer Person trifft jede ihrer Identitäten.
   **Zwei Personen-Identitäten in einer Anfrage** (zwei Personen-Schemes): `ICurrentPrincipal` ist die erste
   (`PrincipalFactory.cs:22`, `FirstOrDefault`). Ein Abmelden beendet darum nur die Sitzungen und sperrt nur die
   Access-Tokens dieser ersten Person. Die zweite Person meldet sich mit einer eigenen Anfrage ab. In der Bibliothek
   ändert sich dafür nichts; die Regel steht in den Remarks von `SignOutEverywhereAsync` und `SignOutAsync` (die Sitzung
   kommt aus derselben angemeldeten Identität wie die Person) und im README.

5. **Demo: Abmelden beendet alles und sperrt sofort.**
   - `UserService`, `TodoService` und der Monolith führen `UseTokenRevocation()` in ihrer ausdrücklichen Kette, direkt
     nach `UseAuth()` und vor `UsePrincipal()` (`MiddlewarePipelineModule.cs:273–275`: „Place after UseAuth“). Die
     Standardkette selbst enthält den Schritt nicht (`ServiceCollectionExtensions.cs:208–233`); das ändert dieses Gate
     nicht.
     Der Gateway braucht nichts: Er liest keine Tokens und leitet `/api/auth/{everything}` nur weiter.
   - `/auth/sign-out` und `/auth/sign-out-everywhere` tun dasselbe (Owner 27.10.), im UserService
     (`Program.cs:224–248`) und im Monolithen (`Program.cs:180–203`): zuerst
     `ITokenSessionService.SignOutEverywhereAsync(person)`, **danach** `now` lesen, dann
     `ITokenRevocationWriter.RevokeSubjectBeforeAsync(person, now, "signed out")`. Diese Reihenfolge, weil die umgekehrte
     schlechter endet. Käme der Stichtag zuerst, bekäme ein Refresh zwischen Stichtag und Schließen ein Access-Token,
     das nach dem Stichtag ausgestellt ist. Und schlägt das Schließen nach dem Stichtag fehl, holen sich gesperrte
     Access-Tokens mit offener Refresh-Kette sofort ein neues. Wirft das Schließen (Grenze aus Entscheidung 3,
     Deadlock), schreibt der Endpunkt den Stichtag trotzdem und antwortet danach mit einem Fehler: Gesperrte
     Access-Tokens sind besser als keine, und ein Wiederholen schließt den Rest. Schlägt der Widerruf fehl, antwortet
     der Endpunkt ebenfalls mit einem Fehler; das Abmelden ist idempotent und kann wiederholt werden.
   - **Rest-Rennen, dokumentiert:** Liegt der Store-Schritt eines Refresh vor dem Schließen, wird sein Access-Token
     trotzdem erst danach ausgestellt (`UserService.Api/Program.cs:199–216`, Monolith `:159–174`). Fällt dessen `iat`
     über eine Sekundengrenze hinter den aufgerundeten Stichtag, gilt dieses eine Access-Token bis zu seinem Ablauf. Die
     Refresh-Kette ist trotzdem zu (Entscheidung 3). Begrenzt ist das Rennen durch die Lebensdauer des Access-Tokens
     (`JwtSettings:ExpireMinutes`). README und Remarks sagen das.
   - `session_id` wird dort nicht mehr aus `http.User` gelesen.
   - Der Stichtag wird auf die volle Sekunde aufgerundet (`TokenRevocationKeys.ToCutoffSeconds`), `iat` < Stichtag ist
     gesperrt. Eine neue Anmeldung in derselben Sekunde wie das Abmelden kann darum bis zur nächsten Sekunde abgelehnt
     werden. Das ist bestehendes Verhalten der Bibliothek, kein Teil dieses Gates; die Demo-Tests warten es nicht weg,
     sondern melden sich neu an, nachdem die Sekunde vorbei ist.
   - README: Widerruf wirkt dienstübergreifend in Staging und Production (Valkey) und im Monolithen; in Development
     bei den Microservices nur in dem Prozess, der ihn schreibt (In-Memory je Prozess), also nicht im TodoService.
   - Tests prüfen die **Wirkung** in jedem Token-lesenden Dienst (Prüfplan, Q-0017): im UserService und im Monolithen
     über das Abmelden; im TodoService über einen Stichtag im eigenen Speicher; über Dienste hinweg mit einem
     gemeinsamen Widerrufsspeicher in beiden Test-Hosts, der für Valkey steht.
   - **Release-Gate:** Es sieht den Widerruf über Schritt S3 („Demo .NET tests against the candidate“,
     `demo/eng/release-gate.mjs:307`), also über diese Tests. Der laufende Stack mit Valkey (S5, S7) prüft ihn nicht.
     Ein Schritt gegen Staging/Production kommt als Folge-Issue (Nacharbeit G-0006).

6. **Q-0071: Warnung beim stillen Fallback, kein Bruch.**
   - **Still** heißt: Der Ring entsteht aus `JWT_SECRET`/`JwtSettings:Secret`, ohne dass der Aufrufer einen Schlüssel
     oder ein geteiltes Geheimnis ausdrücklich gewählt hat. Das sind genau zwei Wege: der Zweig `KeyRingFactory.cs:38–45`
     (über `InfrastructureBuilder.AddJwtAuthentication()` ohne Schlüssel und ohne Authority) und der direkte Aufruf von
     `services.AddJwtAuthentication(configuration, environment)`.
   - **Ausdrücklich** heißt: übergebene Schlüssel (`o.SigningKey`/`o.ValidationKeys`, auch
     `SigningKey.FromSharedSecret(...)`), `UseJwt(jwt => jwt.FromSharedSecret())`, `Issue(...)`, `VerifyOnly(...)` oder
     eine Authority.
   - Der Marker wird **an der Aufrufstelle** gesetzt, nicht in der gemeinsamen Methode: Die öffentliche Methode
     `services.AddJwtAuthentication(configuration, environment)` und der Zweig in `JwtModule` setzen ihn;
     `FromSharedSecret()` ruft einen internen Kern ohne Marker. Sonst würde auch der ausdrückliche Weg warnen.
   - Ein gehosteter Dienst, registriert mit `TryAddEnumerable` (also einmal, auch wenn beide Wege laufen), schreibt beim
     Start **eine** Warnung (Kategorie `Noelia.Infrastructure.Security.Keys`). Der Text nennt nur Form und Weg: Der Ring
     stammt aus einem geteilten Geheimnis der Konfiguration, weil keine Schlüssel übergeben wurden; 8.0 startet so nicht
     mehr. Ausdrücklich geht es mit `UseJwt(jwt => jwt.Issue(...))`/`VerifyOnly(...)` oder mit `o.SigningKey` bzw.
     `o.ValidationKeys` und `SigningKey.FromSharedSecret(...)`; wer das geteilte Geheimnis behalten will, schreibt
     `UseJwt(jwt => jwt.FromSharedSecret())`. Kein Wert, keine Länge, kein Name einer Variablen mit Inhalt.
   - `noelia.jwt.key-separation`, `KeyRing` und gemischte Ringe bleiben unverändert. Marker und Dienst sind intern; die
     öffentliche API wächst nicht. G-0019 übernimmt dieselbe Abgrenzung für den Startabbruch.

7. **Keine neue öffentliche API, Minor 7.2.0.** Kein neuer Enum-Wert, keine neue Methode, keine neue Spalte. Für
   Konsumenten ändert sich Verhalten an sechs Stellen, alle in die geschlossene oder laute Richtung, Text für
   `MIGRATION.md`:
   - **Neu in der Tabelle:** je Sitzung, die das Schließen nach Schritt 1 erfasst (die eine Sitzung bzw. jede Sitzung
     der Person mit Obergrenze nach `at` oder mit einer offenen Zeile), eine geschlossene, abgelaufene
     Markierungszeile (Entscheidung 3). Keine
     Spalte, keine Migration. Ihre `Id`-Ableitung mit dem Domain-Separator `noelia.refresh-token.session-ended.v1` ist
     Datenformat-Oberfläche: Sie zu ändern wäre eine Migration. Eigene Abfragen auf `noelia_refresh_tokens` sehen diese
     Zeilen als geschlossene Tokens. Ein Zurückgehen auf 7.1.x liest sie genauso und bleibt möglich. Refreshs lesen
     die Markierung über den Primärschlüssel (die Rotation einmal, der Grace-Zweig vor und nach seiner Sperre). Das Schließen ruft für die Markierung erstmals
     `SaveChanges` auf dem geteilten Kontext; wie schon `CreateAsync` und die Rotation schreibt das ausstehende
     Änderungen der Anwendung mit. In einer Transaktion der Anwendung gilt die Grenze aus Entscheidung 3 für
     Abmelden und Wiedergebrauch, mit dem geänderten README-Rat.
   - Ein Refresh im Grace-Fenster ergibt `Revoked` statt eines neuen Tokens, wenn das Ende der Kette geschlossen ist:
     nach dem Abmelden, nach einer Kappung des Kettenendes und auf dem Weg über das erneute Lesen. Ebenso ergibt jede
     Rotation und jeder Grace-Refresh `Revoked`, der nach der Markierung eines laufenden Abmeldens kommt (EF).
   - Ein Refresh im Grace-Fenster nach der Obergrenze der Sitzung ergibt `SessionExpired` statt eines toten Tokens.
   - `CloseSessionAsync`/`CloseAllSessionsAsync` können mehr als ein Update ausführen und werfen nach 32 Durchläufen.
   - Der Grace-Zweig läuft in einer Transaktion und sperrt das Kettenende bis zum Commit.
   - Unter PostgreSQL und SQL Server sind Deadlocks zwischen Refresh, Kappung und Abmelden möglich. Ein Refresh scheitert
     dann laut; das Schließen wiederholt bis zu dreimal und scheitert erst danach laut, in einer Transaktion der
     Anwendung sofort (Freigabe, M1).
   **Der Vertrag** steht in den XML-Remarks von `IRefreshTokenStore`, denn die Conformance-Suite liegt in `tests/` und
   wird nicht ausgeliefert: Ein Geschwister entsteht nur, solange das Ende der Kette des Vorgängers offen ist,
   entschieden unter einer Sperre auf diesem Ende, auf die ein gleichzeitiges Schließen wartet. Das Schließen kehrt erst
   zurück, wenn ein frisches Lesen nichts Offenes mehr findet, oder es wirft. Es muss auch gegen gleichzeitige
   Rotationen in begrenzt vielen Schritten enden; wie, ist Sache des Stores (EF: Markierungszeile, InMemory: ein
   Lock). Ein geschlossenes Token ohne `ReplacedBy`
   ergibt auf jedem Weg `Revoked`. Der Satz in `IRefreshTokenStore.cs:24–29` („Concurrent callers must see
   RotatedWithinGrace or ReuseDetected“) bekommt `Revoked` dazu, für den Fall, dass das Kettenende inzwischen
   geschlossen ist. Eigene `IRefreshTokenStore` brauchen diese Semantik, sonst bleibt NO-1 bei ihnen offen.
   `MIGRATION.md` verweist dafür auf den Quelltext von `RefreshTokenStoreConformance`.
   Dazu die Warnung aus Entscheidung 6 und die Ankündigung für 8.0.

8. **Was dieses Gate nicht anfasst.** Die Bibliothek koppelt `SignOutEverywhereAsync` nicht an den Widerrufsspeicher
   (`TokenSessionService` bleibt ohne `ITokenRevocationWriter`); das wäre eine eigene Entscheidung über Abhängigkeiten
   und Fehlerverhalten eines öffentlichen Dienstes. Die Stellen aus #70 bleiben dort. Der Startabbruch für den stillen
   Fallback ist G-0019.

## Verworfen

- **Schreiben, dann nachprüfen (Fassung 1).** Die Nachprüfung zählte jedes andere offene Token als Beleg. Zwei
  gleichzeitige Geschwister bezeugten sich darum gegenseitig, und nach dem Abmelden blieben beide offen (Runde 1, M1).
  Außerdem schloss sie bei Erlaubnis 1 ein zu Recht einziges Geschwister (m1).
- **Die Nachprüfung zählt nur Tokens, die nicht aus dem Grace-Zweig stammen.** Das braucht eine Unterscheidung, die die
  Tabelle nicht trägt; ein Geschwister ist nur daran zu erkennen, dass kein Vorgänger auf es zeigt. Außerdem bliebe die
  Nachprüfung ein Lesen ohne Sperre.
- **Schließen, bis ein Update null Zeilen trifft.** Ein Durchlauf, der auf eine gleichzeitige Rotation wartet, trifft
  null Zeilen, während deren Nachfolger offen ist (Entscheidung 3).
- **Grace-Zweig sperrt alle offenen Zeilen der Sitzung (Fassung 2).** Sicher, aber ohne Terminierung: Jede neue
  Grace-Anfrage sperrt das jüngste Geschwister, bevor der nächste Durchlauf es erreicht, und ein stetiger Strom
  paralleler Anfragen mit demselben alten Token treibt das Schließen bis zur Grenze (Delta-Runde, N1). Außerdem hieß
  „null Zeilen“ dort nicht sicher „beendet“ (N2).
- **SERIALIZABLE erzwingen.** Die Isolation wählt die Anwendung für ihre Verbindung und ihre Transaktion; der Store würde
  sie in einer Transaktion des Aufrufers gar nicht ändern können.
- **Neue Spalte `SessionClosedAt` oder eine Sitzungstabelle.** Jede Anwendung müsste eine
  Migration ihrer Tabelle ausrollen; die Tabelle ist Datenformat-Oberfläche. Für die Sicherheit reichen Sperren auf den
  bestehenden Zeilen, für das Ende die Markierungszeile.
- **Eine Sperrzeile je Sitzung, die jeder Schreiber sperrt.** Sie würde jede Rotation einer Sitzung hinter jede andere
  stellen, auch ohne Abmelden. Die Markierung kostet nur beim Abmelden ein Schreiben und beim Refresh ein Lesen.
- **Das Restrisiko aus Fassung 3 annehmen.** Gegenoption zum Owner-Entscheid F1 vom 21.11.; gewählt wurde die
  Markierungszeile.
- **Beim Abmelden `RevokedAt` der rotierten Vorgänger überschreiben.** `RevokedAt` ist der Beginn des Grace-Fensters;
  ein späterer Wert würde das Fenster **verlängern**, also das Gegenteil bewirken.
- **Beim Abmelden `SessionExpiresAt` aller Zeilen auf die Abmeldezeit setzen.** Würde die gespeicherte Obergrenze
  umdeuten und bräuchte dieselbe Behandlung des Augenblicks; der abgeleitete Zustand reicht.
- **`ReuseDetected` als Ergebnis.** Falscher Diebstahlsalarm im Log für einen zweiten Tab.
- **Neuer Wert `SessionEnded`.** Öffentliche API für eine Aussage, die `Revoked` schon trifft.
- **Gemischte Ringe sperren** (ursprüngliche DoD von Q-0026). Der dokumentierte Umzug Shared Secret → Schlüsselpaar braucht
  sie; die Prüfung meldet sie außerhalb von Development schon als Fail (Owner 21.11.).

## Prüfung (Plan)

**Rot vor dem Fix** gilt für die Tests, die einen Fehler belegen; sie sind unten mit „rot“ markiert, die Gegenprobe
steht im PR. Tests, die Bestehendes absichern (Negativtests, die bisherigen Grace-Tests), sind vorher wie nachher grün.
Q-0018 enthält nur Tests; seine Gegenprobe sind die kaputten Ketten.

**Boss-Tests:** Q-0016 `RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded`. Der Name bleibt, weil Q-0016 schon
läuft und `gate_plan` den Boss nicht überschreibt; das Ergebnis heißt `Revoked`. Q-0071
`SharedSecretFallback_LogsWarningAtStart`. Q-0017 `SignOut_RevokesAccessToken`. Q-0018 `RevocationPipeline_E2E` und
`AuthorityPath_RejectsForeignAlgKidIss`. Gate-Boss: die E2E-Suite RevocationPipeline grün in CI.

- **Conformance (`RefreshTokenStoreConformance`, läuft für InMemory und EF/SQLite):**
  - `RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded` (Boss): `Revoked`, danach kein offenes Token. Rot.
  - `RefreshGrace_AfterSignOut_ReturnsSessionEnded`: dasselbe für eine Sitzung. Rot.
  - `RefreshGrace_AfterTheCeiling_IsSessionExpired`: `SessionExpired`, kein Geschwister. Rot.
  - `RefreshGrace_WithAllowanceOne_KeepsTheSessionOpen` (Erlaubnis 1): `RotatedWithinGrace`, genau ein offenes Token,
    die Sitzung ist nicht beendet. Sichert ab.
  - `RefreshGrace_AfterTheCapClosedTheChainEnd_IsRevoked` (Erlaubnis 1): ein drittes Einlösen desselben alten Tokens
    ergibt `Revoked`, das Geschwister des zweiten Tabs bleibt offen. Rot (Verhaltensänderung, geschlossene Richtung).
  - `RefreshGrace_FollowsTheChainToItsOpenEnd`: R0 → R1 → R2, danach R0 im Fenster → Geschwister. Sichert ab.
  - `RefreshGrace_WhenTheChainIsBrokenOrTooLong_IsRevoked`: eine gelöschte Zeile in der Kette bzw. eine Kette mit 33
    Gliedern → `Revoked`. Rot.
  - `ConcurrentPresentations_BeyondTheAllowance_AreRevokedNeverRotatedTwice`: achtmal gleichzeitig bei Erlaubnis 5 →
    genau ein `Rotated`, kein `ReuseDetected`, die übrigen `RotatedWithinGrace` oder `Revoked`, höchstens 5 offene
    Tokens. Sichert ab (das heutige Verhalten erfüllt ihn schon; das „Rot“ für diesen Fall trägt
    `RefreshGrace_AfterTheCapClosedTheChainEnd_IsRevoked`).
  - `A_replay_after_the_grace_window_in_an_ended_session_is_still_reuse`: Abmelden, danach das alte Token nach dem
    Fenster → `ReuseDetected` (Reihenfolge 1 vor 2). Sichert ab.
  - Die bestehenden Grace-Tests bleiben grün (zwei Tabs ohne Abmelden bekommen weiter ein Geschwister), mit einer
    Ausnahme: `Concurrent_presentations_rotate_exactly_once_and_nobody_is_thrown_out` läuft mit Erlaubnis 8 statt 5
    (Entscheidung 2, Verhaltensänderung). Seine Erwartungen bleiben wörtlich.
- **Der Augenblick (nur EF, deterministisch über `DbCommandInterceptor` bzw. `DbTransactionInterceptor`):**
  - `TwoGraceSiblings_SignOutInBetween_AreBothRevoked` (M1): Zwei Clients lösen dasselbe rotierte Token ein und
    pausieren vor ihrem ersten schreibenden Befehl im Grace-Zweig. Das ist vor dem BEGIN, sonst wartet SQLite auf den
    Schreib-Lock, statt die Schritte zu verschränken. Dazwischen läuft `CloseAllSessionsAsync` vollständig. Erwartet:
    zweimal `Revoked`, null offene Tokens. Rot.
  - `SignOut_BetweenLockAndInsert_LeavesNothingOpen`: Der Grace-Zweig pausiert nach der Sperre, vor dem Schreiben. Auf
    einer zweiten Verbindung startet `CloseAllSessionsAsync` und wartet auf den Schreib-Lock. Danach läuft der
    Grace-Zweig weiter. Erwartet: Wenn das Schließen zurückkehrt, ist kein Token offen. Rot (heute gibt es keine Sperre;
    an derselben Stelle läuft das Schließen vollständig vor dem Schreiben des Geschwisters).
  - `Refresh_SignOutBetweenReadAndClaim_IsRevoked` (Weg über das erneute Lesen): pausiert nach dem Lesen (`:48–49`),
    vor dem Claim; `CloseSessionAsync` läuft. Erwartet: `Revoked`, null offene Tokens. Rot. Dazu derselbe Weg mit einem
    durch Kappung geschlossenen Token.
  - `GraceAfterTheFirstPass_IsRevoked_AndClosingEndsWithinTwoPasses` (N1): Ein Interceptor spielt eine Grace-Anfrage,
    die das Kettenende vor Durchlauf 1 gesperrt hat; eine weitere Grace-Anfrage mit demselben alten Token nach
    Durchlauf 1 ergibt `Revoked`, und das Schließen endet nach höchstens 2 Durchläufen (der Interceptor zählt sie).
    Rot (heute entsteht nach dem Schließen ein Geschwister).
  - `GraceLock_AfterTheChainEndWasRotated_FollowsTheChain` (N2): pausiert zwischen dem Suchen des Kettenendes und der
    Sperre; dazwischen wird das Kettenende rotiert. Erwartet: `RotatedWithinGrace`, nicht `Revoked`. Sichert ab.
  - `CloseSession_WhenTokensKeepAppearing_ThrowsAfter32Passes`: Ein Interceptor legt nach jedem Durchlauf ein neues
    offenes Token an. Erwartet: `InvalidOperationException` nach dem 32. Durchlauf, die Meldung nennt die Sitzung und
    keinen Token-Wert. Dasselbe für `CloseAllSessionsAsync`, dessen Meldung die Person nennt. Rot (heute kehrt das
    Schließen nach einem Update zurück).
  - `Rotation_AfterTheMarker_IsRevoked`: Ein Interceptor rotiert ein offenes Token, nachdem das Schließen die
    Markierung geschrieben hat und bevor Durchlauf 1 läuft. Erwartet: `Revoked`, kein neues Token, danach nichts offen.
    Rot (heute rotiert sie).
  - `Grace_AfterTheMarker_IsRevoked`: dasselbe für eine Grace-Anfrage mit dem alten Token. Rot.
  - `Close_AgainstARotationBeforeEveryPass_EndsWithinTwoPassesAfterTheMarker`: Der Interceptor rotiert vor jedem
    Durchlauf das jüngste offene Token (der schnelle Akteur). Erwartet: Das Schließen kehrt nach höchstens zwei
    Durchläufen zurück, nichts ist offen. Sichert ab; auf SQLite laufen Schreiber nacheinander, den Wettlauf unter MVCC
    prüft erst die PostgreSQL-Conformance (Nacharbeit).
  - `Close_OnATransientError_RetriesUpToThreeTimes`: Der Interceptor wirft beim ersten Durchlauf eine `DbException`
    mit `IsTransient`. Erwartet: Das Schließen gelingt; rot, denn heute wiederholt es nicht. Viermal geworfen → es
    wirft; in einer Transaktion des Aufrufers → es wirft sofort; diese beiden Teile sichern ab.
  - `Close_WhenTheMarkerAppearsBetweenReadAndInsert_TreatsItAsMarked`: Ein Interceptor schreibt die Markierung
    zwischen dem Lesen und dem INSERT (der Konfliktpfad, deterministisch). Erwartet: keine Ausnahme, genau eine
    Markierungszeile, und ein `SaveChanges` danach auf demselben Kontext gelingt. Rot.
  - `MarkerId_MatchesTheFixedTestVector`: eine feste `SessionId` ergibt eine feste Markierungs-Id. Rot.
  - `CloseAll_MarksEverySessionBeforeItsCeiling`: Sitzungen der Person mit `SessionExpiresAt` nach `at` bekommen eine
    Markierung, auch eine ohne offenes Token; eine mit erreichter Obergrenze nur, wenn sie noch eine offene Zeile hat,
    und deren offene Zeilen werden wie heute geschlossen. Rot.
  - `Close_OnAnotherError_WhileWritingTheMarker_Throws`: Ein anderer Fehler als der Schlüsselkonflikt beim Schreiben
    der Markierung wird nicht abgefangen. Sichert ab.
  - `Close_InsideATransactionScope_DoesNotRetry`: eine umgebende `TransactionScope` gilt als Transaktion des
    Aufrufers; ein transienter Fehler wird nicht wiederholt, und Rotation wie Grace-Zweig öffnen darin keine eigene
    Transaktion. Sichert ab für die Wiederholung (heute wiederholt nichts); rot für das Beitreten, sofern das heutige
    `BeginTransaction` unter einer `TransactionScope` scheitert oder eine zweite Transaktion öffnet.
  - `MarkerConflict_InsideBeginTransaction_LeavesTheTransactionUsable` (M4): Schlüsselkonflikt beim Schreiben der
    Markierung in einer Klammer aus `BeginTransaction`; danach gelingt das frische Prüfen und ein weiterer Befehl in
    derselben Transaktion. Rot (es gibt die Markierung heute nicht).
  - `TheMarker_IsNeverOpen_NeverListed_AndPurged`: nach dem Schließen zählt die Markierung nicht als offen, erscheint
    nicht in `ActiveSessionsAsync`, nicht in der Rückgabe, und der Purge entfernt sie. Rot (es gibt sie heute nicht).
  - `CloseAll_IgnoresASignInThatStartsDuringIt` (F3): Eine Anmeldung derselben Person zwischen Durchlauf 1 und
    Zählung bleibt offen, und das Schließen kehrt normal zurück. Sichert ab.
  - `Grace_InsideTheCallersTransaction_HoldsTheLockUntilItsCommit` (neben `TransactionBracketTests`): Der Grace-Zweig
    tritt einer offenen Transaktion des Aufrufers bei; ein Schließen auf einer zweiten Verbindung kehrt erst nach deren
    Commit zurück, und danach ist nichts offen. Sichert ab (auf SQLite hält die Klammer schon heute den Schreib-Lock).
- **Q-0071:**
  - `SharedSecretFallback_LogsWarningAtStart` (Boss): `InfrastructureBuilder.AddJwtAuthentication()` ohne Schlüssel →
    beim Host-Start genau eine Warnung, ohne Geheimniswert. Rot.
  - Direkter Aufruf `services.AddJwtAuthentication(configuration, environment)` → genau eine Warnung. Rot.
  - Beide Wege in einem Host → genau eine Warnung. Rot.
  - Keine Warnung mit `UseJwt(jwt => jwt.FromSharedSecret())`, mit `o.SigningKey = SigningKey.FromSharedSecret(...)`, mit
    `Issue(...)` und mit Authority. Sichert ab.
- **Q-0017 (Demo-Tests):**
  - `SignOut_RevokesAccessToken` (Boss, UserService): Nach `/auth/sign-out` ergibt das alte Access-Token 401, der
    Refresh 401, und eine zweite, parallel geöffnete Sitzung derselben Person ist beendet (Refresh und Access-Token 401).
    Ersetzt `The_access_token_outlives_the_sign_out_until_it_expires`. Rot.
  - Monolith: dasselbe über `/auth/sign-out` am Monolithen, im bestehenden Projekt `demo/tests/Demo.Monolith.Tests`
    (`MonolithFactory`), das S3 schon ausführt. Rot.
  - Wirft das Schließen, schreibt der Endpunkt den Stichtag trotzdem und antwortet mit einem Fehler (Store-Double, das
    wirft). Rot.
  - TodoService in Development: einen Stichtag über den eigenen `ITokenRevocationWriter` aus den Diensten des Test-Hosts
    schreiben; danach ergibt das alte Token an `/api/todos` 401. Ohne `UseTokenRevocation` in
    `TodoService.Api/Program.cs:59–80` ist dieser Test rot.
  - Über Dienste hinweg (`MicroserviceEndToEndTests`): Beide Test-Hosts bekommen dieselbe
    `InMemoryTokenRevocationStore`-Instanz; sie steht für Valkey. Nach dem Abmelden am UserService ergibt das alte Token
    am TodoService 401. Rot.
  - Eine neue Anmeldung nach der Sekundengrenze funktioniert. Sichert ab.
- **Q-0018:**
  - `AuthorityPath_RejectsForeignAlgKidIss` (Boss): Authority-Pfad mit lokalem JWKS-Stub (Backchannel im Test).
    Unbekanntes `kid`, falscher `iss`, falsches `aud` → 401; richtiges Token → 200. Der Name behält „Alg“, weil der
    Boss der Quest ihn trägt. Falsches `alg` ergibt auf dem Authority-Pfad heute 200, weil dort kein Pin gesetzt ist;
    nach dem Owner-Entscheid vom 21.11. fehlt der Fall in G-0006 und kommt im Härtungs-Gate nach 7.2.0 (#72). Den
    Algorithmus-Pin prüft G-0006 auf dem Schlüsselring-Pfad: HS512 mit demselben Geheimnis → 401.
  - `RevocationPipeline_E2E` (Boss): Sitzungs-Widerruf (`RevokeSessionAsync`) über die echte Kette → 401, andere
    Sitzung derselben Person → 200; `SignOutEverywhereAsync` über `TokenSessionService` lässt im Fenster kein
    Geschwister entstehen.
  - JwtBearer → `UseTokenRevocation` → widerrufenes Token → 401 ist schon belegt
    (`ServiceCallerFullChainTests.A_revoked_person_token_is_refused`, G-0016), nur verweisen.
  - **Gegenprobe, Teil der Suite:** Dieselben Prüfungen laufen gegen absichtlich kaputte Ketten und müssen dort
    durchfallen, also das Token durchlassen: ohne `UseTokenRevocation`; mit `UseTokenRevocation` vor `UseAuth`; mit
    abgeschalteter Prüfung von Issuer bzw. Audience; ohne Algorithmus-Pin (`ValidAlgorithms` leer, auf dem
    Schlüsselring-Pfad). Fällt eine Prüfung
    gegen ihre kaputte Kette nicht durch, ist sie wertlos.

## Folgen

- NO-1 ist für die mitgelieferten Stores geschlossen, auch für gleichzeitige Geschwister und auf dem Weg über das
  erneute Lesen; WT Q-0008 kann auf 7.2.0 aufbauen. Das gilt, sobald das Schließen zurückkehrt; wann es laut scheitern
  kann, steht unten.
- Eigene Stores von Konsumenten bleiben offen, bis sie den Vertrag aus den Remarks von `IRefreshTokenStore` erfüllen
  (`MIGRATION.md`, Verweis auf die Suite).
- Ein Abmelden wartet auf laufende Refreshs derselben Sitzung, eine lange Transaktion des Aufrufers verzögert es. Ein
  Fenster öffnet das nicht.
- Gegen einen schnellen Akteur mit einem gültigen Refresh-Token endet das Abmelden nach höchstens zwei Durchläufen nach
  der Markierung. Ein Restrisiko bleibt für Abmelden und Diebstahl-Erkennung in einer Transaktion der Anwendung, bis
  zu deren Commit, über Generationen und über einen herbeigeführten Deadlock (Entscheidung 3). Der Owner hat es am
  21.11. angenommen. Das README ergänzt einen Rat dagegen.
- **Vor Release 7.2.0** kommt ein eigenes Gate mit PostgreSQL-Beweis (Testcontainers): Conformance- und
  Augenblick-Tests gegen PostgreSQL und ein Test, der F4 nachstellt. Ist er rot, wird F4 technisch geschlossen, etwa
  mit einer eigenen Verbindung für die Markierung. Bis dahin keine Release-Vorbereitung für 7.2.0 (Owner 21.11.).
- Unter PostgreSQL und SQL Server sind Deadlocks zwischen Refresh, Kappung und Abmelden möglich. Ein Refresh scheitert
  dann laut und hinterlässt nichts. Das Schließen wiederholt bis zu dreimal; danach, oder in einer Transaktion der
  Anwendung, scheitert das Abmelden bzw. der Refresh laut, und die Sitzung kann offen sein.
- Neue Zeilenart in `noelia_refresh_tokens` (Markierung, Datenformat ohne Migration); jede Rotation liest sie einmal.
- Getestet ist nur SQLite; PostgreSQL-Conformance über Testcontainers, die Bedingung „offen“ im Update der Kappung (N5,
  nur auf einer MVCC-Datenbank rot zu testen) und ein Widerrufsschritt im Release-Gate gegen den laufenden Stack
  werden ein Folge-Issue (Nacharbeit G-0006); der PostgreSQL-Teil mit dem F4-Test ist ein eigenes Gate vor Release
  7.2.0.
- Die Demo zeigt Widerruf in jedem Token-lesenden Dienst; das Release-Gate sieht ihn über S3, nicht im laufenden Stack.
- G-0019 entfernt in 8.0 den stillen Fallback, mit derselben Abgrenzung von „still“.
