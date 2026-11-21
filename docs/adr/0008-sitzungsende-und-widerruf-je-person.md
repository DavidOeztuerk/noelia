# ADR-0008 — Sitzungsende und Widerruf gelten je angemeldeter Person

**Status:** Entwurf (Fassung 2 nach Warden-Runde 1, [Review](https://github.com/DavidOeztuerk/noelia/pull/71#issuecomment-7054852190)), Delta-Runde offen. · **Datum:** 2026-11-21 · **Gate:** G-0006 (Rank A, Anmeldung und Sitzung) · **Quests:** Q-0016 (Grace-Fenster), Q-0071 (Warnung Shared Secret), Q-0017 (Demo), Q-0018 (Tests) · **Issues:** #35, #36, #37 · **Baut auf:** ADR-0007 (Rechte, Rollen und Nutzer-ID nur aus angemeldeten Identitäten), ADR-0003 (Dienst-Tokens hinter der Tür) · **Ziel:** Minor 7.2.0, **keine** neue öffentliche API. Dieses Gate veröffentlicht nichts.

Minor, weil 7.2.0 schon die Änderungen aus G-0018 trägt; der Fix allein wäre ein Patch. Der Text von Q-0016 sagt noch „Patch-Release“; das ist durch die Owner-Entscheide vom 21.11. überholt.

## Was Fassung 2 ändert

- **Entscheidung 3 neu (M1):** Zeilensperren statt Nachprüfung. Das Schließen endet erst, wenn eine frische Zählung nichts Offenes mehr findet; nach 32 Durchläufen wirft es (m3). Die Grenze bei einer Transaktion des Aufrufers entfällt (m5).
- **Entscheidung 2:** Ein Token, das ein Abmelden oder die Kappung zwischen Lesen und Claim geschlossen hat, ergibt `Revoked` (m2).
- **Entscheidung 4:** zwei Personen-Identitäten in einer Anfrage (m11).
- **Entscheidung 5:** Der Stichtag wird nach dem Schließen gelesen, das Rest-Rennen ist benannt (m6). Monolith und Gateway sind ausdrücklich genannt (m7). Demo-Tests prüfen die Wirkung im TodoService und über Dienste hinweg (M2).
- **Entscheidung 6:** was als „still“ gilt; der Marker sitzt an der Aufrufstelle (m8).
- **Entscheidung 7:** Der Vertrag steht in den Remarks von `IRefreshTokenStore` (m10).
- **Prüfplan:** zwei Geschwister, Abmelden zwischen Sperre und Schreiben, Weg über das erneute Lesen, Erlaubnis 1, Obergrenze der Schleife, Wirkung in der Demo, kaputte Ketten für Q-0018, alle Boss-Tests; „vorher rot“ nur für Tests, die einen Fehler belegen (M1, M2, m1, m2, m3, m9, c2).
- **Datenbanken:** Die Prämisse ist eingeschränkt, getestet ist nur SQLite (m4).

## Owner-Entscheide (wörtlich)

Wortlaut aus den Owner-Antworten der Sitzungen vom 27.10. und 21.11.2026, so in Fassung 1 übernommen; im Repository
sonst nicht festgehalten (sinngemäß: `.monarch/quests/Q-0017-…md`, DoD).

- **27.10.2026:** „Abmelden beendet alle Sitzungen der Person, mehrere Tabs und Geräte sind erlaubt.“
- **21.11.2026, Q-0026:** „7.2.0: nur Warnung“. Ausgeführt hieß die Option: 7.2.0 bricht nichts; der stille Fallback bleibt, loggt beim Start aber eine Warnung, und `MIGRATION.md` kündigt die Entfernung für 8.0 an. Der Startabbruch kommt in einem eigenen 8.0-Gate (G-0019, Q-0026). Gemischte Schlüssel bleiben für den Umzug erlaubt.
- **21.11.2026, Q-0017:** „Echter Widerruf“. Ausgeführt hieß die Option: Abmelden beendet alle Sitzungen und sperrt sofort alle Access-Tokens der Person; jeder Dienst prüft den Widerruf; dienstübergreifend in Staging und Production (Valkey) und im Monolithen, in Development bei den Microservices nur je Prozess, und das README sagt das.

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
Token aus, wenn die Sitzung noch offen ist. Damit ein Abmelden im selben Augenblick nicht verliert, hält der Refresh die
offenen Tokens der Sitzung fest, bis er fertig ist. Ein gleichzeitiges Abmelden wartet darauf und schließt danach auch
das neue Token. Das Abmelden hört erst auf, wenn wirklich nichts mehr offen ist. Die Datenbanktabelle der Anwendungen
ändert sich nicht.

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
   keine offene Sitzung zurück“. Dieses ADR erfüllt die erste, indem das Fenster den Zustand der Sitzung liest, und die
   zweite mit Entscheidung 3.

2. **Der Grace-Zweig fragt zuerst die Sitzung.** In `AlreadyRotatedAsync` (EF) bzw. im Zweig `ReplacedBy is not null`
   (InMemory), in dieser Reihenfolge:
   0. **Nur EF, Weg über das erneute Lesen:** Trägt die neu gelesene Zeile kein `ReplacedBy`, ergibt das
      **`ConsumeOutcome.Revoked`**. Sie wurde geschlossen, nicht rotiert, wie in `:61–64`. Damit lebt ein durch
      Abmelden oder Kappung geschlossenes Token auch auf diesem Weg nicht wieder auf.
   1. Fenster vorbei oder Vorgänger nie geschlossen → wie heute `ReuseDetected` mit Schließen der Sitzung.
   2. Sitzung hat kein offenes Token → **`ConsumeOutcome.Revoked`**, kein Geschwister, kein Schreiben. In EF entscheidet
      das die Sperre aus Entscheidung 3, nicht ein Lesen davor.
   3. Obergrenze der Sitzung erreicht (`now >= SessionExpiresAt`) → **`ConsumeOutcome.SessionExpired`**, kein Geschwister.
   4. Sonst Geschwister wie heute (`RotatedWithinGrace`), danach Kappung.
   `Revoked` statt eines neuen Werts: Der Enum-Wert sagt schon „The session was ended deliberately“. `ReuseDetected` wäre
   falsch: Ein zweiter Tab, der nach dem Abmelden sein altes Token zeigt, ist kein Diebstahl, und der Dienst würde einen
   Diebstahl ins Log schreiben. Reihenfolge 1 vor 2: Ein Wiedergebrauch nach dem Fenster bleibt ein Wiedergebrauch, auch
   in einer beendeten Sitzung (das Schließen ist idempotent).
   **Erlaubnis 1** (`MaxConcurrentTokensPerSession = 1`, zulässig): Der zweite Tab bekommt sein Geschwister, und die
   Kappung schließt das Token des ersten. Die Sitzung bleibt mit genau einem offenen Token offen. Kommen beide Tabs
   gleichzeitig, kann der zweite statt eines Geschwisters `Revoked` bekommen, nämlich dann, wenn die Kappung des ersten
   die einzige Zeile geschlossen hat, die der zweite sah. Das ist die geschlossene Richtung und folgt aus der Erlaubnis
   selbst: Bei Erlaubnis 1 kann ohnehin nur ein Tab weiterarbeiten.

3. **Abmelden gewinnt auch im selben Augenblick (EF): Zeilensperren auf bestehenden Zeilen.** Prüfen und Schreiben im
   Grace-Zweig sind mehrere Anweisungen; ein Abmelden dazwischen darf kein offenes Geschwister zurücklassen. Praktisch
   erreichbar ist dieser Augenblick durch paralleles Einlösen desselben alten Tokens und durch das Rotieren erhaltener
   Geschwister, auch ohne Angreifer: zwei weitere Tabs im Augenblick des Abmeldens. Zwei Hälften:
   - **Grace-Zweig: erst sperren, dann entscheiden, dann schreiben.** In einer Transaktion (eigene, oder die des
     Aufrufers, wenn eine offen ist, wie in `ClaimAsync`):
     1. Ein No-op-Update auf alle offenen Zeilen der Sitzung
        (`SetProperty(t => t.RevokedAt, t => t.RevokedAt)` mit `SessionId == s && RevokedAt == null && ReplacedBy == null`).
        Es ändert nichts, sperrt diese Zeilen aber bis zum Commit.
     2. Trifft es **null** Zeilen, ist die Sitzung beendet → `Revoked`, nichts schreiben.
     3. Obergrenze erreicht → `SessionExpired`, nichts schreiben.
     4. Geschwister schreiben, Kappung, Commit (bei eigener Transaktion).
   - **Schließen: wiederholen, bis eine frische Zählung nichts Offenes findet.** `CloseSessionAsync` und
     `CloseAllSessionsAsync` führen das bedingte Update aus und zählen danach in einer eigenen Anweisung die offenen
     Zeilen der Sitzung bzw. der Person. Ist die Zahl größer als null, folgt der nächste Durchlauf. Nach **32**
     Durchläufen wirft die Methode eine `InvalidOperationException` (fail closed): Das Abmelden meldet einen Fehler und
     ist idempotent wiederholbar; still zurückkehren wäre „vorhanden, aber nicht wirksam“. Die Rückgabe ist die Summe der
     geschlossenen Zeilen.
     **Warum die Zählung und nicht „bis ein Update null Zeilen trifft“:** Wartet ein Durchlauf auf die Sperre einer
     gleichzeitigen **Rotation**, sieht er danach den Vorgänger geschlossen und überspringt ihn. Den Nachfolger sieht er
     nicht, weil der erst nach dem Start der Anweisung bestätigt wurde. Der Durchlauf trifft also null Zeilen, während
     der Nachfolger offen ist. Die Zählung danach ist eine neue Anweisung und sieht ihn.
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
   alle offenen Zeilen. Eine Anmeldung (`CreateAsync`) beginnt eine neue Sitzung und fällt nicht darunter.
   Angenommen, das Schließen endet, weil die Zählung zum Zeitpunkt σ null offene Zeilen sieht, und danach bestätigt eine
   Transaktion X ein offenes Token der Sitzung. X hielt eine Sperre auf einer Zeile O, die beim Sperren offen war. Ein
   Schließen von O wartet zwischen dieser Sperre und dem Commit von X auf X (a). Ein Schließen, das vor der Sperre
   bestätigt war, hätte X gesehen, (c) oder (b), und X hätte O nicht gesperrt. Also ist O bis zum Commit von X nicht geschlossen, und dieser Commit liegt
   nach σ. War O vor σ bestätigt, hätte die Zählung O als offen gesehen: Widerspruch. Sonst hat eine frühere Transaktion
   O nach σ bestätigt, und für sie gilt dasselbe. Diese Kette ist endlich und endet bei einer Zeile, die vor σ bestätigt
   war, denn die Sitzung bestand schon beim Abmelden. Wieder ein Widerspruch.
   Für den Grace-Zweig heißt das: Sperrt er zuerst, wartet das Schließen auf seinen Commit und schließt danach auch das
   Geschwister. Die Antwort an den Client lautet dann `RotatedWithinGrace`, aber das Token ist zu, sobald das Abmelden
   zurückkehrt. Schließt das Abmelden zuerst, findet die Sperre null Zeilen, und das Ergebnis ist `Revoked`. Für zwei
   Geschwister aus Runde 1 (M1) gilt das je Geschwister einzeln; ein Geschwister bezeugt das andere nicht mehr, weil
   keines mehr nachträglich zählt.
   **Transaktion des Aufrufers:** Unter READ COMMITTED gilt dasselbe. Die Sperren halten bis zum Commit des Aufrufers,
   ein gleichzeitiges Abmelden wartet so lange. Eine lange Transaktion verzögert also das Abmelden, öffnet aber kein
   Fenster. Die Grenze aus Fassung 1 entfällt. Der README-Abschnitt „Inside a transaction you already have“
   (`README.md:851–872`) nennt künftig die Isolation, das Warten und den Rat, Refresh und Abmelden nicht in lange
   Transaktionen zu legen; die Remarks von `IRefreshTokenStore` und `TokenSessionService` ebenso.
   **REPEATABLE READ oder SNAPSHOT** (vom Aufrufer gewählt): Hier erwarten wir bei einer Überschneidung einen Fehler
   der Datenbank (Serialisierungs- bzw. Update-Konflikt), also ein lautes Scheitern, nie eine offene Sitzung. Geprüft
   ist das nicht.
   **InMemory** braucht keine Sperren: Ein Lock umfasst Prüfen, Schreiben, Kappen und Schließen.
   **Getestet ist nur SQLite.** Die Tests laufen gegen einen einzigen Schreiber. Damit belegen sie die Reihenfolge der
   Schritte, aber nicht (b) auf einer MVCC-Datenbank. Conformance- und Augenblick-Tests gegen PostgreSQL über
   Testcontainers (`Category=Integration`, wie bei Redis) kommen als Folge-Issue (Nacharbeit G-0006).

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
     Access-Tokens mit offener Refresh-Kette sofort ein neues. Schlägt der Widerruf fehl, antwortet der Endpunkt mit
     einem Fehler; das Abmelden ist idempotent und kann wiederholt werden.
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
   Konsumenten ändert sich Verhalten an vier Stellen, alle in die geschlossene Richtung, Text für `MIGRATION.md`:
   - Ein Refresh im Grace-Fenster einer beendeten Sitzung ergibt `Revoked` statt eines neuen Tokens, auch auf dem Weg
     über das erneute Lesen.
   - Ein Refresh im Grace-Fenster nach der Obergrenze der Sitzung ergibt `SessionExpired` statt eines toten Tokens.
   - `CloseSessionAsync`/`CloseAllSessionsAsync` können mehr als ein Update ausführen und werfen nach 32 Durchläufen.
   - Der Grace-Zweig läuft in einer Transaktion und sperrt die offenen Zeilen der Sitzung bis zum Commit.
   **Der Vertrag** steht in den XML-Remarks von `IRefreshTokenStore`, denn die Conformance-Suite liegt in `tests/` und
   wird nicht ausgeliefert: Ein Geschwister entsteht nur in einer offenen Sitzung, entschieden unter einer Sperre, auf
   die ein gleichzeitiges Schließen wartet. Das Schließen kehrt erst zurück, wenn ein frisches Lesen nichts Offenes mehr
   findet. Ein geschlossenes Token ohne `ReplacedBy` ergibt auf jedem Weg `Revoked`. Eigene `IRefreshTokenStore`
   brauchen diese Semantik, sonst bleibt NO-1 bei ihnen offen. `MIGRATION.md` verweist dafür auf den Quelltext von
   `RefreshTokenStoreConformance`.
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
- **SERIALIZABLE erzwingen.** Die Isolation wählt die Anwendung für ihre Verbindung und ihre Transaktion; der Store würde
  sie in einer Transaktion des Aufrufers gar nicht ändern können.
- **Neue Spalte `SessionClosedAt`, eine Sitzungstabelle oder eine Sperrzeile je Sitzung.** Jede Anwendung müsste eine
  Migration ihrer Tabelle ausrollen; die Tabelle ist Datenformat-Oberfläche. Seit Fassung 2 ist auch kein Fenster mehr
  übrig, das eine Sperrzeile schließen müsste: Sperren auf den bestehenden Zeilen reichen.
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
  - Die bestehenden Grace-Tests bleiben grün (zwei Tabs ohne Abmelden bekommen weiter ein Geschwister). Sichert ab.
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
  - `CloseSession_WhenTokensKeepAppearing_ThrowsAfter32Passes`: Ein Interceptor legt nach jedem Durchlauf ein neues
    offenes Token an. Erwartet: `InvalidOperationException` nach dem 32. Durchlauf. Rot (heute kehrt das Schließen nach
    einem Update zurück).
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
  - Monolith: dasselbe über `/auth/sign-out` am Monolithen (`Demo.Tests` bekommt dafür eine Referenz auf
    `Demo.Monolith.Api`). Rot.
  - TodoService in Development: einen Stichtag über den eigenen `ITokenRevocationWriter` aus den Diensten des Test-Hosts
    schreiben; danach ergibt das alte Token an `/api/todos` 401. Ohne `UseTokenRevocation` in
    `TodoService.Api/Program.cs:59–80` ist dieser Test rot.
  - Über Dienste hinweg (`MicroserviceEndToEndTests`): Beide Test-Hosts bekommen dieselbe
    `InMemoryTokenRevocationStore`-Instanz; sie steht für Valkey. Nach dem Abmelden am UserService ergibt das alte Token
    am TodoService 401. Rot.
  - Eine neue Anmeldung nach der Sekundengrenze funktioniert. Sichert ab.
- **Q-0018:**
  - `AuthorityPath_RejectsForeignAlgKidIss` (Boss): Authority-Pfad mit lokalem JWKS-Stub (Backchannel im Test). Falsches
    `alg`, unbekanntes `kid`, falscher `iss`, falsches `aud` → 401; richtiges Token → 200.
  - `RevocationPipeline_E2E` (Boss): Sitzungs-Widerruf (`RevokeSessionAsync`) über die echte Kette → 401, andere
    Sitzung derselben Person → 200; `SignOutEverywhereAsync` über `TokenSessionService` lässt im Fenster kein
    Geschwister entstehen.
  - JwtBearer → `UseTokenRevocation` → widerrufenes Token → 401 ist schon belegt
    (`ServiceCallerFullChainTests.A_revoked_person_token_is_refused`, G-0016), nur verweisen.
  - **Gegenprobe, Teil der Suite:** Dieselben Prüfungen laufen gegen absichtlich kaputte Ketten und müssen dort
    durchfallen, also das Token durchlassen: ohne `UseTokenRevocation`; mit `UseTokenRevocation` vor `UseAuth`; mit
    abgeschalteter Prüfung von Issuer bzw. Audience; ohne Algorithmus-Pin (`ValidAlgorithms` leer). Fällt eine Prüfung
    gegen ihre kaputte Kette nicht durch, ist sie wertlos.

## Folgen

- NO-1 ist für die mitgelieferten Stores geschlossen, auch für gleichzeitige Geschwister und auf dem Weg über das
  erneute Lesen; WT Q-0008 kann auf 7.2.0 aufbauen.
- Eigene Stores von Konsumenten bleiben offen, bis sie den Vertrag aus den Remarks von `IRefreshTokenStore` erfüllen
  (`MIGRATION.md`, Verweis auf die Suite).
- Ein Abmelden wartet auf laufende Refreshs derselben Sitzung, eine lange Transaktion des Aufrufers verzögert es. Ein
  Fenster öffnet das nicht.
- Getestet ist nur SQLite; PostgreSQL-Conformance über Testcontainers und ein Widerrufsschritt im Release-Gate gegen
  den laufenden Stack werden ein Folge-Issue (Nacharbeit G-0006).
- Die Demo zeigt Widerruf in jedem Token-lesenden Dienst; das Release-Gate sieht ihn über S3, nicht im laufenden Stack.
- G-0019 entfernt in 8.0 den stillen Fallback, mit derselben Abgrenzung von „still“.
