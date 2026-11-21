# PostgreSQL-Beweis für ADR-0008

**Gate:** G-0020 (Issue #73, Nacharbeit G-0006) · **Art:** Beweis, kein Fix. Unter `src/` ändert sich in diesem Gate
nichts, die Version bleibt 7.1.1. · **Stand:** Design, Fassung 3 (Warden-Runde 1: 4 Major, 10 Minor eingearbeitet; Delta-Runde: approve), noch ohne Ergebnis.

## In einfacher Sprache

ADR-0008 sagt, dass „Abmelden beendet alle Sitzungen" auch bei vielen gleichzeitigen Anfragen hält. Die Begründung
stützt sich auf drei Eigenschaften der Datenbank (Sperren halten bis zum Commit, ein wartendes Update prüft seine
Bedingung neu, eine neue Anweisung sieht frühere Commits). Getestet ist das bisher nur auf SQLite, das Schreiber
nacheinander ausführt. Dieses Gate prüft es gegen eine echte PostgreSQL-Datenbank. Es soll zeigen, ob die Annahmen
stimmen und ob der Fall F4 (Abmelden in der Transaktion der Anwendung, dazu ein Refresh mit dem alten Token) wirklich
so ausgeht, wie das ADR es als Restrisiko beschreibt. Ist ein Test rot, wird nichts zurechtgebogen: Das Gate hält an,
und der Owner entscheidet über den Fix.

## 1. Testaufbau

**Paket und Container.** `Testcontainers.PostgreSql` (Version von `Testcontainers.Redis`, 4.7.0) in
`Directory.Packages.props` und im Testprojekt. `Npgsql.EntityFrameworkCore.PostgreSQL` ist dort schon gepinnt (10.0.3),
wird aber von keinem Projekt referenziert; die `PackageReference` kommt neu ins Testprojekt. Beide Verweise treffen nur
das Testprojekt; `PackageDependencyBudgetTests` zählt `src/` und bleibt unberührt. Ob `Testcontainers.PostgreSql` 4.7.0
weitere transitive Pins verlangt (wie `SSH.NET` bei Redis), zeigt erst die Wiederherstellung und die Paketprüfung; das
steht dann im Befund.

**Fixture.** `PostgresFixture` startet einen Container (`postgres:17-alpine`, `deadlock_timeout` je Verbindung gesetzt, siehe §2) je Testklasse, nach dem Muster von `RedisFixture`: Läuft keine Container-Runtime, **wirft** sie,
statt zu überspringen. Jeder Test bekommt eine eigene Datenbank (`CREATE DATABASE noelia_<guid>` aus der Vorlage
`template0`, dann `EnsureCreated`); so teilen Tests weder Zeilen noch Sperren.

**Was gegen PostgreSQL läuft.**

| Block | Quelle | Wie |
|---|---|---|
| Conformance | `RefreshTokenStoreConformance` (702 Zeilen) | `PostgresRefreshTokenStoreTests` erbt sie, wie `SqliteRefreshTokenStoreTests` |
| Augenblick-Tests | `SqliteRefreshTokenStoreMomentTests` (1171 Zeilen) | Die Klasse wird zur abstrakten Basis `RefreshTokenStoreMomentTests`; zwei Erben: SQLite (unverändert, gleiche Testnamen) und PostgreSQL |
| F4, N5, Index | neu | `PostgresRefreshTokenStoreProofTests`, nur PostgreSQL |

Die Basis fragt von der Datenbank nur ab, was sich unterscheidet: Verbindung öffnen, Optionen (`UseSqlite` bzw.
`UseNpgsql`), das Erkennen der No-op-Sperre am SQL-Text (PostgreSQL schreibt den Alias ohne Anführungszeichen), die
beiden Handeinfügungen (`RotateByHand`, `PlantMarkerByHand`: Parameternamen, Zufallsbytes, Zeitformat). Die abstrakte Basis trägt **kein** `[Trait]`
(xUnit vererbt es; sonst liefe der PostgreSQL-Erbe unter `Category=Unit` ohne Docker); die Erben tragen `Unit` bzw.
`Integration`. Jede Zeile der Tests bleibt, was sie ist; es wird nichts gelockert, Zahl und Namen der SQLite-Tests bleiben gleich (wird nachgezählt).
Wo ein SQLite-Test nur die Reihenfolge belegt, weil der Provider keine Transaktion der Umgebung aufnehmen kann
(Kommentar bei den `TransactionScope`-Tests, Moment-Tests Z. 525–529), prüft der PostgreSQL-Erbe zusätzlich, dass Arbeit mit der Klammer festschreibt und
zurückrollt; das ist der Teil, den der Kommentar dem PostgreSQL-Gate zugewiesen hat.

## 2. Die F4-Tests

**Was bewiesen werden soll.** Ein Abmelden oder die Diebstahl-Erkennung eines Refresh läuft in der Transaktion der
Anwendung. Die Markierung ist darin nicht festgeschrieben, andere Verbindungen sehen sie nicht. ADR-0008 nennt zwei
Mechanismen, die dann offen bleiben: **Deadlock** und **Generationen**. **Erwartet wird das sichere Ergebnis:** Nach dem
Abmelden ist keine Sitzung offen. Gemessen wird der festgeschriebene Stand über eine frische Verbindung, nachdem die
Klammer beendet ist, ob sie festgeschrieben hat oder nicht. Die Diebstahl-Erkennung und `CloseAllSessionsAsync` laufen
über dasselbe `CloseAsync` (Store Z. 441); der Deadlock-Fall deckt `CloseSessionAsync` und die Diebstahl-Erkennung ab,
ein Fall mit `CloseAllSessionsAsync` zeigt die Gleichwertigkeit.

**Gemeinsame Regeln.**
- Kein Schlafen, nur Zustände. Warten auf C: `pg_blocking_pids(pid_C) = {pid_G}` mit Frist (10 s), dazu von einer dritten
  Verbindung `SELECT … WHERE "Id" = R FOR UPDATE SKIP LOCKED`: keine Zeile heißt, C hält R.
- Hilfsverbindungen (Abfragen, `pg_backend_pid`) mit `Enlist=false`; `pg_backend_pid` von C wird innerhalb der Klammer
  gelesen. G startet außerhalb des Scope-Flusses (`ExecutionContext.SuppressFlow`), sonst tritt sie über
  `Transaction.Current` der Transaktion von C bei.
- **Das Opfer wird festgelegt, nicht erhofft** (Warden, Major 1): `deadlock_timeout` je Verbindung über
  `Options=-c deadlock_timeout=…`: C 2 s, G 60 s. PostgreSQL prüft je Wartendem einmal nach dessen Frist, also bricht es C
  ab, sofern G innerhalb von 2 s nach dem Wartebeginn von C selbst zu warten beginnt (sonst wird G nach 60 s das Opfer,
  Aufbaufehler). Darum wird G sofort nach dem erreichten Zustand freigegeben und in Abständen von höchstens 50 ms
  abgefragt. Vorbedingung des Tests: C scheitert mit SQLSTATE 40P01 und G nicht. „Opfer G" ist
  ein Aufbaufehler, nie grün.
- Der Zähler `pg_stat_database.deadlocks` steigt verzögert (nach dem Rückgeben der Verbindung). Abgefragt wird in
  Autocommit mit Frist (10 s) bis `>= vorher + 1`, dann genau `+ 1`. Erster Beleg ist das abgefangene 40P01 auf C.
- Eigene Datenbank je Test, daher gehört der Zähler dem Test.

**Fall Deadlock** (Aufbau, jeder Schritt ein Zustand):
1. Sitzung S mit R (älteres offenes Geschwister, zuerst eingefügt) und der Kette T0 → T1 → E. T0 wurde vor mehr als der
   Grace-Zeit rotiert (Wiedervorlage = Diebstahl), T1 eben (G lebt davon). `IssuedAt` R < E ≤ jetzt(G). Vorbedingung:
   `ORDER BY ctid` zeigt R vor E, sonst Abbruch mit Meldung (Aufbaufehler).
2. **G** (Grace-Anfrage mit T1, eigene Verbindung, Obergrenze 1) hält **am INSERT ihres Geschwisters** an (Interceptor
   `IsInsert`, wie `SignOut_BetweenLockAndInsert_LeavesNothingOpen`). Damit hält G E (No-op-Sperre) und hat die
   Markierung schon geprüft (nicht gefunden). Das gilt in allen Läufen, auch in der Kontrolle (Warden, Major 2).
3. **C** (Klammer) ruft `CloseSessionAsync(S)` bzw. präsentiert T0 (Diebstahl). Der Pass sperrt R und blockiert auf E.
4. G wird freigegeben: Geschwister X, Kappung (Obergrenze 1) wählt {R, E} und wartet auf R, das C hält. Zyklus; nach 2 s
   bricht PostgreSQL C ab.
5. Vorhersage bei C als Opfer: C wirft 40P01, die Klammer rollt zurück, G schließt R und E und schreibt X fest;
   **X bleibt offen**: Ergebnis rot mit genau einer offenen Zeile. Das ist der Befund, falls er so eintritt. Gleiches
   gilt für `TransactionScope`.
6. Auswertung: Anzahl offener Zeilen von S über eine frische Verbindung muss 0 sein. Ausgegeben werden: ob das
   Abmelden warf (Typ, SQLSTATE), Ergebnis von G, Zeilen von S.

Vier Fälle (Theory): {Abmelden, Diebstahl-Erkennung} × {`BeginTransaction`, `TransactionScope`}, dazu ein Fall
`CloseAllSessionsAsync` mit `BeginTransaction`.
**Kontrolle (muss grün sein):** derselbe Aufbau **ohne** Klammer. Die Markierung ist dann vor Durchlauf 1 festgeschrieben,
G hält aber schon am INSERT, also entsteht derselbe Zyklus; C scheitert mit 40P01 (Frist 2 s), der Store wiederholt den
Pass (Interceptor zählt mindestens zwei Durchlauf-Updates auf C), danach ist nichts offen. Der Zähler steigt um 1.
Ohne diese Kontrolle ließe sich ein Aufbau, der den Zyklus nie erreicht, nicht von einem Beweis unterscheiden.

**Fall Generationen** (der zweite Mechanismus, ADR-0008 Z. 359–360): Ein Akteur A rotiert auf eigener Verbindung das
jüngste offene Token O_k (Claim-UPDATE, Nachfolger O_{k+1}, nicht festgeschrieben) und hält. C (Klammer) blockiert in
Durchlauf k auf O_k (`pg_blocking_pids`). A schreibt fest. Der Durchlauf überspringt O_k, die Zählung sieht O_{k+1}.
Vor Durchlauf k+1 hält ein Interceptor C an (vor dem Pass-UPDATE), bis A O_{k+1} gesperrt hat. So treibt A das
Schließen deterministisch Durchlauf um Durchlauf. Erwartet (ADR): In der Klammer wirft das Schließen nach 32
Durchläufen (`MaxClosePasses`), die Sitzung kann offen sein. Kontrolle: ohne Klammer sieht A die Markierung
(Store Z. 196), und das Schließen endet nach höchstens zwei Durchläufen.
Scheitert die Stufe an der Komplexität, geht sie mit Begründung an den Owner zurück; sie wird dann im Befund als nicht
geprüft geführt, und der ADR-Nachtrag sagt nicht „F4 geprüft".

**Markierungskonflikt** (ADR-0008 Z. 296–297, dem F4-Test zugewiesen): zwei Verbindungen melden dieselbe Sitzung ab;
das zweite wartet auf den nicht festgeschriebenen Primärschlüssel. Unter `BeginTransaction` fängt der Savepoint den
23505 ab (die Klammer bleibt brauchbar), unter `TransactionScope` bricht der Konflikt die Transaktion ab (25P02 danach,
laut). Beides ist ein Test.

**Last-Test:** entfällt. Eine Flut von Refresh-Anfragen zeigt nur eine Wahrscheinlichkeit; die deterministischen Fälle
zeigen den Mechanismus. Bei Bedarf Folge-Issue.

## 3. N5 und der Index

**N5.** G rotiert, ihre Kappung liest die offenen Zeilen und hält an (Interceptor nach dem Lesen). Eine zweite
Verbindung rotiert eine überzählige Zeile von Hand und schreibt fest (Obergrenze so groß, dass diese Rotation keine
Zeile kappt, die G hält; sonst hinge der Test). Die Kappungszeit liegt **später** als die Rotationszeit, sonst ist das
Überschreiben unsichtbar. Erwartet: `RevokedAt` dieser Zeile bleibt die Rotationszeit; zusätzlich kommt zwischen
t_rot + Grace und t_kappung + Grace noch `RotatedWithinGrace` statt `ReuseDetected`. Der Test ist laut #73 Punkt 2 rot,
bis `CapAsync` die Bedingung `RevokedAt == null && ReplacedBy == null` bekommt. **Das ist ein Fix unter `src/` und
gehört nicht in dieses Gate:** ist der Test rot, hält das Gate an.

**Index `(SubjectId, RevokedAt)`: ehrliche Abgrenzung** (Warden, Major 3). Der wartende Durchlauf von
`CloseAsync` filtert über `SessionId`, nicht über diesen Index; der Index liegt auf Schritt 1 von
`CloseAllSessionsAsync` (ein SELECT, das unter MVCC nie wartet). ADR-0008 Z. 393–395 nennt `(SubjectId, RevokedAt)` als Zugriffspfad des Schließens; das trifft für Schritt 1 von
`CloseAllSessionsAsync` zu, nicht für den wartenden Durchlauf in `CloseAsync` (Store Z. 464, `SessionId`). Auf
PostgreSQL ist die Wanderung einer Zeile im Index darum **nicht prüfbar**, und der Befund sagt es: offen für SQL
Server. Was bleibt und wirklich geprüft wird, heißt nach dem, was es zeigt:
`CloseAll_AcrossACommittedRotation_UsesTheSessionIndex_LeavesNothingOpen` (Planer über
`ALTER DATABASE … SET enable_seqscan = off`; `EXPLAIN` auf das SQL, das EF tatsächlich sendet, belegt den Index).

## 4. CI

`ci.yml` führt `dotnet test Noelia.slnx` ohne Filter aus, auf `ubuntu-latest`, wo ein Docker-Daemon läuft; so laufen
die Redis-Integrationstests heute, und die PostgreSQL-Tests laufen dort mit, ohne Änderung an der Pipeline. Lokal:
`dotnet test tests/Noelia.Infrastructure.Tests --filter "Category=Integration"` mit laufendem Docker; ohne Docker
schlagen sie fehl, nicht still durch. Die Laufzeit steigt um den Containerstart (einige Sekunden je Klasse) und etwa
mindestens 2 s je Deadlock-Fall (`deadlock_timeout` von C); ein Lauf mit Opfer G kostet 60 s und ist ein Aufbaufehler.

## Fassung 3: Umsetzungsregeln aus der Delta-Runde (approve)

- **Kontrolle:** Die Backend-Id von C kommt aus dem Interceptor an der Verbindung des Pass-UPDATE
  (`NpgsqlConnection.ProcessID`), denn ohne Klammer gibt es keinen Ort „innerhalb der Klammer". Das 40P01 auf C hält
  `CommandFailed` fest, weil der Store es abfängt.
- **Generationen:** (i) Erwartet ist 0 offen; die Vorhersage ist Rot (das Schließen wirft nach 32 Durchläufen, O_32
  bleibt offen). (ii) Vorbedingung: S hat genau eine offene Zeile, sonst kappt A eine Zeile, die C schon geschlossen hat,
  und der Test hängt. (iii) In der Klammer ist „A antwortet Revoked" ein Aufbaufehler: vor der ersten Rotation von außen
  wird geprüft (`Enlist=false`), dass die Markierung unsichtbar ist. Jedes Warten hat eine Frist.
- **Index-Test:** `ALTER DATABASE … SET enable_seqscan = off` wirkt nur auf neue Sitzungen. Es wird direkt nach
  `CREATE DATABASE` gesetzt, vor `EnsureCreated`; `current_setting('enable_seqscan')` auf der Verbindung des Tests
  belegt es.

## 5. Was offen bleibt (SQL Server und Weiteres)

- SQL Server (mit und ohne `READ_COMMITTED_SNAPSHOT`): alle Annahmen (a) bis (c) aus ADR-0008, der zweite Zyklus
  („Grace-Anfrage hält E und wartet auf das unbestätigte INSERT der Markierung") und die Wanderung im Index.
- MySQL/InnoDB: nur als Grenze im ADR beschrieben, nicht geprüft.
- REPEATABLE READ und SERIALIZABLE unter PostgreSQL (ADR: „erwarten ein lautes Scheitern"): nicht Teil dieses Gates.
- Last-Test (siehe oben).
- Index-Wanderung unter sperrendem READ COMMITTED (siehe §3): nur auf SQL Server prüfbar.
