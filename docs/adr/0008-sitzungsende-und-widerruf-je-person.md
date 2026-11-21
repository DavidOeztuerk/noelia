# ADR-0008 — Sitzungsende und Widerruf gelten je angemeldeter Person

**Status:** Entwurf (Fassung 1), Warden-Runde 1 offen. · **Datum:** 2026-11-21 · **Gate:** G-0006 (Rank A, Anmeldung und Sitzung) · **Quests:** Q-0016 (Grace-Fenster), Q-0071 (Warnung Shared Secret), Q-0017 (Demo), Q-0018 (Tests) · **Issues:** #35, #36, #37 · **Baut auf:** ADR-0007 (Rechte, Rollen und Nutzer-ID nur aus angemeldeten Identitäten), ADR-0003 (Dienst-Tokens hinter der Tür) · **Ziel:** Minor 7.2.0, **keine** neue öffentliche API. Dieses Gate veröffentlicht nichts.

## Owner-Entscheide (wörtlich)

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
Token aus, wenn die Sitzung noch offen ist, und prüft das nach dem Schreiben ein zweites Mal, damit auch ein Abmelden in
genau diesem Augenblick gewinnt. Die Datenbanktabelle der Anwendungen ändert sich nicht.

Dazu: Die Demo sperrt beim Abmelden auch die Access-Tokens sofort und meldet alle Sitzungen der Person ab. Ein Dienst,
der still ein geteiltes Geheimnis als Schlüssel nimmt, schreibt beim Start eine Warnung; in 8.0 startet er so nicht mehr.

## Frage

1. Wie erkennt der Refresh-Store im Grace-Fenster eine beendete Sitzung, ohne die Tabelle `noelia_refresh_tokens` der
   Konsumenten zu ändern, und so, dass ein gleichzeitiges Abmelden nicht verliert?
2. Wessen Sitzungen beendet ein Abmelden, und woher kommt diese Person (ADR-0007)?
3. Was zeigt die Demo, und was meldet 7.2.0 zum stillen Shared-Secret-Fallback?

## Befund, im Quelltext belegt (Stand `2c1d36d`)

- `EntityFrameworkRefreshTokenStore.cs:56–59`: Ein Token mit `ReplacedBy` geht nach `AlreadyRotatedAsync`
  (`:146–170`). Liegt die Rotation weniger als `grace` zurück, entsteht ein Geschwister, **ohne** Blick auf den Rest der
  Sitzung.
- `CloseSessionAsync` (`:173–183`) und `CloseAllSessionsAsync` (`:186–196`) schließen nur Zeilen mit
  `RevokedAt == null && ReplacedBy == null`. Der rotierte Vorgänger trägt `RevokedAt` (Zeit der Rotation) und
  `ReplacedBy`; er bleibt, wie er ist, und öffnet im Fenster ein neues Geschwister.
- `InMemoryRefreshTokenStore.cs:66–78`: dieselbe Logik.
- Derselbe Grace-Zweig prüft auch die Obergrenze der Sitzung nicht: Ist `SessionExpiresAt` erreicht, entsteht ein
  Geschwister mit `ExpiresAt` in der Vergangenheit, gemeldet als `RotatedWithinGrace`.
- Demo: `UserService.Api/Program.cs:224–237` liest `session_id` über `http.User.FindFirst` aus **allen** Identitäten
  (die Fehlerklasse aus ADR-0007) und schließt nur diese eine Sitzung; kein Demo-Dienst führt `UseTokenRevocation`, und
  nichts schreibt in den registrierten Widerrufsspeicher (`DemoProviders.cs:104–107, 131–134`). `SignInTests.cs:146–165`
  hält fest, dass das Access-Token das Abmelden überlebt.
- `KeyRingFactory.cs:38–45`: Ohne Schlüssel und ohne Authority entsteht der Ring still aus `JWT_SECRET` bzw.
  `JwtSettings:Secret`. Die Prüfung `noelia.jwt.key-separation` meldet ein geteiltes Geheimnis außerhalb von Development
  als Fail (`BuiltInSecurityChecks.cs:342–348`), unterscheidet aber nicht zwischen still und ausdrücklich.

## Entscheidungen

1. **Beendet heißt: kein offenes Token mehr.** Eine Sitzung ist beendet, wenn sie keine Zeile mit `RevokedAt == null`
   und `ReplacedBy == null` hat. Das ist ein abgeleiteter Zustand, keine neue Spalte. Er trägt, weil
   - jede Rotation den Nachfolger in derselben Transaktion schreibt, in der sie den Vorgänger schließt (`ClaimAsync`);
   - die Kappung (`CapAsync`) immer mindestens ein Token offen lässt (`allowance >= 1` wird geprüft);
   - nur `CloseSessionAsync`, `CloseAllSessionsAsync` und die Erkennung eines Wiedergebrauchs eine Sitzung auf null
     offene Tokens bringen; ein abgelaufenes Token bleibt im Sinne dieser Regel „offen“.
   Der rotierte Vorgänger wird beim Abmelden **nicht** angefasst (siehe Verworfen). Die Quest-DoD „markieren auch
   rotierte Vorgänger“ ist damit anders erfüllt: Das Fenster liest den Zustand der Sitzung statt einer Markierung.

2. **Der Grace-Zweig fragt zuerst die Sitzung.** In `AlreadyRotatedAsync` (EF) bzw. im Zweig `ReplacedBy is not null`
   (InMemory), in dieser Reihenfolge:
   1. Fenster vorbei oder Vorgänger nie geschlossen → wie heute `ReuseDetected` mit Schließen der Sitzung.
   2. Sitzung hat kein offenes Token → **`ConsumeOutcome.Revoked`**, kein Geschwister, kein Schreiben.
   3. Obergrenze der Sitzung erreicht (`now >= SessionExpiresAt`) → **`ConsumeOutcome.SessionExpired`**, kein Geschwister.
   4. Sonst Geschwister wie heute (`RotatedWithinGrace`).
   `Revoked` statt eines neuen Werts: Der Enum-Wert sagt schon „The session was ended deliberately“. `ReuseDetected` wäre
   falsch: Ein zweiter Tab, der nach dem Abmelden sein altes Token zeigt, ist kein Diebstahl, und der Dienst würde einen
   Diebstahl ins Log schreiben. Reihenfolge 1 vor 2: Ein Wiedergebrauch nach dem Fenster bleibt ein Wiedergebrauch, auch
   in einer beendeten Sitzung (das Schließen ist idempotent).

3. **Abmelden gewinnt auch im selben Augenblick (EF).** Prüfen und Schreiben sind zwei Anweisungen; ein Abmelden
   dazwischen ließe ein offenes Geschwister in einer beendeten Sitzung zurück. Ein Angreifer mit dem alten Token kann das
   Fenster durch wiederholtes Einlösen gezielt offen halten, also ist das kein theoretischer Fall. Darum zwei Hälften, die
   nur zusammen wirken:
   - **Grace-Zweig: schreiben, dann nachprüfen.** Das Geschwister wird geschrieben (Commit, wenn der Store die Transaktion
     selbst führt), danach wird geprüft, ob die Sitzung **außer diesem Geschwister** noch ein offenes Token hat. Wenn nicht,
     wird das Geschwister bedingt geschlossen und das Ergebnis ist `Revoked`.
   - **Schließen: wiederholen, bis nichts mehr offen ist.** `CloseSessionAsync` und `CloseAllSessionsAsync` führen das
     bedingte Update so lange aus, bis es null Zeilen trifft, mit einer festen Obergrenze an Durchläufen; die Rückgabe ist
     die Summe. Eine Anweisung sieht nur Zeilen, die vor ihrem Start bestätigt waren; ein Geschwister, das während des
     ersten Durchlaufs entsteht, trifft spätestens der nächste.
   Warum das zusammen reicht (READ COMMITTED, Store führt die Transaktion selbst): Sei *c* der Commit des Geschwisters und
   *r* die Nachprüfung, *r* > *c*. Sieht ein Durchlauf des Schließens das Geschwister, schließt er es. Sieht es keiner, lag
   jeder Durchlauf vor *c*; der erste hat dann vor *c* bestätigt, und die Nachprüfung nach *c* findet außer dem Geschwister
   nichts Offenes und schließt es selbst. InMemory braucht das nicht: Ein Lock umfasst Prüfen, Schreiben und Schließen.
   **Grenze, dokumentiert:** Läuft der Store in einer Transaktion des **Aufrufers** (`CurrentTransaction` gesetzt), sehen
   andere Verbindungen das Geschwister erst bei dessen Commit; das Fenster ist dann so lang wie diese Transaktion. Die
   Remarks von `IRefreshTokenStore` und `TokenSessionService` sagen das und raten, Refresh und Abmelden nicht in lange
   Transaktionen zu legen. Eine vollständige Lösung bräuchte SERIALIZABLE oder eine Sperrzeile je Sitzung, also eine
   neue Tabelle (verworfen, siehe unten).

4. **Wessen Sitzungen: nur die der angemeldeten Person (auf ADR-0007).** Die Person, deren Sitzungen ein Abmelden
   beendet, kommt aus `ICurrentPrincipal` (seit G-0018 nur aus angemeldeten Personen-Identitäten:
   `PrincipalFactory.cs:22` über `ServiceCallerIdentity.PersonsOf`, `ServiceCallerHandler.cs:46–47`), nie aus einem
   Claim irgendeiner Identität von `HttpContext.User`. Ein Dienst-Token nennt bewusst keine Person und trägt kein `sub`
   (ADR-0003, ADR-0002); dieses ADR folgert daraus, dass ihm keine Sitzung gehört. Mit einem Dienst-Token steht keine Person hinter der Anfrage, `ICurrentPrincipal.Require()` wirft
   (`ICurrentPrincipal.cs:41–45`), und es wird nichts beendet. Der Widerruf der
   Access-Tokens gilt **je Personen-Identität** wie in ADR-0007 Entscheidung 4: Die Middleware prüft jede angemeldete
   Personen-Identität einzeln, der Stichtag einer Person trifft jede ihrer Identitäten. In der Bibliothek ändert sich
   dafür nichts; die Regel steht in den Remarks von `SignOutEverywhereAsync` und `SignOutAsync` (die Sitzung kommt aus
   derselben angemeldeten Identität wie die Person) und im README.

5. **Demo: Abmelden beendet alles und sperrt sofort.**
   - `UserService`, `TodoService` und der Monolith führen `UseTokenRevocation()` in ihrer ausdrücklichen Kette, direkt
     nach `UseAuth()` und vor `UsePrincipal()` (`MiddlewarePipelineModule.cs:273–275`: „Place after UseAuth“). Die
     Standardkette selbst enthält den Schritt nicht (`ServiceCollectionExtensions.cs:471`); das ändert dieses Gate nicht.
   - `/auth/sign-out` und `/auth/sign-out-everywhere` tun dasselbe (Owner 27.10.): zuerst
     `ITokenSessionService.SignOutEverywhereAsync(person)`, dann
     `ITokenRevocationWriter.RevokeSubjectBeforeAsync(person, now, "signed out")`. Diese Reihenfolge, weil die umgekehrte
     bei einem Fehler im zweiten Schritt schlechter endet: Gesperrte Access-Tokens mit offener Refresh-Kette holen sich
     sofort ein neues, gültiges Token. Schlägt der Widerruf fehl, antwortet der Endpunkt mit einem Fehler; das Abmelden
     ist idempotent und kann wiederholt werden.
   - `session_id` wird dort nicht mehr aus `http.User` gelesen.
   - Der Stichtag wird auf die volle Sekunde aufgerundet (`TokenRevocationKeys.ToCutoffSeconds`), `iat` < Stichtag ist
     gesperrt. Eine neue Anmeldung in derselben Sekunde wie das Abmelden kann darum bis zur nächsten Sekunde abgelehnt
     werden. Das ist bestehendes Verhalten der Bibliothek, kein Teil dieses Gates; die Demo-Tests warten es nicht weg,
     sondern melden sich neu an, nachdem die Sekunde vorbei ist.
   - README: Widerruf wirkt dienstübergreifend in Staging und Production (Valkey) und im Monolithen; in Development
     bei den Microservices nur in dem Prozess, der ihn schreibt (In-Memory je Prozess), also nicht im TodoService.
   - `SignInTests.cs:146–165`: Das Access-Token ergibt nach dem Abmelden 401; der Refresh ergibt 401; eine zweite,
     parallel geöffnete Sitzung derselben Person ist ebenfalls beendet.

6. **Q-0071: Warnung beim stillen Fallback, kein Bruch.** Nimmt `KeyRingFactory.Build` den Weg über
   `JWT_SECRET`/`JwtSettings:Secret`, registriert `JwtModule` einen internen Marker und einen gehosteten Dienst, der beim
   Start **eine** Warnung schreibt (Kategorie `Noelia.Infrastructure.Security.Keys`). Der Text nennt nur die Form und den
   Weg: Der Ring stammt aus einem geteilten Geheimnis der Konfiguration, weil keine Schlüssel übergeben wurden; 8.0 startet
   so nicht mehr; ausdrücklich geht es mit `Issue(...)`/`VerifyOnly(...)` oder `SigningKey.FromSharedSecret(...)`. Kein
   Wert, keine Länge, kein Name einer Variablen mit Inhalt. Mit übergebenen Schlüsseln (auch einem ausdrücklichen
   `FromSharedSecret`) oder mit Authority gibt es keine Warnung. `noelia.jwt.key-separation`, `KeyRing` und gemischte
   Ringe bleiben unverändert. Der Marker ist intern; die öffentliche API wächst nicht.

7. **Keine neue öffentliche API, Minor 7.2.0.** Kein neuer Enum-Wert, keine neue Methode, keine neue Spalte. Für
   Konsumenten ändert sich Verhalten an drei Stellen, alle in die geschlossene Richtung, Text für `MIGRATION.md`:
   - Ein Refresh im Grace-Fenster einer beendeten Sitzung ergibt `Revoked` statt eines neuen Tokens.
   - Ein Refresh im Grace-Fenster nach der Obergrenze der Sitzung ergibt `SessionExpired` statt eines toten Tokens.
   - `CloseSessionAsync`/`CloseAllSessionsAsync` können mehr als ein Update ausführen; eigene `IRefreshTokenStore`
     brauchen dieselbe Semantik (Conformance-Suite), sonst bleibt NO-1 bei ihnen offen.
   Dazu die Warnung aus Entscheidung 6 und die Ankündigung für 8.0.

8. **Was dieses Gate nicht anfasst.** Die Bibliothek koppelt `SignOutEverywhereAsync` nicht an den Widerrufsspeicher
   (`TokenSessionService` bleibt ohne `ITokenRevocationWriter`); das wäre eine eigene Entscheidung über Abhängigkeiten
   und Fehlerverhalten eines öffentlichen Dienstes. Die Stellen aus #70 bleiben dort. Der Startabbruch für den stillen
   Fallback ist G-0019.

## Verworfen

- **Neue Spalte `SessionClosedAt` oder eine Sitzungstabelle.** Jede Anwendung müsste eine Migration ihrer Tabelle
  ausrollen; die Tabelle ist Datenformat-Oberfläche. Eine Sperrzeile je Sitzung würde zusätzlich das letzte Fenster aus
  Entscheidung 3 schließen; das rechtfertigt keine Migration in einem Minor.
- **Beim Abmelden `RevokedAt` der rotierten Vorgänger überschreiben.** `RevokedAt` ist der Beginn des Grace-Fensters;
  ein späterer Wert würde das Fenster **verlängern**, also das Gegenteil bewirken.
- **Beim Abmelden `SessionExpiresAt` aller Zeilen auf die Abmeldezeit setzen.** Würde die gespeicherte Obergrenze
  umdeuten und bräuchte dieselbe Behandlung des Augenblicks; der abgeleitete Zustand reicht.
- **`ReuseDetected` als Ergebnis.** Falscher Diebstahlsalarm im Log für einen zweiten Tab.
- **Neuer Wert `SessionEnded`.** Öffentliche API für eine Aussage, die `Revoked` schon trifft.
- **Gemischte Ringe sperren** (ursprüngliche DoD von Q-0026). Der dokumentierte Umzug Shared Secret → Schlüsselpaar braucht
  sie; die Prüfung meldet sie außerhalb von Development schon als Fail (Owner 21.11.).

## Prüfung (Plan)

Jeder Test ist vor dem Fix rot und danach grün; die Gegenprobe steht im PR.

- **Conformance (`RefreshTokenStoreConformance`, läuft für InMemory und EF/SQLite):**
  `RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded` (Boss; Ergebnis `Revoked`, kein offenes Token danach),
  `RefreshGrace_AfterSignOut_ReturnsSessionEnded`, `RefreshGrace_AfterTheCeiling_IsSessionExpired`, und die bestehenden
  Grace-Tests bleiben grün (zwei Tabs ohne Abmelden bekommen weiter ein Geschwister).
- **Der Augenblick (nur EF):** Ein `DbCommandInterceptor` führt beim INSERT des Geschwisters über eine zweite Verbindung
  `CloseAllSessionsAsync` aus. Erwartet: Ergebnis `Revoked`, danach kein offenes Token. Ein zweiter Test lässt das
  Geschwister zwischen zwei Durchläufen des Schließens entstehen und erwartet ebenfalls null offene Tokens.
- **Q-0071:** Host-Start mit stillem Fallback → genau eine Warnung, ohne Geheimniswert; mit `Issue(...)`, mit
  ausdrücklichem `FromSharedSecret` und mit Authority → keine.
- **Q-0017 (Demo-Tests):** Nach `/auth/sign-out` ergibt das alte Access-Token 401 an UserService und am Monolithen; der
  Refresh ergibt 401; eine zweite Sitzung derselben Person ist beendet. Für TodoService im Microservice-Aufbau dort, wo
  der Test einen geteilten Speicher hat; sonst hält der Test die Grenze aus Entscheidung 5 fest.
- **Q-0018:** Authority-Pfad mit lokalem JWKS-Stub (Backchannel im Test): falsches `alg`, unbekanntes `kid`, falscher
  `iss`, falsches `aud` → 401, richtiges Token → 200. Sitzungs-Widerruf (`RevokeSessionAsync`) über die echte Kette →
  401, andere Sitzung derselben Person → 200. `SignOutEverywhereAsync` über `TokenSessionService` lässt im Fenster kein
  Geschwister entstehen. JwtBearer → `UseTokenRevocation` → widerrufenes Token → 401 ist schon belegt
  (`ServiceCallerFullChainTests.A_revoked_person_token_is_refused`, G-0016).

## Folgen

- NO-1 ist für die mitgelieferten Stores geschlossen; WT Q-0008 kann auf 7.2.0 aufbauen.
- Eigene Stores von Konsumenten bleiben offen, bis sie die Conformance-Suite bestehen (`MIGRATION.md`).
- Das letzte Fenster bei einer Transaktion des Aufrufers bleibt, dokumentiert.
- Die Demo zeigt Widerruf im laufenden Stack; das Release-Gate sieht ihn.
- G-0019 entfernt in 8.0 den stillen Fallback.
