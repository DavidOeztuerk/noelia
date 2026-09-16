# Plan: Noelia wird maschinell auskunftsfähig (6.0.0)

Dieses Dokument beschreibt, was in Noelia entstehen muss, damit eine **Control
Plane** in einem eigenen Repository gebaut werden kann. Die Control Plane selbst
ist nicht Teil dieses Plans und nicht Teil dieser Bibliothek.

## Warum überhaupt

Auf der Übersichtsseite der Demo steht der Satz:

> A microservice stack has no single dashboard. Each service composes its own,
> reports its own modules and answers for its own readiness.

Das ist richtig und es ist die Produktlücke. Ein Noelia-Dienst ist heute
**auskunftsfähig gegenüber einem Menschen**: Er rendert eine Seite, ein Mensch
liest sie, und was dort steht, gilt für diesen Prozess in diesem Augenblick.

Was fehlt, ist Auskunftsfähigkeit **gegenüber einem zweiten Programm**. Drei
Dinge kann ein einzelner Prozess prinzipiell nicht leisten, und alle drei sind
das, wofür ein Betrieb zahlt:

1. **Die Flotte.** Ob 3 von 17 Diensten nur lokal bremsen, sieht niemand, der
   17 Tabs vergleicht.
2. **Die Zeit.** Eine Prüfung, die vor fünf Tagen mit einem Deploy umgekippt
   ist, kann eine Seite nicht zeigen, die nur „jetzt" kennt.
3. **Den Nachweis.** Ein datiertes, signiertes Dokument über die
   Souveränitätsgrenze einer Anlage — das Ding, das man einem Prüfer hinlegt.

Der Verkaufsgrund dahinter ist nicht das Dashboard, sondern dass die Liste der
erreichbaren Ziele **vollständig** ist. Sie ist vollständig, weil jeder Dienst
seine ausgehenden Ziele deklarieren muss, bevor er startet, und ein
undeklarierter Aufruf scheitert. Kein anderes .NET-Fundament erzwingt das, also
kann keines diese Liste erzeugen — nur schätzen.

## Der Zuschnitt

| Teil | Inhalt | Art |
|---|---|---|
| 1 | `OperatorReport`: ein Modell hinter dem Dashboard, abrufbar als JSON | additiv |
| 2 | Die Prüfspur zurücklesen und verifizieren | additiv |
| 3 | Doppelte Typen zusammenführen | **brechend** |

Teil 1 und 2 sind das, was die Control Plane braucht. Teil 3 ist der Grund, dass
6.0.0 eine Hauptversion ist: Nach der Regel dieses Repos verlangt ein Major
einen Bruch, und Auskunftsfähigkeit allein bricht nichts.

---

## Teil 1 — `OperatorReport`

### Der Befund

`DashboardPage.RenderAsync` holt sich acht Dienste aus dem Container und
schreibt aus jedem direkt HTML in einen `StringBuilder`. **Zwischen den Daten
und der Darstellung liegt nichts.** Es gibt kein Modell, das man serialisieren
könnte.

Der naheliegende Fehler wäre, daneben einen zweiten Pfad zu bauen, der dieselben
Dienste abfragt und JSON schreibt. Dann gibt es zwei Codepfade für eine Aussage,
und sie driften — und zwar lautlos, weil niemand beide gleichzeitig ansieht. Das
ist exakt die Fehlerklasse, für die die 5.0-Linie existiert.

### Die Entscheidung

**Ein Modell, zwei Darstellungen.**

```
Dienste im Container
        │
        ▼
OperatorReportCollector      ← fragt einmal ab
        │
        ▼
   OperatorReport            ← reine Daten, serialisierbar
        │
   ┌────┴────┐
   ▼         ▼
 HTML      JSON
```

`DashboardPage` rendert künftig **aus dem Modell**, nicht mehr aus den Diensten.
Damit können HTML und JSON nicht auseinanderlaufen: Was die Seite zeigt und was
die Control Plane liest, ist dieselbe Erhebung.

Der erklärende Fließtext bleibt in der HTML-Darstellung. Er ist Präsentation,
keine Aussage über den Zustand, und hat in einem Datenmodell nichts verloren.

### Wo das Modell liegt

`Noelia.Abstractions/Operator/`. Reine Daten, kein ASP.NET, kein Anbieter —
damit hält es den `NoeliaProviderFree`-Wächter ein. Der Nutzen dieser Wahl: Wer
den Bericht auswerten will, deserialisiert ihn mit den offiziellen Typen, statt
sie abzuschreiben.

Der Sammler liegt in `Noelia.Dashboard`, weil er `HealthCheckService` und
`HttpContext` braucht.

### Der Endpunkt

`GET {DashboardPath}/report.json`

In derselben Middleware wie die Seite, hinter **derselben** Authentifizierung,
derselben Sichtbarkeitsregel und demselben Prüfspur-Eintrag. Ein Berichtspfad,
der leichter zugänglich wäre als die Seite, wäre eine Hintertür um die
Zugangsregel herum.

### Schema-Version

Der Bericht trägt `schemaVersion`. Die Control Plane liest Dienste
unterschiedlicher Noelia-Stände nebeneinander; ohne Versionsangabe muss sie
raten, und Raten über ein Format ist der Anfang stiller Fehlinterpretation.

### Flotten-Zugehörigkeit

Der Bericht nennt, zu welcher Anlage der Dienst gehört (`fleet`), aus der
Konfiguration. Ohne das kann die Control Plane 17 Dienste nicht zu einer Flotte
gruppieren, sondern nur zu einer Liste.

---

## Teil 2 — Die Prüfspur zurücklesen

### Der Befund

`ISovereignAuditSink` hat **eine** Methode: `WriteAsync`. Die Senke ist
schreibgeschützt. Es gibt in ganz Noelia keinen Weg, eine geschriebene Kette
zurückzulesen.

Das heißt: Die Kette ist hashverkettet, jeder Eintrag hängt am Hash seines
Vorgängers, eine Änderung bricht alle folgenden Hashes — und **niemand kann das
nachprüfen**. Die Zusage ist da, die Einlösung fehlt.

Für einen Souveränitätsnachweis ist das der wichtigste fehlende Baustein. „Die
Kette ist unversehrt" ist eine Behauptung, solange sie niemand nachrechnet.

### Die Entscheidung

Ein **eigener, optionaler Port** — nach dem Muster, mit dem 5.2.0
`IChainedSovereignAuditSink` eingeführt hat:

```csharp
public interface IReadableSovereignAuditSink : ISovereignAuditSink
{
    IAsyncEnumerable<StoredAuditEntry> ReadAsync(
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default);
}
```

Warum kein zusätzliches Mitglied auf `ISovereignAuditSink`: Das bräche jede
bestehende Implementierung, und eine Senke, die nur schreiben kann — ein
Append-only-Log, ein Fremdsystem, in das man hineinschreibt und nicht heraus —
ist eine legitime Senke. Wer zurücklesen kann, sagt es zusätzlich.

`IAsyncEnumerable`, nicht `IReadOnlyList`: Eine Prüfspur kann Millionen
Einträge haben. Eine Signatur, die das Zurücklesen nur als vollständige Liste
anbietet, zwingt jeden Prüfer, alles in den Speicher zu holen.

### Der Verifizierer

`IAuditChainVerifier` rechnet die Kette nach und antwortet mit einem Ergebnis,
das auch den **Bruch benennt** — nicht nur „ungültig". Ein Prüfbericht, der
sagt, dass etwas nicht stimmt, aber nicht wo, zwingt zur Handarbeit an genau der
Stelle, an der man eine Maschine wollte.

Das Ergebnis geht in den `OperatorReport` und damit in den Nachweis.

---

## Teil 3 — Was 6.0.0 zum Major macht

Die README-Roadmap nennt mehrere doppelte Typen. Zwei davon haben sich seit dem
Eintrag erledigt — `RateLimitResult` und `IDomainEvent` existieren nur noch
einmal. Der Eintrag wird entsprechend korrigiert. Echt sind noch:

- **`CacheStatistics`** in `Noelia.Abstractions.Caching` und in
  `Noelia.Infrastructure.Communication.Caching`.
- **Zwei Prüfspur-Systeme**: `ISecurityAuditLogger` mit eigenem
  `SecurityAuditEvent` neben `ISecurityAuditService`. Der `OperatorReport`
  müsste sonst beide führen und den Leser entscheiden lassen, welches die
  Wahrheit ist.

Beides wird zusammengeführt, das Verschwundene in `MIGRATION.md` angesagt.

---

## Reihenfolge

1. Modell in `Noelia.Abstractions.Operator`
2. Sammler in `Noelia.Dashboard`
3. `DashboardPage` rendert aus dem Modell — Gegenprobe: die Seite zeigt
   unverändert dasselbe
4. `/report.json` in der Middleware, hinter derselben Zugangsregel
5. `IReadableSovereignAuditSink` + Redis-Implementierung + Verifizierer
6. Zusammenführung der doppelten Typen
7. Demo verdrahten und im Browser gegenlesen

Nach jedem Schritt: Tests, die die **Wirkung** prüfen. Ein Test, der den
fluenten Rückgabewert festnagelt, prüft nichts — das steht so in `CLAUDE.md` und
es ist der Grund, dass 5.1.0 neun Funde hatte.

## Was ausdrücklich nicht hierher gehört

- **Das Einsammeln.** Die Control Plane holt ab; die Dienste schicken nichts.
  Würden sie melden, müsste jeder Dienst ein neues ausgehendes Ziel
  deklarieren — das Produkt verschlechterte die Bilanz, die es misst.
- **Das Gedächtnis.** Historie über Deploys hinweg ist Aufgabe der Control
  Plane. Ein Prozess, der seine eigene Vergangenheit aufbewahrt, beantwortet
  eine Frage, die er nicht beantworten kann.
- **Die Signatur des Nachweises.** Sie deckt die Aggregation über die Flotte ab,
  und die entsteht erst dort.
