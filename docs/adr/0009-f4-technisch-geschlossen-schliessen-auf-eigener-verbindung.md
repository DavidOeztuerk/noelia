# ADR-0009: Abmelden und Wiedergebrauch in einer Transaktion der Anwendung (F4 technisch geschlossen)

**Status:** Entwurf, Fassung 1 (Design-Runde offen) · **Datum:** 2026-11-21 · **Gate:** G-0020 (Rank A) · **Quests:**
Q-0074 (dieses Design), Q-0075 (Umsetzung), Q-0076 (N5) · **Baut auf:** ADR-0008 (Sitzungsende und Widerruf je Person),
Befund `docs/befunde/postgresql-beweis-adr-0008.md` · **Ziel:** Minor 7.2.0, **keine neue öffentliche API**.

## Owner-Entscheide (wörtlich)

Wortlaut aus den Owner-Nachrichten dieser Sitzung (21.11.2026), im Repository sonst nicht festgehalten, wie bei den
Entscheiden in ADR-0008; Auszüge, nicht gekürzt innerhalb eines Zitats.

- **21.11.2026, vor dem Beweis:** „ich würde das gerne direkt technisch schließen!“
- **21.11.2026, nach dem Beweis:** „Entscheidung: F4 technisch schließen und N5 fixen, beides vor 7.2.0, auf diesem
  Branch. G-0020 wird Beweis + Fix. Die roten Tests bleiben unverändert und werden durch den Fix grün.“ Dazu: „Prüfe
  als Kandidaten die Markierung über eine eigene Verbindung oder das ganze Schließen außerhalb der Transaktion der
  Anwendung. Beide F4-Mechanismen (Deadlock, Generationen) müssen danach grün sein. Neue öffentliche API nur, wenn nötig,
  so klein wie möglich und im ADR begründet. Die Verhaltensänderung für Anwendungen mit eigener Transaktion kommt in
  MIGRATION.md.“
- **21.11.2026, Rückfrage „was tut der Store ohne Factory“:** „Weder 1 noch 2 so. Grundsatz: laut scheitern statt
  stilles Restrisiko, aber so: a) Prüfe zuerst den Weg ohne öffentliche Factory: Der Store wird nur über
  AddEntityFrameworkRefreshTokens<TContext>() registriert (EntityFrameworkRefreshTokenStore.cs:769–773). Ein interner
  Konstruktor mit IServiceScopeFactory gibt im eigenen Scope einen eigenen TContext mit eigener Verbindung. Ist
  IDbContextFactory<TContext> registriert, nimm sie. Ziel: Keine Anwendung muss etwas ändern, keine neue öffentliche
  API. b) Laut scheitern nur, wenn keine eigene Verbindung möglich ist (etwa dieselbe DbConnection im neuen Scope oder
  ein von Hand gebauter Store), und zwar nur beim Abmelden. c) Die Diebstahl-Erkennung darf nie ‚gar nicht schließen‘
  heißen. Geht keine eigene Verbindung, schließt sie wie heute in der Transaktion der Anwendung und protokolliert einen
  Fehler. d) Selbst-Deadlock: Die eigene Verbindung darf nicht auf Sperren warten, die die Transaktion der Anwendung
  selbst hält. Das gehört ins Design und bekommt einen Test (kein Hängen bis zum Timeout).“

## In einfacher Sprache

Meldet eine Anwendung ab, während sie eine eigene Datenbank-Transaktion offen hat, verschwindet die Markierung „diese
Sitzung ist beendet“ erst mit dem Commit der Anwendung. Läuft gleichzeitig ein Refresh mit dem alten Token, warten beide
Seiten aufeinander (Deadlock) oder jagen sich über viele Runden (Generationen). Der PostgreSQL-Beweis (G-0020) hat das in
beiden Fällen nachgestellt: Das Abmelden warf, die Transaktion wurde zurückgerollt, und die Sitzung blieb offen.

Die Lösung: Das Abmelden (und das Schließen bei erkanntem Diebstahl) läuft nicht mehr in der Transaktion der Anwendung,
sondern auf einer eigenen Verbindung, die der Store sich selbst holt. Dort gelten die Regeln, die ohne Transaktion der
Anwendung ohnehin schon halten: Die Markierung ist sofort sichtbar, ein abgebrochener Schritt wird wiederholt. Die
Anwendung muss nichts ändern. Folge: Ein Abmelden ist unumkehrbar, auch wenn die Anwendung ihre Transaktion danach
zurückrollt, und es wartet nicht mehr auf deren Sperren. Geht keine eigene Verbindung, scheitert das Abmelden laut; die
Diebstahl-Erkennung schließt wie bisher in der Transaktion und meldet einen Fehler im Protokoll.

## Der Beweis, auf dem das aufbaut

PostgreSQL 17, Testcontainers (Befund, Abschnitt „Befund“): `F4_Deadlock_CloseInTheCallersTransaction_LeavesNothingOpen`
(fünf Fälle) und `F4_Generations_CloseInTheCallersTransaction_LeavesNothingOpen` sind rot, ihre Kontrollen ohne Klammer
(`…ControlWithoutABracket…`) sind grün. Der einzige Unterschied zwischen rot und grün ist, dass das Schließen in der
Transaktion der Anwendung läuft. Das Design stützt sich darauf: Was die Kontrolle grün macht, ist genau das, was hier
für den Fall mit Klammer hergestellt wird.

## Frage

Wie wird das Schließen in einer Transaktion der Anwendung so sicher wie ohne, ohne neue öffentliche API und ohne dass
eine Anwendung etwas ändern muss?

## Optionen

- **A: Nur die Markierung auf eigener Verbindung schreiben.** Reicht nicht. Eine Grace-Anfrage, die vor der Markierung
  geprüft hat (sie hält das Kettenende und steht vor ihrem INSERT), schreibt ihr Geschwister trotzdem und wartet in der
  Kappung auf Zeilen, die der Durchlauf in der Transaktion der Anwendung hält. Der Zyklus bleibt, PostgreSQL bricht die
  Transaktion der Anwendung ab (sie wartet zuerst), und es gibt in einer Klammer keine Wiederholung: Das Abmelden wirft,
  und das Geschwister bleibt offen. Der rote Test bliebe rot.
- **B: Das ganze Schließen auf eigener Verbindung** (Markierung, Durchläufe, Zählung). Das ist der Fall der Kontrolle:
  Die Markierung ist sofort festgeschrieben, und ein Deadlock trifft eine einzelne Anweisung, die der Store wiederholt.
  **Gewählt.**
- **C: Die Verbindung der Anwendung klonen** (`DbConnection` über die Provider-Fabrik oder den Verbindungstext). Verworfen:
  Der Verbindungstext einer geöffneten Verbindung enthält das Kennwort oft nicht mehr (`Persist Security Info`), SQLite im
  Speicher und gemeinsame Verbindungen sind nicht klonbar, und es wäre ein providerspezifischer Weg im
  providerfreien Paket.
- **D: Öffentliche `IDbContextFactory<TContext>` im Konstruktor.** Verworfen auf Vorgabe des Owners: keine neue API, keine
  Änderung für Anwendungen. Eine registrierte Factory wird trotzdem benutzt, wenn es sie gibt (Entscheidung 2).

## Entscheidungen

1. **Wann der Store isoliert.** Nur wenn die Anwendung eine Transaktion offen hat (`CallerOwnsATransaction`: `BeginTransaction`
   oder umgebende `TransactionScope`) und der Weg ein Schließen ist: `CloseSessionAsync`, `CloseAllSessionsAsync` und der
   Wiedergebrauchszweig von `TryConsumeAsync` (ruft `CloseSessionAsync`). Rotation, Grace-Zweig, Anmelden, Kappung und
   Aufräumen bleiben in der Transaktion der Anwendung, wie ADR-0008 sie beschreibt. Ohne Transaktion der Anwendung ändert
   sich nichts.
2. **Woher die eigene Verbindung kommt, ohne neue öffentliche API.** `AddEntityFrameworkRefreshTokens<TContext>()` ist
   in `src` und `demo` der einzige Registrierungsweg (`EntityFrameworkRefreshTokenStore.cs:768–773`; Aufrufer:
   `EntityFrameworkNoeliaModule.cs:21`, `demo/…/UserService.Infrastructure/Registration.cs:23`). Von Hand gebaut wird der
   Store nur in Tests und in Anwendungen, die ihn ohne die Erweiterung verwenden. Er registriert künftig den Store über
   eine Fabrik-Lambda und einen **internen** Konstruktor `(TContext, IServiceScopeFactory)`; der öffentliche
   Konstruktor `(TContext)` bleibt unverändert. Für ein isoliertes Schließen öffnet der Store einen eigenen asynchronen
   Scope und nimmt daraus `IDbContextFactory<TContext>`, falls registriert (`CreateDbContextAsync`), sonst `TContext`. Die
   Abhängigkeiten stehen schon im Paket (`IServiceCollection` wird dort bereits benutzt). Den Logger holt der Store mit
   `context.GetService<ILoggerFactory>()` aus dem Kontext selbst, also auch für einen von Hand gebauten Store.
3. **„Eigene Verbindung möglich“ heißt:** ein Scope-Factory ist da, der Scope liefert einen Kontext, und dessen
   `Database.GetDbConnection()` ist **nicht dieselbe Instanz** wie die der Anwendung. Fehlt der Scope-Factory (von Hand
   gebauter Store) oder ist es dieselbe Verbindung (eine Anwendung, die ihren Kontext auf eine feste, geteilte
   `DbConnection` gesetzt hat), gibt es keine eigene Verbindung.
4. **Ablauf isoliert.** Innerhalb von `new TransactionScope(TransactionScopeOption.Suppress, AsyncFlowOption.Enabled)`
   (sonst träte die neue Verbindung in die umgebende Transaktion ein): ein **neuer** Store über dem eigenen Kontext, der
   dasselbe `CloseSessionAsync`/`CloseAllSessionsAsync` aufruft. Dort ist `CallerOwnsATransaction` falsch, also gelten
   Markierung zuerst, Wiederholung bei Deadlock und alle Beweise aus ADR-0008 unverändert. Als Netz, nicht als
   Mechanismus, bekommt der eigene Kontext ein `CommandTimeout` von 30 s.
5. **Fehlerverhalten (Owner b, c).**
   - **Abmelden** ohne eigene Verbindung, in einer Transaktion der Anwendung: `InvalidOperationException` mit einem Text,
     der den Weg nennt (über `AddEntityFrameworkRefreshTokens` registrieren, mit einem Kontext, der eine eigene Verbindung
     öffnet; oder ohne eigene Transaktion abmelden). Kein stilles Restrisiko. Scheitert das isolierte Schließen selbst
     (nach den Wiederholungen), wirft das Abmelden wie bisher.
   - **Wiedergebrauch (Diebstahl)** ohne eigene Verbindung, oder wenn das isolierte Schließen scheitert: Der Store
     schließt **in der Transaktion der Anwendung**, wie heute, und schreibt einen Fehler ins Protokoll (Ereignis mit
     Sitzungs-Id, nie Token oder Verbindungstext). „Gar nicht schließen“ gibt es nicht.
6. **Selbst-Deadlock (Owner d).** Eine zweite Verbindung sieht nicht festgeschriebene Zeilen der Transaktion der
   Anwendung nicht und wartet auf deren Sperren, während die Anwendung auf das Abmelden wartet: ein Hängen, das keine
   Datenbank erkennt. Zwei Fälle, zwei Antworten:
   - **Hat dieser Store in der laufenden Transaktion selbst geschrieben** (Anmelden, Rotation, Grace-Geschwister, Kappung;
     er merkt sich die Kennung der Transaktion: `Database.CurrentTransaction.TransactionId` bzw.
     `Transaction.Current.TransactionInformation.LocalIdentifier`), dann schließt er **in dieser Transaktion**, wie ADR-0008
     es beschreibt, und schreibt eine Warnung ins Protokoll. Nur dort sieht er seine eigenen offenen Zeilen und wartet auf
     keine fremde Verbindung. Das Restrisiko von F4 bleibt für genau diesen Fall (Rotation und Abmelden derselben Sitzung
     in einer Transaktion) bestehen und steht in MIGRATION.md.
   - **Schreibt die Anwendung selbst in die Tabelle** (an der Bibliothek vorbei): nicht erkennbar. Hier fängt das
     `CommandTimeout` (Entscheidung 4) das Hängen; das Abmelden scheitert laut, die Diebstahl-Erkennung fällt auf
     Entscheidung 5 zurück.
7. **Verhaltensänderung für Anwendungen mit eigener Transaktion** (MIGRATION.md, README): (a) Ein Abmelden oder Schließen
   bei Diebstahl wird **nicht** zurückgerollt, wenn die Anwendung ihre Transaktion zurückrollt; eine Audit-Zeile gehört
   danach geschrieben. (b) Es wartet nicht mehr auf Sperren der Anwendung und blockiert sie nicht. (c) Änderungen, die die
   Anwendung auf ihrem Kontext offen hat, schreibt das Abmelden nicht mehr mit (der Kontext des Stores war der
   der Anwendung; der eigene ist ein anderer). (d) Ein von Hand gebauter Store scheitert beim Abmelden in einer Transaktion
   laut (Entscheidung 5). Der README-Rat aus ADR-0008 (`README.md:927–928`: Refresh und Abmelden nicht in eine eigene Transaktion legen) entfällt,
   ausgenommen der Fall aus Entscheidung 6.
8. **SQL Server** bleibt ungeprüft; README und MIGRATION sagen es bis zu Issue #74. Dort ist auch zu prüfen, ob die eigene
   Verbindung auf Sperren der Anwendung wartet.

## Tests (Prüfung, Plan)

- **Die roten F4-Tests bleiben in ihren Erwartungen unverändert** (`open == 0` usw.) und werden grün. Ihr Aufbau ändert
  sich in genau einem Punkt: Der Client C bekommt seinen Store über den Registrierungsweg (`AddDbContext` mit denselben
  Optionen samt Interceptoren und `AddEntityFrameworkRefreshTokens`, aufgelöst in einem Scope), also so, wie eine
  Anwendung ihn bekommt. Ein von Hand gebauter Store ist ausdrücklich der Fall von Entscheidung 5 und hat einen eigenen
  Test. Die Kontrollen ohne Klammer bleiben unverändert.
- **Neu:** Abmelden mit von Hand gebautem Store in einer Transaktion wirft (und danach ist nichts verändert); derselbe
  Fall bei der Diebstahl-Erkennung schließt in der Transaktion und protokolliert einen Fehler; dieselbe `DbConnection`
  im neuen Scope gilt als „keine eigene Verbindung“; **Rotation und Abmelden derselben Sitzung in einer Transaktion**
  schließt auch das nicht festgeschriebene Token und hängt nicht (Frist im Test, kein Timeout-Warten); Abmelden wird nicht
  zurückgerollt, wenn die Anwendung zurückrollt; unter `TransactionScope` tritt die eigene Verbindung nicht bei;
  `CloseAllSessionsAsync` isoliert; offene Änderungen der Anwendung werden nicht mitgeschrieben; die Registrierung wirkt
  (Test auf die Wirkung, nicht auf den Rückgabewert der Erweiterung).
- **Bestehende Tests, deren Erwartung sich mit Absicht ändert**, werden einzeln im PR benannt und begründet; keiner wird
  gelockert oder übersprungen. Betroffen sind die Tests, die einen von Hand gebauten Store in einer Transaktion der
  Anwendung abmelden oder Wiedergebrauch erkennen lassen: `Close_InsideACallersTransaction_DoesNotRetry`
  (`RefreshTokenStoreMomentTests.cs:501`), `Close_InsideATransactionScope_DoesNotRetry` (`:531`) und, zu prüfen,
  `TransactionBracketTests` (Wiedergebrauch in der Klammer, `:121–142`). Für den von Hand gebauten Store gilt
  Entscheidung 5 (Abmelden wirft, Diebstahl schließt in der Transaktion wie heute); die Tests, die „keine Wiederholung in
  der Klammer“ belegen, behalten ihre Erwartung für den Wiedergebrauchszweig und den Fallback und bekommen für das
  Abmelden den neuen Wurf als Erwartung, jeweils mit Begründung im PR.

## Verworfen

- Eine Option am `TokenSessionService` oder eine öffentliche Methode wie `CloseSessionIsolatedAsync`: neue API ohne Not.
- Das Abmelden bei fehlender eigener Verbindung still in der Transaktion zu lassen: genau das stille Restrisiko, das der
  Owner ausgeschlossen hat (Entscheidung 5).

## Folgen

- `EntityFrameworkRefreshTokenStore` bekommt einen internen Konstruktor, Zustand je Instanz (Kennung der Transaktion, in
  der er geschrieben hat) und einen isolierten Weg für das Schließen; `AddEntityFrameworkRefreshTokens` registriert über
  eine Fabrik. Das Paket `Noelia.Data.EntityFrameworkCore` bekommt keine neue Paketabhängigkeit (Budget-Test prüft es).
- ADR-0008, Entscheidung 3, Absatz „Grenze: Schließen in einer Transaktion des Aufrufers“ wird durch dieses ADR
  ersetzt; der Nachtrag „auf PostgreSQL geprüft“ folgt nach grünem Lauf (Gate-Abschluss).
- N5 (Kappung) ist eine eigene Quest (Q-0076) ohne Design: `CapAsync` schließt nur Zeilen mit
  `RevokedAt == null && ReplacedBy == null`.
- Folge-Issue #74: SQL Server-Beweis nach 7.2.0.
