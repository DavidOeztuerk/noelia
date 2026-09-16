# Noelia 5.3.0 → 6.0.0

**Noelia wird maschinell auskunftsfähig.** Bisher konnte ein Dienst einem
*Menschen* Auskunft geben: Er rendert eine Seite, jemand liest sie, und was
dort steht, gilt für diesen Prozess in diesem Augenblick. Einem zweiten
Programm — einer Flottenansicht, einem Prüfer, einem nächtlichen Lauf — konnte
er nichts sagen.

## Ein Modell hinter dem Dashboard

Neu: `OperatorReport` in `Noelia.Abstractions.Operator`, abrufbar unter
`GET {Dashboard-Pfad}/report.json`.

`DashboardPage` schrieb HTML direkt aus den registrierten Diensten. Zwischen
Daten und Darstellung lag nichts, also hätte ein zweiter Leser dieselben
Dienste über einen zweiten Codepfad abfragen müssen — und zwei Pfade für eine
Aussage driften lautlos auseinander.

Jetzt erhebt ein Sammler den Bericht einmal; die Seite rendert ihn, der
Endpunkt gibt ihn heraus. Beides in derselben Middleware, hinter derselben
Authentifizierung, derselben Sichtbarkeitsregel und demselben
Prüfspur-Eintrag.

Jeder Abschnitt trägt einen von vier Zuständen statt einer leeren Liste:
`Absent`, `Unavailable`, `Faulted`, `Present`. Ein Dienst ohne Bremse und ein
Dienst, dessen Bremse nicht mehr antwortet, sahen bisher gleich aus.

**Was du tun musst:** nichts. Die Seite sieht aus wie vorher. Wer den Bericht
nutzen will, setzt `Dashboard:Fleet`, damit ein Sammler mehrere Dienste einer
Anlage zuordnen kann — Noelia erfindet keinen Namen.

## Die Prüfspur lässt sich nachrechnen

Neu: `IReadableSovereignAuditSink`, `IAuditChainVerifier` und
`GET {Dashboard-Pfad}/audit-chain.json`.

`ISovereignAuditSink` hatte genau eine Methode: `WriteAsync`. Die Spur war
hashverkettet, eine Änderung brach alle folgenden Hashes — und nichts in
Noelia konnte das prüfen. „Die Kette ist unversehrt" war eine Behauptung,
solange sie niemand nachrechnete.

Der Prüfer fängt zwei verschiedene Eingriffe: der eigene Hash eines Eintrags
fängt einen überschriebenen Datensatz, der Vergleich mit dem Vorgänger fängt
einen entfernten, eingefügten oder verschobenen — wo jeder Datensatz für sich
stimmt und nur die Reihenfolge lügt. Das Ergebnis benennt die Stelle.

Verifikation ist ein eigener Pfad und läuft **nicht** bei jedem Seitenaufruf:
Eine Kette aus Millionen Einträgen bei jedem Aufruf nachzurechnen machte das
Öffnen des Dashboards zum Angriff auf den Speicher, über den es berichtet.
`OperatorReport.Audit.CanBeVerified` sagt, ob Fragen sich lohnt.

**Was du tun musst:** nichts, wenn deine Senke nur schreibt — dann meldet der
Prüfer `IsSupported = false` und sagt, was ihn möglich machte. `Noelia.Redis`
und die In-Memory-Senke können ab 6.0.0 zurücklesen.

### Speicherformat: der Redis-Index trägt eine Folgenummer

Der sortierte Satz `{prefix}:audit:index` war nach Zeitstempel sortiert. Die
Reihenfolge einer Kette ist aber die der Anhänge, die das Compare-and-Set
entscheidet, nicht die der Uhren: Zwei Repliken können in derselben
Millisekunde anhängen, und ein Prüfer bekäme die beiden in beliebiger
Reihenfolge — falscher Alarm an einer Kette, die niemand angefasst hat.

**Keine Migration nötig.** Die Folgenummer setzt am vorhandenen Höchstwert an,
nicht bei null. Eine vor 6.0.0 nach Zeitstempeln indizierte Kette behält ihre
Reihenfolge, und der nächste Eintrag landet darüber.

## Brechend: `CacheStatistics` gab es zweimal

`Noelia.Abstractions.Caching.CacheStatistics` und
`Noelia.Infrastructure.Communication.Caching.CacheStatistics` — gleicher Name,
verschiedene Felder, und **verschiedene Maßstäbe**: das eine meldete `HitRatio`
als Bruch (0…1), das andere `HitRate` als Prozent (0…100).

Die Variante aus `Noelia.Abstractions` überlebt, weil dort der Port liegt.

**Was du tun musst,** wenn du `IServiceResponseCache.GetStatistics()` liest:

| vorher | jetzt |
|---|---|
| `TotalRequests` | `Hits + Misses` |
| `CacheHits` | `Hits` |
| `CacheMisses` | `Misses` |
| `CacheEvictions` | `Evictions` |
| `LastReset` | `LastUpdated` |
| `HitRate` — **0…100** | `HitRatio` — **0…1** |

Die letzte Zeile ist die gefährliche. Ein Schwellwert, der bei `> 80` Alarm
schlug, schlägt jetzt nie wieder Alarm. Der Compiler fängt den Feldnamen; den
Maßstab fängt er nicht.

## Brechend: zwei Prüfspur-Systeme, eines bleibt

`Noelia.Infrastructure.Security` führte ein eigenes `SecurityAuditEvent` und
`SecurityEventSeverity` neben den gleichnamigen Typen in
`Noelia.Abstractions.Security.Audit`. **Die Schweregrade stimmten nicht
überein:**

| Wert | alte Infrastructure-Skala | überlebende Skala |
|---|---|---|
| 0 | Information | Information |
| 1 | Warning | Low |
| 2 | Error | Medium |
| 3 | **Critical** | High |
| 4 | — | **Critical** |

Ein auf einer Skala geschriebener und auf der anderen gelesener Wert wechselte
lautlos die Bedeutung. Die Typen aus `Noelia.Abstractions` überleben.

**Was du tun musst:** `SecurityEventSeverity.Warning` → `Low` oder `Medium`,
`SecurityEventSeverity.Error` → `High`. Wer die Zahlenwerte gespeichert hat,
muss `3` als `Critical` gelesene Einträge auf `4` heben.

### Und: `ISecurityAuditLogger` hat nie gespeichert

Der Fund dahinter ist der eigentliche Grund für die Zusammenführung.
`SecurityAuditLogger` schrieb ein Ereignis in den Logstrom und hörte dort auf —
mit einem Kommentar im Code, dass eine *richtige* Implementierung es auch
speichern würde. `Audit` ist in `UseDefaults()`, also betraf das **jeden**
Noelia-Dienst.

Die Lesehälfte war schlimmer: `GetSecurityEventsAsync` gab bedingungslos eine
leere Folge zurück. Wer fragte, welche Sicherheitsereignisse aufgetreten sind,
bekam „keine" — was wie eine Antwort klingt und keine war. Ein Test nagelte das
sogar als Sollverhalten fest.

Jetzt schreibt der Logger immer in den Logstrom **und** an einen registrierten
`ISecurityAuditService`, wenn es einen gibt. Das Lesen ohne Speicher wirft und
nennt die Pakete, die einen brächten, statt Leere zu melden.

**Was du tun musst:** nichts zum Schreiben. Wenn du
`GetSecurityEventsAsync` aufrufst, registriere einen `ISecurityAuditService`
(`AddRedisSecurity(...)` oder `AddInMemorySecurity()`) — vorher bekamst du dort
ohnehin nie etwas.

---

# Noelia 5.2.0 → 5.3.0

## Transactional Outbox

Neu: `IOutbox`, `IOutboxReader` und `OutboxMessage` in `Noelia.Abstractions`,
`AddEntityFrameworkOutbox<TContext>()` in `Noelia.Data.EntityFrameworkCore`,
`UseOutboxDispatcher()` in `Noelia.Infrastructure`.

`IEventBus` sagt seit jeher ausdrücklich, dass er **keinen** Outbox garantiert:
Zwischen dem Commit einer Änderung und dem Veröffentlichen des Ereignisses
darüber liegt eine Lücke, und ein Prozess, der darin stirbt, hinterlässt ein
System, in dem die Änderung geschah und niemand davon erfuhr. Es andersherum zu
versuchen verschiebt die Lücke nur: Dann kann die Veröffentlichung gelingen und
der Commit scheitern, und Zuhörer erfahren von etwas, das nie passiert ist.

`RecordAsync` schreibt die Absicht in **dieselbe** Transaktion und ruft
absichtlich kein `SaveChangesAsync`. Das Speichern des Aufrufers entscheidet, ob
beides passiert ist — oder keines.

```csharp
context.Jobs.Add(job);
await outbox.RecordAsync(new JobFinished(job.Id));
await context.SaveChangesAsync();          // beides, oder nichts
```

Zustellung ist **at-least-once**, und der Dispatcher tut nicht so, als wäre sie
etwas anderes: Er veröffentlicht zuerst und markiert danach, weil ein Absturz
dazwischen die Nachricht zweimal zustellt — die Alternative verliert sie.
`OutboxMessage.Id` reist deshalb mit.

Das Zustellen ist ein eigener Prozess mit eigenem Takt: `UseOutboxDispatcher()`
richtet ihn ein. Ein Dienst im Verbund braucht ihn, nicht jeder. Zwei
Dispatcher an einem Speicher sind sicher — das Beanspruchen ist ein bedingtes
Update —, aber unnötig.

**Was du tun musst:** `MapNoeliaOutbox()` in `OnModelCreating` aufrufen und eine
Migration erzeugen. Eine Tabelle, die niemand anlegt, ist eine Nachricht, die
niemand aufschreibt.

## Fehlermeldungen sind nicht mehr deutsch

`ErrorMessageService` lieferte deutsche Sätze aus einem Paket, dessen API,
Dokumentation und README englisch sind. Jede Anwendung, die nicht für ein
deutschsprachiges Publikum geschrieben war, zeigte ihren Nutzern eine Sprache,
die sie nicht gewählt hatten — und konnte daran nichts ändern, ohne den ganzen
Dienst zu ersetzen.

Neu: `IErrorTextProvider`. Noelia entscheidet weiter die **Struktur** — welcher
Code existiert, ob er überhaupt gezeigt werden darf, welche Hilfeseite ihn
erklärt, welche Handlungen ihn lösen könnten. Den **Wortlaut** entscheidet die
Anwendung.

`GetSuggestedActions` liefert jetzt Schlüssel aus `ErrorActions`
(`check-input`, `contact-support`) statt Sätze. Ein Schlüssel ist etwas zum
Nachschlagen; ein Satz ist etwas zum Überschreiben.

**Was du tun musst:**

- Wenn dir Englisch recht ist: nichts.
- Wenn du deutsche Meldungen willst: registriere einen `IErrorTextProvider` —
  die bisherigen Texte stehen in der Versionsgeschichte dieser Datei.
- **Wenn du `GetSuggestedActions` direkt anzeigst, prüfe das.** Dort stehen
  jetzt Schlüssel. Ein angezeigter Schlüssel fällt sofort auf; ein Satz in einer
  ungewählten Sprache fällt nie auf, und genau darum geht es.

## Die Sitzungsübersicht konnte nie etwas anzeigen

`TokenSessionService` ist `AddScoped` registriert, und die Menge der
beobachteten Subjekte war ein Instanzfeld — bei jeder Anfrage ein neues, leeres
Wörterbuch. Das Dashboard meldete deshalb `0 active sessions observed by this
instance`, in jedem Einsatz, dauerhaft. Die Formulierung ließ die Leere wie eine
Antwort klingen statt wie einen Defekt, und genau das ist die Fehlerklasse, für
die die 5.0-Linie existiert: registriert, vorhanden, ohne Wirkung, und niemand
merkt es.

Die Menge liegt jetzt in `SessionObservations`, einem Singleton, das
`NoeliaModule.TokenSessions` mitregistriert. Über die Komposition ändert sich
für dich nichts.

**Brechend, wenn du `TokenSessionService` selbst baust** — in eigenen Tests
etwa. Der Konstruktor hat einen fünften Parameter:

```csharp
// vorher
new TokenSessionService(store, options, clock, logger);

// jetzt
new TokenSessionService(store, options, clock, logger, new SessionObservations());
```

Es gibt bewusst **keine** Überladung mit vier Parametern. Sie müsste sich ihre
eigene Beobachtungsmenge anlegen — also genau den Defekt wiederherstellen, den
diese Änderung behebt, und zwar lautlos. Ein `[Obsolete]`-Pfad, der weiter das
Falsche tut, ist schlechter als ein Compilerfehler, der eine Zeile kostet.

Wer den Dienst aus dem Container auflöst, ist nicht betroffen.

Eine Nebenwirkung, mit der zu rechnen ist: Dashboards, die hier immer `0`
zeigten, zeigen ab jetzt echte Zahlen. Das ist keine neue Last — die Menge hält
nur Subjekt-Bezeichner dieses Prozesses —, aber es ist eine Zahl, die vorher
niemand gesehen hat.

---

# Noelia 5.1.0 → 5.2.0

## Ein Port lag im Motor: `ISovereignAuditSink` ist umgezogen

`Noelia.Infrastructure.Audit.ISovereignAuditSink` → `Noelia.Abstractions.Audit.ISovereignAuditSink`.

Er war als Port gedacht und lag im Motor, wo ihn **kein Anbieterpaket umsetzen
konnte**: Anbieter hängen an `Noelia.Abstractions`, nicht an
`Noelia.Infrastructure`. Die README sagt seit 5.0 „Noelia.Abstractions | *Every
port*"; hier stimmte das nicht.

**Was du tun musst:** `using Noelia.Abstractions.Audit;` ergänzen. Der Typ ist
derselbe, der Namensraum nicht. Wer den Port nie selbst umgesetzt hat, merkt
nichts.

## Eine Prüfspur, die alle Repliken teilen

Neu: `IChainedSovereignAuditSink` und `UseRedisSovereignAudit()`.

`AuditTrailService` schrieb seine Kette aus einem Feld fort, das es selbst
hält. Für eine Replik richtig, für zwei still falsch: Beide beginnen bei ihrem
eigenen Kopf, und ein Prüfer findet hinterher einen Bruch auf einem System, an
dem niemand manipuliert hat.

Eine Senke, die den Kettenkopf **mitbesitzt**, löst das. Speichern und
Fortschreiben sind dabei **ein** Aufruf, weil sie keine zwei sein können:
Zuerst fortschreiben und ein Absturz hinterlässt einen Kopf, der auf nichts
zeigt; zuerst speichern und ein zweiter Schreiber hängt sich an denselben
Vorgänger. `RedisSovereignAuditSink` macht beides in einem Lua-Skript, mit
Compare-and-Set auf dem Kopf — dasselbe Muster, das die Security-Prüfspur in
diesem Paket seit 4.4.3 benutzt.

Transparenzprotokolle lösen es andersherum: Trillian lässt nie zwei Schreiber
an einen Merkle-Baum, sondern sequenziert je Baum aus einem einzigen Prozess.
Compare-and-Set erreicht dieselbe Zusage von der anderen Seite — die Schreiber
rennen, genau einer gewinnt, der Verlierer baut auf dem Kopf neu auf, der
gewonnen hat.

`noelia.audit.chain-scope` meldet jetzt `Pass`, wo die Senke den Kopf hält, und
bleibt `Warning`, wo die Kette im Prozess fortgeschrieben wird.

**Was du tun musst:** nichts. Ohne eine kettenführende Senke bleibt das
bisherige Verhalten. Mit `UseRedisSovereignAudit()` teilen sich alle Repliken
eine Kette — und die vorhandenen Ketten je Prozess sind dann Vorgeschichte,
die getrennt geprüft werden muss.

## Der Schlüsselring braucht keinen Verschlüsselungsdienst mehr

`UseDataProtection(applicationName)` verlangte `IDataEncryptionService`. In der
Praxis hieß das Redis — und jede Stufe ohne Redis konnte ihren Schlüsselring
**gar nicht** schützen und meldete auf Dauer `Fail`.

Jetzt verlangt es `IMasterKeyProvider`: AES-256-GCM unter einem je Element
abgeleiteten Schlüssel, `HKDF-SHA256(master, salt, "noelia.dataprotection.keyring.v1")`
mit frischem 16-Byte-Salz. Ableiten statt speichern heißt, dass jede Replik mit
dem Hauptschlüssel lesen kann, was eine andere geschrieben hat — geteilt wird
nur der Schlüssel, den der Betrieb ohnehin hält.

**Version, Salz und Vektor liegen im GCM-Tag**, als Associated Data. Das ist
der Befund 4.4.2 als Bauvorschrift: Steuerdaten außerhalb des Tags ließen einen
Angreifer mit Schreibzugriff ändern, wie authentifizierte Bytes gelesen werden,
während die Entschlüsselung weiter Integrität meldete. Vier Gegenproben ändern
je ein Byte von Version, Salz, Vektor und Tag und verlangen eine Ablehnung.

**Was du tun musst:** Wenn du `UseDataProtection` schon aufrufst, ersetze die
Anforderung `UseRedisEncryption()` durch `AddConfiguredMasterKey()` oder
`AddSecretStoreMasterKey()`. **Ein bestehender Schlüsselring aus 5.2.0-preview
ist nicht lesbar** — das Format hat sich geändert; lösche ihn, bevor du
umstellst.

## Das Dashboard trägt die richtige Korrelations-ID

Der Prüfspur-Eintrag zu einem Dashboard-Aufruf enthielt
`HttpContext.TraceIdentifier` — eine Anfrage-ID je Verbindung, lokal zu diesem
Prozess. Unter dem Namen `correlationId` korrelierte sie nichts: Wer einen
Eintrag in der Hand hielt, fand die zugehörige Anfrage im Protokoll keines
anderen Dienstes.

Jetzt gilt `CorrelationId.Current`, sonst die Kopfzeile `X-Correlation-ID` der
Anfrage, und erst zuletzt der Trace-Identifier. Die Kopfzeile wird direkt
gelesen, weil das Dashboard über einen `IStartupFilter` **vor** der Pipeline der
Anwendung hängt — also vor `UseCorrelationId()`.


Additiv. Nichts, was du aufrufst, ändert seine Form — aber zwei neue Prüfungen
können einen Bericht, der gestern grün war, heute rot machen. Das ist die
Absicht: Sie melden zwei Zustände, die vorher niemand gemeldet hat.

## Der Schlüsselring von ASP.NET liegt nicht mehr im Container

Neu: `UseDataProtection(applicationName)` in der Zusammensetzung.

ASP.NET schreibt seinen Data-Protection-Schlüsselring sonst in ein Verzeichnis
im Container und warnt bei jedem Start zweimal darüber. Beides stimmt und
keines ist harmlos: Was mit diesem Ring geschützt ist — ein
Authentifizierungs-Cookie, ein Antiforgery-Token, ein Rücksetz-Link — verifiziert
nicht mehr, sobald der Container ersetzt wird, und eine zweite Replik verifiziert
nie, was die erste ausgestellt hat. `DataProtectionSecretProvider` gab es, und
registriert hat ihn niemand.

Der Ring geht jetzt durch `IDistributedCacheService` und wird mit
`IDataEncryptionService` verschlüsselt — beides Noelia-Ports, also **kein neues
Paket**. Welcher Server das ist, entscheidet wie überall der Betrieb:

```csharp
noelia.UseRedisCache(serviceName)
      .UseRedisEncryption()
      .UseDataProtection(serviceName);
```

Der neue Check `noelia.dataprotection.key-ring` meldet `Fail`, solange der Ring
im Dateisystem des Containers liegt oder unverschlüsselt abgelegt wird.

## Die Prüfspur sagt, wie weit sie reicht

Neu: `noelia.audit.chain-scope`, und er meldet `Warning`, solange eine souveräne
Prüfspur läuft.

`AuditTrailService` schreibt seine Kette aus einem Feld fort, das es selbst
hält. Für eine Replik ist das richtig und für zwei still falsch: Beide beginnen
bei ihrem eigenen Kopf, und ein Prüfer findet hinterher eine gebrochene Kette
auf einem System, an dem niemand manipuliert hat.

**Eine zweite Senke allein behebt das nicht** — zwei verschränkte Ketten in
einem Speicher sind schlechter als zwei getrennte. Die Security-Prüfspur in
`Noelia.Redis` löst dasselbe Problem bereits mit Compare-and-Set auf einem
gemeinsamen Kopf; die souveräne bekommt das noch. Bis dahin steht im Dashboard,
welche der beiden du fährst.

## Ein Modul darf sagen, dass es nichts registriert

Neu: `NoeliaModuleContractBuilder.RegistersNothing(reason)`.

Acht eingebaute Module hatten keinen Vertrag, und das Dashboard schrieb für
jedes denselben Satz: *„Running, but its contract declares no requirement or
provided effect."* Für `PermissionEnforcement` war das die Wahrheit — es ist ein
Pipeline-Schritt und registriert nichts —, für die anderen sieben eine
Auslassung. Beides las sich gleich.

Sieben erklären jetzt, was sie liefern; eines erklärt, dass es nichts liefert,
und warum. Der Aufruf widerspricht `Provides<T>`: wer beides deklariert,
bekommt eine Ausnahme statt einer stillen Entscheidung.

**Was du tun musst:** nichts, es sei denn, du hast eigene Module. Dann lohnt der
Blick ins Dashboard — der Satz steht dort für jedes Modul ohne Vertrag.

---

# Noelia 5.0.0 → 5.1.0

**Neun Stellen behaupteten etwas, das nicht stattfand.** Sechs sind entfernt,
drei wirken jetzt. Der Anlass war eine Bestandsaufnahme am laufenden Demo-Stack:
drei Health-Endpunkte antworteten mit `200` und `"checks": []`, und die Suche
nach dem Grund fand dieselbe Sorte Fehler noch achtmal.

Es ist dieselbe Fehlerklasse wie in [MASTERPLAN-5.0.md](docs/MASTERPLAN-5.0.md)
§1: etwas ist *registriert* und deshalb *anwesend*, aber nicht *wirksam* — und
nichts im System merkt den Unterschied. 5.0 hat die Mechanik gebaut, die das
aufdeckt. 5.1 ist, was diese Mechanik zuerst gefunden hat, angewandt auf Noelia
selbst.

| Was | Was es behauptete | Was es tat |
|---|---|---|
| `AddDatabaseHealthCheck()` und zwei Geschwister | Prüfungen einzurichten | leerer Rumpf, gibt `this` zurück |
| deren Tests | diese Methoden abzudecken | nageln den Rückgabewert fest |
| `RabbitMQHealthCheck` | den Broker zu prüfen | verlangt ein `IConnection`, das niemand registriert |
| `RedisHealthCheck` | den Server zu prüfen | ruft `INFO`, außerhalb Development gesperrt |
| `RedisPerformanceHealthCheck` | Kennzahlen zu melden | dasselbe, plus Serverinterna im Rumpf von `/health` |
| `/health`-Antwort | den Fehler zu nennen | nennt den Endpunkt aus der Treiberausnahme |
| `TrustForwardedHeadersFrom` | dem Vermittler zu glauben | belegt Optionen, die niemand anwendet |
| `AddRedisCache` neben `UseDefaults()` | den verteilten Zähler zu setzen | wird vom eingebauten überschrieben |
| `InProduction(reason)` | den Grund festzuhalten | druckt seine Zeichenzahl |

## Was du tun musst

1. **Prüfe, ob `/health/ready` in deinen Diensten etwas prüft.** Ohne eine
   Registrierung mit dem Etikett `ready` antwortet der Endpunkt `200`, auch
   wenn Datenbank, Cache und Broker nicht erreichbar sind. Der neue Check
   `noelia.health.readiness-coverage` meldet das ab 5.1.0 als `Fail` im
   Dashboard und beim Start. Registriere die Prüfungen deiner Anbieter:
   `AddDatabase<TContext>(...)`, `AddRedisConnection(...)`,
   `UseMassTransitMessaging(...)`.
2. **Entferne Aufrufe von `AddDatabaseHealthCheck()`,
   `AddRabbitMqHealthCheck()` und `AddExternalApiHealthChecks()`.** Die drei
   Methoden auf `HealthCheckBuilder` sind gestrichen. Sie hatten einen
   auskommentierten Rumpf und gaben `this` zurück — ein Dienst konnte alle drei
   aufrufen und registrierte nichts. Sie lassen sich in
   `Noelia.Infrastructure` auch nicht füllen: Wer eine Datenbank oder einen
   Broker erreicht, nennt einen Treiber, und dieses Paket darf das nicht
   (ADR-0001). Die Prüfungen kommen mit ihrem Anbieter.
3. **`RabbitMQHealthCheck` ist gestrichen.** Die Klasse verlangte ein
   `IConnection` von RabbitMQ.Client, das MassTransit nicht registriert — sie
   war nicht auflösbar und wurde nirgends registriert. Die Erreichbarkeit des
   Brokers prüft MassTransits eigener Bus-Check, den
   `UseMassTransitMessaging(...)` mit dem Etikett `ready` einrichtet.
4. **`AddRedisConnection(...)` registriert jetzt zwei Health-Checks**
   (`redis` mit `ready`, `redis-performance`). Wenn dein
   Bereitschaftsendpunkt bisher `200` meldete, während Redis nicht erreichbar
   war, meldet er jetzt `503`. Das ist die Absicht: Ein Dienst, der ohne
   diesen Server nicht arbeiten kann, ist ohne ihn nicht bereit.
   `Noelia.Redis` wächst dadurch von 14 auf 15 wiederhergestellte Pakete.

## Weitergereichte Kopfzeilen werden jetzt angewandt

`TrustForwardedHeadersFrom(...)` hat die Optionen des Rahmenwerks belegt, und
**niemand hat sie angewandt**: Noelias Pipeline rief `UseForwardedHeaders()`
nie auf. Ein Dienst hinter einem Vermittler zählte deshalb weiterhin den
Vermittler — in jeder Bremse, in jedem Eintrag der Prüfspur und in jeder
Sicherheitsmeldung — und sah hinter einer TLS-Terminierung `http`, was ein
Refresh-Cookie ohne `Secure` ausgibt.

Ab 5.1.0 stellt `UseNoelia(...)` den Schritt als Erstes ein, **sofern**
`TrustForwardedHeadersFrom` mindestens einen Vermittler benannt hat. Wer nie
Vertrauen erklärt hat, merkt nichts.

**Was du tun musst:** Wenn deine Anwendung `app.UseForwardedHeaders()` selbst
aufruft *und* `TrustForwardedHeadersFrom` benutzt, entferne den eigenen Aufruf.
Zwei Durchläufe verbrauchen zwei Einträge aus `X-Forwarded-For`.

### Und für Dienste, die selbst weiterleiten

Neu: `AddForwardedOriginPropagation()` beziehungsweise der
`ForwardedOriginHandler` aus `Noelia.Http`. `UseForwardedHeaders` *verbraucht*
die Kopfzeilen — richtig so —, weshalb ein Gateway die Kette beendet: Der
Dienst dahinter sah wieder `http`. Ein Gateway, eine Fassade oder jede
Fächerverteilung reicht den Ursprung damit weiter. Ein Dienst, der nur
antwortet, braucht den Aufruf nicht.

## Die Health-Antwort nennt keinen Ausnahmetext mehr

`/health`, `/health/live` und `/health/ready` schrieben
`error: exception.Message` in den Rumpf. Der Endpunkt ist dort, wo er
veröffentlicht wird, nicht authentifiziert, und ein Treiberfehler nennt den
Endpunkt, mit dem er gesprochen hat — bei StackExchange.Redis samt Adresse.

Ab 5.1.0 steht dort `description`: das, was die Prüfung selbst gesagt hat. Die
Ausnahme wird protokolliert.

**Was du tun musst:** Wenn eine Überwachung das Feld `error` liest, stelle sie
auf `description` um.

## `Noelia.Redis` nimmt an der Zusammensetzung teil

Neu: `UseRedisCache(prefix)`, `UseRedisTokenRevocation(maxTokenLifetime)` und
`UseRedisSecurityAudit()` auf dem `NoeliaBuilder`.

Der Grund ist eine stille Falle: `AddRedisCache(prefix)` registriert sofort,
die eingebauten Module registrieren beim Bauen der Zusammensetzung — also
später. Wer `AddRedisCache` neben `UseDefaults()` aufrief, bekam deshalb den
prozessinternen Zähler, den `RateLimiting` mitbringt. Nichts schlug fehl,
nichts wurde protokolliert; die Zähler blieben im Prozess, und das ist eine
Grenze je Replik unter dem Namen einer gemeinsamen.

**Was du tun musst:** Stelle `AddRedisCache(...)` neben `AddNoelia` auf
`UseRedisCache(...)` **innerhalb** der Zusammensetzung um. Die `Add…`-Aufrufe
bleiben für Hosts, die von Hand zusammensetzen.

## `RedisPerformanceHealthCheck` ist gestrichen

Die Klasse las `INFO` und meldete Speicherverbrauch, Trefferquote und
Verbindungszahl. Drei Gründe, und jeder allein hätte gereicht: `INFO` ist ein
Admin-Kommando, das `AddRedisConnection` außerhalb von Development sperrt;
Serverkennzahlen gehören in die Telemetrie, wo man sie über die Zeit sieht,
nicht in eine Sonde, die mit ja oder nein antwortet; und sie landeten im Rumpf
von `/health`.

`RedisHealthCheck` bleibt, prüft aber nur noch, was eine Bereitschaftssonde
fragt: Schreiben, Zurücklesen, Vergleichen, Löschen. Auch er brauchte bis 5.1.0
`INFO` und meldete deshalb **jeden** erreichbaren Server außerhalb von
Development als ungesund — weshalb ihn nie jemand registriert hat.

## Verhaltensänderung in einer Nebenversion

Punkt 2 und 3 entfernen öffentliche API, Punkt 4 ändert eine Antwort. Nach der
Regel in der README wäre das eine Hauptversion. Es ist bewusst 5.1.0:

- Die drei Methoden **registrierten nachweislich nichts**. Kein Verhalten kann
  auf ihnen beruhen; es bricht eine Übersetzung, und die Behebung ist das
  Löschen der Zeile.
- `RabbitMQHealthCheck` **konnte nie ausgeführt werden**, weil seine
  Abhängigkeit in keinem Behälter stand.
- Punkt 4 ändert eine Antwort von einer falschen in eine richtige. Ein
  Bereitschaftsendpunkt, der über einem ausgefallenen Cache `200` meldet, ist
  kein Verhalten, das Bestandsschutz verdient.

Die Alternative wäre gewesen, die Falschaussagen bis 6.0 stehen zu lassen.

## `InProduction(reason)` erreicht jetzt einen Leser

Der Aufruf hat die Zusammensetzung schon in 5.0.0 abgebrochen, wenn in
`Production` kein Grund angegeben war — das war nie kaputt. Festgehalten wurde
der Grund aber nirgends: Die Seite druckte
`"Production exposure reason: recorded (37 characters)."` und nie den Text.

Ab 5.1.0 steht der Grund im Wortlaut auf der Seite und im Ergebnis von
`noelia.dashboard.operator-access`. **Prüfe deine Begründungen**, bevor du
umstellst: Sie sind ab jetzt für Betriebspersonal sichtbar und gehören
entsprechend formuliert — kein Hostname, kein Ticketinhalt, kein Geheimnis.

---

# 4.4.3 code line → Noelia 5.0.0

This is a complete identity change, not only a package rename. Every package id,
namespace, public API name, configuration prefix, storage prefix, table default,
telemetry name and cryptographic domain identifier now uses `Noelia`/`noelia`/
`NOELIA`. There is no compatibility alias in the 5.0 assemblies.

## Before the first Noelia process starts

1. Stop every 4.x writer. Do not run both identities against the same stores.
2. Expire all 4.x access and refresh tokens, or migrate every revocation and
   session record before traffic reaches Noelia. An empty new revocation prefix
   must never be mistaken for “nothing was revoked”.
3. Export retained encrypted values and secrets with the exact 4.4.3 code that
   wrote them, then write them through Noelia. The authenticated domain strings
   changed, so copying ciphertext bytes is not a migration.
4. Archive and verify the 4.x Redis audit chain with 4.4.3. Start a new Noelia
   chain after the cutover; the signing derivation domain and event identity
   changed deliberately.
5. Copy the 32-byte master key into `noelia/master-key`, and copy the active
   password pepper into `noelia.passwords.primary`, before resolving either
   service. Losing the pepper makes existing password hashes unverifiable.
6. Rename the refresh-token table to `noelia_refresh_tokens` in a database
   migration. The named tenant filter is now `NoeliaTenant`.
7. Rename application configuration and secret-environment prefixes to
   `Noelia` and `NOELIA_`. In particular, JWT key variables are
   `NOELIA_JWT_KID`, `NOELIA_JWT_PRIVATE_KEY` and `NOELIA_JWT_PUBLIC_KEY`.
8. Update dashboards and alerts to the `noelia.*` meter names. The default
   Redis instance name, PostgreSQL database and PostgreSQL user are `noelia`.

Run this cutover first in `~/Projects/Demo`. A green build alone does not prove
that retained data, revocations or operator alerts survived the identity change.

## 5.0 API and composition changes

The major release also removes APIs that claimed behaviour without a consumer
and makes module dependencies executable.

- A custom module now calls `NoeliaBuilder.Use(module, register, contract)` and
  declares every external requirement with `Requires<T>(providerHints)` and
  every guaranteed service with `Provides<T>(package, registration)`. Missing
  requirements stop startup and name the service, module, package and call.
- `NoeliaComposition.Contracts` contains contracts only for registrations that
  actually ran. `Without(module, reason)` therefore removes both the
  registration and its active contract.
- `ISecretManager` is removed. Register and consume `ISecretProvider`; use
  `IVersionedSecretProvider` only when version access is required.
- `AddRedisSecretManager(...)` becomes `AddRedisSecretProvider(...)` and
  `AddInMemorySecretManager()` becomes `AddInMemorySecretProvider()`.
  `AddSecretManagement(configuration, environment)` becomes
  `AddSecretManagement()` and refuses startup until an `ISecretProvider` is
  registered.
- `SecretVersion` is metadata only. Its `Value` property is removed; read one
  value explicitly through `GetSecretVersionAsync`.
- `SecretRotationOptions` and `SecurityAuditOptions` are removed. Neither was
  read, so configuring either never changed runtime behaviour.
- `AddSecurityAudit(configuration)` becomes `AddSecurityAudit()` for the same
  reason.
- `ISovereigntyReport`, `SovereigntyAssessment`, `IAuditTrailService` and
  `AuditEvent` move to `Noelia.Abstractions` under the corresponding
  `Sovereignty` and `Audit` namespaces. Dashboard and provider packages can now
  consume the ports without inheriting the 44-package infrastructure graph.
- `ITokenSessionService`, `SignInResult` and `RefreshResult` move to
  `Noelia.Abstractions.Security.Sessions`. The infrastructure package still
  provides the implementation; update the namespace import in consumers.
- `ISecurityCheckRunner` runs value-free, timeout-bounded checks at startup and
  on explicit operator invocation. No HTTP endpoint is added.
- The optional `Noelia.Dashboard` package adds `NoeliaModule.Dashboard` through
  `UseDashboard(...)`. It is read-only, returns 404 without an explicit
  `VisibleTo(...)` policy and requires `InProduction(reason)` in Production.
  Configuration is rendered only as key/value shapes; no value is exposed.

The archived notes below use the corresponding Noelia 5 names for APIs and
components so that this repository contains one identity. Their version numbers
refer to predecessor releases; Noelia package ids did not exist before 5.0. Use
the matching historical tag when an exact old identifier is required for a
4.x migration.

---

# 4.4.2 → 4.4.3

**Sicherheitsbehebung.** Betroffen sind die verteilte Bremse, der Redis-
Geheimnisspeicher, die Redis-Sicherheitsprüfspur und PBKDF2 über
`IDataEncryptionService`. Zusätzlich werden bisher wirkungslose
Verschlüsselungs- und JWT-Einstellungen jetzt tatsächlich gelesen.

## Vor dem Umstieg: zwei Redis-Formate trennen

### `SecretManager`

Bis einschließlich 4.4.2 schrieb `SecretManager` AES-CBC ohne MAC. Solche
Datensätze sind nicht nachträglich beweisbar und werden von 4.4.3 nicht als
Format 2 gelesen. Stoppe Schreiber, exportiere benötigte Werte in einer
kontrollierten 4.4.2-Umgebung, untersuche den Schreibzugriff auf den Speicher
und schreibe die Werte erst danach mit 4.4.3 neu. Ein alter Wert, der bereits
manipuliert wurde, wird durch Neuverschlüsselung nicht rückwirkend echt.

Format 2 benutzt AES-256-GCM. Der Tag bindet Geheimnisname, Version,
Erstellungs- und Ablaufzeit, Aktivzustand und Ersteller. Ein verschobener
Datensatz oder eine nachträglich verlängerte Ablaufzeit wird abgelehnt. Der
konfigurierte Schlüssel muss genau 32 Byte ergeben. Ein nur für Entwicklung
erzeugter flüchtiger Schlüssel wird niemals protokolliert.

### Redis-Sicherheitsprüfspur

Die bisherige Kette schrieb die Ereignis-ID statt des Ereignis-Hashes als
Kettenkopf und haschte nur einen Teil der Sicherheitsfelder. Ihre Datensätze
sind mit dem vollständigen v2-Hash nicht kompatibel. Exportiere und bewahre den
alten Abschnitt unveränderlich auf. Beginne den 4.4.3-Abschnitt anschließend in
einer frischen Redis-Datenbank beziehungsweise nach einer ausdrücklich
gesicherten und kontrollierten Bereinigung ausschließlich der `audit:*`-Daten.

`AddRedisSecurityAudit()` verlangt nun einen stabilen `IMasterKeyProvider` und
leitet daraus einen zweckgetrennten HMAC-Schlüssel ab:

```csharp
services.AddConfiguredMasterKey();
services.AddRedisSecurityAudit();
```

Alternativ wird ein eigener stabiler 32-Byte-Schlüssel übergeben:

```csharp
services.AddRedisSecurityAudit(auditSigningKey);
```

Der neue Abschnitt hascht alle Ereignisfelder kanonisch, vergleicht Signaturen
zeitkonstant, hängt über mehrere Prozesse per Redis-Compare-and-set atomar an
und prüft die Kettentopologie unabhängig von gleichen Zeitstempeln.

## Verteilte Bremse

Jeder Aufruf erhält im gleitenden Redis-Fenster eine eigene zufällige 128-Bit-
Kennung. Gleichzeitige Aufrufe in derselben Millisekunde verbrauchen daher
jeweils einen Platz. Redis-Fehler werden nicht mehr als erfolgreicher Zählerstand
ausgegeben.

Die Ausfallvorgabe ist jetzt `DenyAll`: Ohne vertrauenswürdigen Zähler antwortet
die Middleware mit HTTP 503. Wer bewusst Verfügbarkeit vor Durchsetzung stellt,
muss das sichtbar konfigurieren:

```json
{
  "DistributedRateLimiting": {
    "CircuitBreaker": {
      "FallbackBehavior": "AllowAll"
    }
  }
}
```

## Hashing, Verschlüsselungsoptionen und JWT-Prüfung

- `HashingOptions.TimeCost` bedeutet bei PBKDF2 jetzt wörtlich Iterationen; die
  Vorgabe ist 600.000. Bereits gespeicherte 30.000-Runden-Werte bleiben
  prüfbar. Eigene Aufrufe, die den alten internen Faktor einkalkuliert haben,
  müssen die gewünschte tatsächliche Rundenzahl übergeben.
- `DefaultAlgorithm`, `DefaultHashingAlgorithm`, `DefaultPepper`,
  `MaxDataSize` und `CompressionThreshold` steuern jetzt den laufenden
  `DataEncryptionService`. Die Größenbegrenzung zählt UTF-8-Bytes vor
  Schlüsselzugriff und Pufferzuteilung.
- Der JWT-Konfigurationsprüfer liest wie die Laufzeit `JwtSettings` und
  `JwtSettings:ExpireMinutes`. Ein alter, nur für den Prüfer angelegter
  `Jwt`-Abschnitt hat keine Wirkung und sollte entfernt werden.
- `AddSecretStoreMasterKey()` erklärt `ISecretProvider` als Startanforderung.
  `AddOpenBaoSecretProvider(configuration)` registriert den vorhandenen
  selbst betreibbaren KV-v2-Anbieter vollständig.

---

# 4.4.1 → 4.4.2

**Sicherheitsbehebung.** Die öffentliche API bleibt unverändert. Betroffen ist,
wer mit dem Redis-Paket der 4.4.1-Vorgängerlinie verschlüsselte Werte gespeichert hat.

## Envelope-Steuerdaten sind jetzt authentifiziert

4.4.1 verschlüsselte den Payload mit AES-GCM, band aber die umgebenden
Steuerdaten nicht in den GCM-Tag ein. Insbesondere entschied
`Metadata["compressed"]` nach erfolgreicher Prüfung, ob die entschlüsselten
Bytes dekomprimiert werden. Wer Schreibzugriff auf das gespeicherte Envelope
hatte, konnte dieses Flag verändern, während die Entschlüsselung weiterhin
`Success = true` und `IntegrityVerified = true` meldete.

4.4.2 schreibt Envelope-Version `"2.1"`. Der GCM-Tag authentifiziert nun eine
kanonische, längenpräfixierte Darstellung von Version, Key-ID, Algorithmus, IV,
Caller-AAD, Zeitstempel, Integritätsfeld und sämtlichen Metadaten. Hinzufügen,
Entfernen oder Ändern eines semantischen Feldes lässt die Prüfung scheitern.

## Was du tun musst

1. Vor weiteren Schreibvorgängen auf 4.4.2 aktualisieren.
2. Gespeicherte 4.4.1-Envelopes der Version `"2.0"` kontrolliert mit 4.4.1
   lesen und mit 4.4.2 neu schreiben. 4.4.2 lehnt Version `"2.0"` bewusst ab,
   weil ihre Steuerdaten nachträglich nicht als authentisch bewiesen werden
   können.
3. Speicher untersuchen, auf die nicht vertrauenswürdige Parteien in der
   4.4.1-Laufzeit Schreibzugriff hatten. Neuverschlüsselung schützt künftige
   Änderungen, beweist aber nicht, dass ein altes Envelope unverändert blieb.

---

# 4.4.0 → 4.4.1

**Sicherheitsbehebung.** Eine Patch-Fassung, und das ist richtig: keine
Signatur ändert sich, und es ändert sich kein Verhalten, auf das sich jemand
berufen durfte — es stellt her, was zugesagt war. Advisory:
`GHSA-276v-hjxx-vrmw`.

## `AddEncryption` hat nicht verschlüsselt

**Betroffen:** Redis-Paket der Vorgängerlinie, jede Fassung bis einschließlich
4.4.0. Betroffen ist, wer `AddEncryption()` aufgerufen und abgelegt hat, was
`EncryptionResult.EncryptedData` zurückgab. Sonst niemand — `SecretManager`,
`KeyManagementService` und `FileBasedProvider` verschlüsseln jeder selbst und
sind davon nicht berührt.

**Was war.** `DataEncryptionService.EncryptAesGcmAsync` legte den Klartext
unverändert in den Ergebnispuffer:

```csharp
// Perform GCM encryption (simplified - in production use proper GCM implementation)
Array.Copy(data, encryptedData, data.Length);
```

Dazu eine Prüfsumme aus sechzehn Nullbytes, ein gewürfelter und nie benutzter
Vektor, und eine JSON-Hülle, die `"Algorithm":"AES256GCM"` trug. Daneben, im
Feld `IntegrityHash`, der SHA-256 **des Klartexts** — ein Orakel für jeden, der
den Speicher lesen kann. Ein fremder Schlüssel entschlüsselte dieselbe Hülle
und meldete `Success = true`, `IntegrityVerified = true`.

**Was jetzt gilt.** Echtes AES-256-GCM über
`System.Security.Cryptography.AesGcm` — kein neues Paket, nichts, dem man über
.NET hinaus vertrauen müsste. Vektor je Vorgang aus dem CSPRNG des
Betriebssystems und mitgespeichert, Prüfwert aus der Verschlüsselung, kein
Klartext-Hash an keiner Stelle. Beim Lesen wird die volle 16-Byte-Prüfsumme
verlangt, eine abgeschnittene abgelehnt.

| | 4.4.0 | 4.4.1 |
|---|---|---|
| Feld `Data` | der Klartext, base64 | Geheimtext |
| `AuthTag` | 16 Nullbytes | die GCM-Prüfsumme |
| Vektor | gewürfelt, gespeichert, **unbenutzt** | gewürfelt, gespeichert, benutzt |
| `IntegrityHash` | SHA-256 **des Klartexts** | leer — die Prüfsumme ist der Prüfwert |
| fremder Schlüssel | entschlüsselt, `IntegrityVerified = true` | `Success = false` |
| ein gekipptes Bit | fällt nicht auf | `Success = false` |
| zweimal derselbe Klartext | zweimal dasselbe Feld `Data` | zwei verschiedene |
| `EncryptionOptions.AdditionalData` | von **niemandem** gelesen | in die Prüfsumme gebunden |
| `CompressBeforeEncryption` | gab die Eingabe zurück, vermerkte `compressed=true` | komprimiert (gzip) |
| Hüllenfassung | `"1.0"` | `"2.0"` |

**Was du tun musst.**

1. Auf 4.4.1 heben. Nichts am Aufruf ändert sich.
2. **Die Werte wechseln.** Was so abgelegt wurde, gilt als offengelegt —
   gegenüber jedem, der den Speicher lesen konnte, und gegenüber jeder Kopie
   davon: Repliken, Sicherungen, Abzüge. Neue Schlüssel, neue Token, neue
   Zugangsdaten. Einen offengelegten Wert noch einmal zu verschlüsseln macht
   ihn nicht wieder geheim.
3. **Neu verschlüsseln, was bleiben soll.** 4.4.1 **lehnt eine Hülle der
   Fassung `"1.0"` ab** und sagt im Fehlertext, warum — statt etwas
   zurückzugeben, wofür es nicht einstehen kann. Solche Werte mit 4.4.0 lesen,
   unter 4.4.1 zurückschreiben.

## Zwei Dinge, die sich sonst noch ändern

**Nicht umgesetzte Verfahren werden abgelehnt statt ersetzt.**
`ChaCha20Poly1305`, `XChaCha20Poly1305`, `AES256CBC` und `AES128CBC` fielen
bisher durch den Vorgabezweig auf den AES-Weg und kamen als `AES256GCM`
gestempelt zurück. Jetzt kommt `Success = false` mit dem Namen des Verfahrens
im Fehlertext — dieselbe Haltung, mit der `HashAsync` die speicherharten
Hashverfahren seit 4.3 ablehnt, statt PBKDF2 unter ihrem Namen zu liefern.

Wer eines davon aufgerufen hat, hat nie bekommen, was er verlangte. Dass es
jetzt nein sagt, ist die Behebung, kein Rückschritt.

**`DecryptionResult.IntegrityVerified` ist jetzt auf `false` voreingestellt.**
Es stand auf `true`, und jeder Fehlerpfad der ausgelieferten Umsetzung gab das
so zurück — eine gescheiterte Entschlüsselung meldete geprüfte Echtheit. Wer
eine eigene Umsetzung von `IDataEncryptionService` schreibt und sich auf die
Vorgabe verlassen hat, setzt den Wert jetzt ausdrücklich. Die Richtung ist die
sichere: im Zweifel ungeprüft.

## Was noch gefunden wurde und **nicht** in 4.4.1 steckt

Beim Absuchen der ganzen Bibliothek nach derselben Bauart — etwas heißt nach
einer Sicherheitsleistung und erbringt sie nicht — kamen neun weitere Befunde
heraus, darunter eine Bremse, die einen gleichzeitigen Stoß durchlässt, und
eine Prüfspur, die mit einem Schlüssel je Prozess unterschreibt. Sie sind
gemessen, aufgeschrieben und **nicht** Teil dieser Fassung, damit eine
Sicherheitsbehebung klein und nachvollziehbar bleibt:
[`docs/befunde/`](docs/befunde/README.md).

---

# 4.3.0 → 4.4.0

Fünf Zusagen aus 4.3.0, die der Code nicht hielt. Nicht brechend in der Signatur,
aber sichtbar im Verhalten — Punkt 1 und 5 ändern, was in deinen Logs steht und
was in der Zusammensetzung erscheint.

## 1. Die Maskierung trifft Werte, nicht Namen von Dingen

**Was war.** `DataMaskingEnricher` verglich Eigenschaftsnamen als
**Teilzeichenkette** gegen eine eigene Fünf-Wort-Liste. Damit wurde maskiert, was
gar kein Geheimnis ist:

| Eigenschaft | 4.3.0 | 4.4.0 |
|---|---|---|
| `SecretName` (13 Log-Stellen in Noelia) | `***` | sichtbar |
| `TokenId` (4 Stellen) | `***` | sichtbar |
| `TokenEndpoint` | `***` | sichtbar |
| `AuthorizationPolicy`, `RefreshTokenLifetime` | `***` | sichtbar |

Ein Secret-*Name* ist kein Secret, eine Token-*Id* ist kein Token — beides ist,
woran ein Betreiber erkennt, welches gemeint ist.

**Was jetzt gilt.** Exakter Abgleich gegen `SensitiveFieldNames` — dieselbe
Liste, die der CQRS-Pfad und die HTTP-Middleware schon lesen. Deren Doku sagt
seit jeher, warum es eine Liste sein muss und warum exakt verglichen wird; 4.3.0
hatte eine zweite angelegt und die bestehende nicht angefasst.

**Zwei Folgen, die du in den Logs siehst:**

- **PII wird jetzt auch hier maskiert.** `Username`, `Email`, `City`, `Name`
  stehen auf der gemeinsamen Liste. Wo sie bisher im Konsolenlog standen, steht
  jetzt `[REDACTED]`. Wenn du eines davon sehen willst, nimm es aus
  `SensitiveFieldNames` — eine Stelle, alle drei Pfade.
- **Die Maske heißt `[REDACTED]`, nicht `***`.** Eine Maske im ganzen Logstrom,
  damit ein `grep` nach Schwärzungen alle findet.

`userpassword` und `clientsecret` sind der Liste hinzugefügt — sie waren
zusammengesetzte Namen, die der exakte Abgleich sonst verloren hätte, und sie
gelten jetzt auf allen drei Pfaden.

## 2. Der Revisions-Hash lässt sich nicht durch Verschieben fälschen

**Was war.** `AuditEvent<T>.WithComputedHash()` verkettete die Felder mit `|` —
einem Zeichen, das in ihnen vorkommen darf. `CorrelationId` kommt aus einem Kopf,
den der Aufrufer setzt, die Zustandsschnappschüsse sind JSON. Gemessen:

```
CorrelationId='a|b'  Before='c'    → +ecHCQYB1ul/Z1+sYLiPSgXKD6bd9M/MaDQEfwdpUcg=
CorrelationId='a'    Before='b|c'  → +ecHCQYB1ul/Z1+sYLiPSgXKD6bd9M/MaDQEfwdpUcg=
```

Zwei verschiedene Ereignisse, ein Hash, **beide bestanden `VerifyHash()`**. Wer
Schreibzugriff auf den Speicher hatte, konnte Inhalt über eine Feldgrenze
schieben, ohne die Manipulationserkennung auszulösen.

**Was jetzt gilt.** Jedes Feld wird mit seiner Länge geschrieben
(`4:usr|8:as self|…`). Verschieben ändert die Längen, also den Hash.

**Was zu tun ist:** bereits gespeicherte Ereignisse haben Hashes nach der alten
Regel. Sie verifizieren nach dem Umstieg nicht mehr. Wer eine laufende Kette hat,
schneidet sie ab und beginnt eine neue — der alte Abschnitt bleibt mit seiner
alten Regel prüfbar, wenn du ihn aufhebst.

## 3. Die Speicherreihenfolge ist die Kettenreihenfolge

**Was war.** `AuditTrailService` schrieb den Hash unter einem Lock fort, rief den
Sink aber **außerhalb**. Unter Last erreichten die Ereignisse den Speicher in
einer anderen Reihenfolge, als sie verkettet wurden — und ein Prüfer, der den
Speicher der Reihe nach liest, sah eine gebrochene Kette auf einem System, an dem
niemand etwas manipuliert hatte.

**Was jetzt gilt.** Kette und Schreibvorgang laufen in einem serialisierten Zug
(`SemaphoreSlim` statt `lock`, weil der Sink asynchron ist). Der Hash wird erst
fortgeschrieben, wenn der Sink das Ereignis hat: eine Kette, die über ein nie
gespeichertes Ereignis hinweg weiterläuft, hinterlässt eine Lücke, die kein
Prüfer schließen kann.

## 4. Die strenge Egress-Vorgabe lässt sich wirklich straffen

**Was war.** `SovereignPlatformBuilder` hatte `_allowLoopback` und
`_allowPrivateNetworks`, beide auf `true`, **ohne Setzmethode**. Ein Baumeister
für „Strict Egress", der immer alle RFC1918-Netze erlaubte und keinen Weg bot,
das abzustellen.

**Was jetzt gilt.** `WithoutLoopback()` und `WithoutPrivateNetworks()`.

```csharp
noelia.AddSovereignPlatform(s => s
    .WithoutPrivateNetworks()
    .Allow("openbao.internal", "postgres.internal"));
```

Die Vorgabe bleibt: beide erlaubt. Datenbank, Cache und Broker liegen dort.

## 5. Die souveräne Plattform ist ein Modul

**Was war.** `AddSovereignPlatform` registrierte direkt in `Services`. Damit
tauchte sie in `NoeliaComposition` nicht auf und ließ sich nicht mit
`Without(…, grund)` abwählen — dem Mechanismus, um den 4.0.0 herum gebaut ist.

**Was jetzt gilt.** `NoeliaModule.SovereignPlatform`, registriert über
`Use(modul, registrieren)` wie alles andere.

```csharp
noelia.UseDefaults()
      .AddSovereignPlatform(s => s.Allow("openbao.internal"))
      .Without(NoeliaModule.SovereignPlatform, "Abnahmeumgebung ruft absichtlich nach draußen");
```

Abgewählt wird **nichts** eingerichtet — keine Egress-Grenze, kein Report, keine
Prüfspur. Eine Grenze, die trotzdem stünde, würde weiter die Aufrufe abweisen,
für die der Grund geschrieben wurde.

**Was zu tun ist:** `AddSovereignPlatform` registriert `IConfiguration` nicht
mehr selbst. Ein Host tut das ohnehin; nur ein Test mit blanker
`ServiceCollection` muss es jetzt selbst tun.

---

# 4.2.3 → 4.3.0

Vier Ergänzungen. Nichts zu ändern auf deiner Seite — die Punkte oben in 4.4.0
korrigieren allerdings, was drei davon zugesagt und nicht gehalten haben.

- **Die Korrelationskennung steht auf der Konsole.** Das Development-Template
  trägt `[{CorrelationId}]`.
- **Maskierung im Serilog-Zug.** `DataMaskingEnricher`, standardmäßig
  registriert — siehe 4.4.0 §1 für die Form, die tatsächlich gilt.
- **Revisionssichere Prüfspur.** `IAuditTrailService`, `ISovereignAuditSink`,
  `AuditEvent<T>` mit SHA-256-Verkettung — siehe 4.4.0 §2 und §3.
- **`AddSovereignPlatform()`** bündelt Egress-Grenze, Report und Prüfspur —
  siehe 4.4.0 §4 und §5.

# 4.2.2 → 4.2.3

`Shape.Of` warf `NotSupportedException` bei einem Command mit
`ReadOnlyMemory<byte>`: Reflexion kann einen `ref struct`-Rückgabewert nicht
aufrufen, und `LoggingBehavior` rief `Shape.Of` **vor** `next()`. Ein Command
starb also am Protokollieren, bevor sein Handler existierte.

Getter, die Reflexion nicht aufrufen kann, werden jetzt mit ihrem Typnamen
beschrieben, und ein Fehler in `Shape` hält keine Anfrage mehr auf.

# 4.2.1 → 4.2.2

Die Grenzköpfe (`X-RateLimit-*`) standen nur auf der **erlaubten** Antwort —
ausgerechnet nicht auf der einen, bei der ein Aufrufer `X-RateLimit-Remaining: 0`
lesen will. `AddRateLimitHeaders` stand im Erlaubt-Zweig statt davor.

# 4.2.0 → 4.2.1

**Die Ausnahmelisten ließen sich nicht leeren.** `WhitelistedIps` trug Loopback,
`WhitelistedEndpoints` die Gesundheitspfade, und der .NET-Binder **ergänzt** eine
Sammlung, statt sie zu ersetzen:

```
"WhitelistedIps": [ "9.9.9.9" ]   →   127.0.0.1, ::1, 9.9.9.9
```

Wer die Liste bewusst ausschrieb, bekam die Vorgabe trotzdem, und nichts in
seiner Konfiguration sagte es ihm. **Beide Listen sind jetzt leer.** Wer eine
Ausnahme will, nennt sie — auch Loopback und die Gesundheitspfade.

Dazu: die Vorgabekette bremste ihre eigene Lebendprobe, weil `UseRateLimiting()`
vor `UseHealthCheckEndpoints()` stand.

# 4.1.0 → 4.2.0

**`Noelia.Http`** — Korrelation und Bremse ohne den Motor. `Noelia.Infrastructure`
zieht 44 transitive Pakete; wer nur eine Korrelationskennung wollte, erbte
Swashbuckle, OpenTelemetry, neun Serilog-Pakete, JWT-Bearer, TOTP und
FluentValidation. Das neue Paket hat **null** Fremdpakete: eine
`FrameworkReference` auf `Microsoft.AspNetCore.App` und einen Projektverweis auf
`Noelia.Abstractions`.

Die Namensräume bleiben `Noelia.Infrastructure.*` — Typen zwischen Assemblies zu
verschieben lässt jedes `using` übersetzen, ein Umbenennen bräche den Quelltext
jedes Aufrufers. `AddNoelia` und `UseNoelia` sind unverändert; **kein Aufrufer
muss etwas tun.**

`HttpPackageStaysThinTests` prüft die Projektdatei *und* die gebaute Assembly,
damit ein Fremdpaket nicht über einen Projektverweis hereinkommt.

# 4.0.2 → 4.1.0

Sieben Meldungen auf einmal, und fünf davon sind dieselbe Sorte Fehler: eine
Zusage, die weiter reicht als ihre Wirkung.

**1. Die Vorgabekette startete nicht.** `UseDefaults()` plus die Kette ohne
Lambda — der kürzeste dokumentierte Weg — starb beim Start mit
„UseRateLimiting() needs IDistributedRateLimitStore". Die Dienstseite hatte
`Without(modul, grund)`, die Kettenseite kannte die Auswahl nicht.
`NoeliaComposition` lag im Container und wurde nirgends gelesen.

`InfrastructureMiddlewareBuilder` liest sie jetzt, und jedes Glied überspringt
sich, wenn sein Modul nicht drin ist — das Tor steht **vor** `Requires<T>`, denn
ein abgewähltes Modul hat nichts registriert und darf seinen Dienst nicht
verlangen. Ohne Zusammensetzung im Container (der alte
`AddSharedInfrastructure`-Weg) wird nichts übersprungen: Schweigen ist kein Nein.

**2. `Authorization` war ein Modul für zwei Dinge.** Der Richtlinienanbieter, der
`[RequirePermission]` beantwortet, und `UsePermissions()`, das jede Anfrage ohne
Berechtigung abriegelt. Wer eine öffentliche Fläche hatte, musste zwischen beidem
wählen. Jetzt `Authorization` und `PermissionEnforcement`, beide in der Vorgabe.

---

# 4.0.1 → 4.0.2

Zwei Fehler im Katalog, beide aus 4.0.0. Nicht-brechend.

## 1. `IJwtService` stand ohne seinen Schlüsselbund im Behälter

`NoeliaModule.Jwt` registrierte `IJwtService` bedingungslos. `JwtService` nimmt
einen `KeyRing` als Konstruktorargument, und den legte kein Weg des Baumeisters
ab — weder `FromSharedSecret()` noch `VerifyOnly(...)` noch `Issue(...)`. Das
einzige `AddSingleton(keys)` stand auf dem alten Weg, den `AddNoelia` nicht ruft.

Weil `IJwtService` `Scoped` ist, fiel das erst bei der ersten Anfrage auf, die
ein Token anfasst.

Auf dem alten Weg schützte die Kopplung: beide Registrierungen standen im
selben `if (keys is not null)`-Block — beide oder keine. Der Katalog hatte sie
getrennt und eine Hälfte mitgenommen. Sie stehen jetzt in
`AddJwtAuthentication(services, keys, authority, …)`, durch das jeder Weg läuft
— die einzige Stelle, an der sie sich nicht wieder trennen lassen.

**Was sich für dich ändert:** `UseDefaults()` allein registriert keinen
`IJwtService` mehr. Es gab dort auch vorher keinen brauchbaren; jetzt sagt der
Behälter das, statt ihn bei der ersten Anfrage zu verweigern. Wer ihn braucht,
sagt, woher die Schlüssel kommen:

```csharp
noelia.UseDefaults().UseJwt(jwt => jwt.VerifyOnly(publicKey, keyId));
```

## 2. `NoeliaModule.Authorization` tat, was `ResourceAuthorization` heißt

Das Modul rief `AddResourceAuthorization()`. `[RequirePermission]` nennt eine
`Permission:`-Politik, die allein `PermissionPolicyProvider` beantwortet — und
der wird von `AddAuthorization()` registriert. Ohne ihn lehnt das Rahmenwerk jede
Anfrage an genau die Endpunkte ab, die das Attribut schützen soll, als „policy
not found". Fail-closed, aber lautlos: ein Absturz wird behoben, ein falscher
Name überlebt.

Jetzt gibt es zwei Module mit ehrlichen Namen:

| Modul | Was | In `UseDefaults()` |
|---|---|---|
| `Authorization` | Berechtigungspolitiken, `[RequirePermission]` | ✅ |
| `ResourceAuthorization` | `ResourceRead`, `ResourceOwner` und die übrigen | ❌ |

**Was sich für dich ändert:** wer Ressourcenpolitiken benutzt, nennt sie und
registriert einen Anbieter:

```csharp
noelia.UseDefaults()
      .Use(NoeliaModule.ResourceAuthorization);   // + AddInMemoryResourceAuthorization()
```

Sie sind nicht in der Vorgabe, weil ihre beiden Handler
`IResourceAuthorizationService` als Konstruktorargument nehmen. Das kam beim
Einschalten von `ValidateOnBuild` heraus: die Anforderung war nie erklärt, und
ein Behälter mit ihnen ließ sich nicht bauen.

## Und der Wächter dazu

Die Konformitätstests bauen den Behälter jetzt mit `ValidateOnBuild` und
`ValidateScopes`. Ein Modul, das einen Verbraucher ohne seine Abhängigkeit
registriert, fällt damit beim Bauen auf — nicht in Produktion. Beide Fehler
oben wären so nie herausgekommen.

---

# 4.0 → 4.0.1

Eine Zeile, dreimal. Nichts zu ändern auf deiner Seite.

## Fremde Antwortrümpfe stehen nicht mehr im Protokoll

`ServiceCommunicationManager` schrieb den Rumpf einer fehlgeschlagenen Antwort
auf `Warning` — beim GET, beim POST, und die Fehlerliste aus einem
`success: false`-Umschlag noch dazu. Dieser Rumpf ist die Auskunft des anderen
Dienstes über dessen Daten, und er landet hier wörtlich: ein Name, eine Adresse,
was die Gegenseite eben in ihren Fehler geschrieben hat.

Seit 4.0.0 kommt er ohnehin in `ServiceResponse.Body` beim Aufrufer an. Im
Protokoll ist er damit nicht nur riskant, sondern überflüssig — und das
Aufbewahren übernimmt man dabei für Daten, die dieser Dienst nie bekommen hat.

Protokolliert wird jetzt die Form: welcher Dienst, welcher Status, wie viele
Bytes beziehungsweise wie viele Fehler. Die Werte gehören dem Aufrufer, der
weiß, ob er sie behalten darf.

```
vorher   GET to UserService answered NotFound: {"error":"anna@example.com hat kein Konto"}
jetzt    GET to UserService answered NotFound (48 bytes)
```

Wer den Rumpf im Protokoll haben will, schreibt ihn selbst — dort, wo die
Entscheidung darüber hingehört.

---

# 3.x → 4.0

Seven changes. One is the new entry point; six are fixes that could not wait,
because the new shape would have set them in stone.

If you call `AddSharedInfrastructure` and nothing else, only §4 and §5 can reach
you. If you take `GetAsync` from `IServiceCommunicationManager` or you run behind
a proxy, read §3 and §4 first.

---

## 1. `AddNoelia` — a default you can see, and depart from

**What changed.** There were two entry points, and both were wrong for someone
who knows what they want. One decided thirteen modules for you. The other
**replaced** the default instead of adjusting it — taking it cost thirteen
modules *and* everything outside the module system (Serilog, Swagger, CORS, the
JSON conventions, the `HttpContext` accessor), with nothing said about it.

| Before | Now |
|---|---|
| `AddSharedInfrastructure(config, env, name)` | `AddNoelia(config, env, name, g => g.UseDefaults())` |
| `AddSharedInfrastructure(config, env, name, infra => infra.AddJwtAuthentication().AddHealthChecks())` | `AddNoelia(config, env, name, g => g.UseDefaults().Without(NoeliaModule.Communication, "no broker"))` |
| — no way to say why something was left out | `Without(module, reason)` — the reason is a parameter with no default |
| — a provider could not contribute a module | `Use(module, register)`, and `UseInMemoryCache(...)` from the provider package |

`UseDefaults()` is a call you can see and delete. Delete it and you get nothing,
the way Entity Framework gives you nothing without a provider.

**The two are not identical.** `UseDefaults()` contains only what starts with
nothing else registered. Three modules the old entry point included are not in
it, because each needs a decision Noelia must not make for you:

| Module | Needs | Add it with |
|---|---|---|
| `HttpResponseCaching` | a distributed cache | `.Use(NoeliaModule.HttpResponseCaching)` + `AddRedisCache(prefix)` or `AddInMemoryCache(prefix)` |
| `Communication` | a message bus | `.Use(NoeliaModule.Communication)` + `AddMessaging(...)` |
| `Encryption` | a master key | `.Use(NoeliaModule.Encryption)` + `AddConfiguredMasterKey()` or `AddSecretStoreMasterKey()` |

A service that used them keeps working by naming them. A service that did not is
now free of three startup requirements it never wanted.

`AddSharedInfrastructure` still exists and still does what it did.

## 2. Forwarded headers are believed only from proxies you name

**What changed.** Rate limiting read `X-Forwarded-For` and `X-Real-IP`
unconditionally, and so did the audit trail, telemetry and input sanitisation.
A header the caller writes is not information about the caller.

Worst in `IsWhitelisted`: the exemption list was read through the same header and
carries `127.0.0.1` by default, so `X-Forwarded-For: 127.0.0.1` removed the rate
limit entirely, with no configuration at all.

**What to do.** If your service is reached directly, nothing. If it sits behind a
load balancer or ingress, name it — otherwise every request now counts as coming
from the proxy, which is one bucket for everyone:

```csharp
noelia.UseRateLimiting(rate => rate.TrustForwardedHeadersFrom("10.0.0.0/8"));

// or, outside the rate limit builder
services.TrustForwardedHeadersFrom(["10.0.0.0/8"]);
```

`X-Real-IP` is no longer read anywhere. It is not part of the platform's
forwarded-headers set, and a second parser would be a second thing to get right.
A proxy that sets only that header says so:

```csharp
services.TrustForwardedHeadersFrom(["10.0.0.0/8"],
    options => options.ForwardedForHeaderName = "X-Real-IP");
```

## 3. One rate limiter, and it reads its own setting

**What changed.** There were three. `RateLimitMiddleware` asked
`IRateLimitService`, whose `CheckRateLimitAsync` runs over a rule collection —
empty unless someone registered rules, and the only way to register them,
`ConfigureRateLimitRules`, registers a singleton factory for `IRateLimitService`
that resolves `IRateLimitService` in its own body. Anyone who resolved it lost
the process with no log. `RateLimitingMiddleware` counted in an `IMemoryCache`,
per process, and was wired nowhere.

`DistributedRateLimitingMiddleware` remains.

**Removed:** `IRateLimitService`, `RateLimitService`, `InMemoryRateLimitService`,
`AddRedisRateLimiting()`, `AddInMemoryRateLimiting()`, `AddRateLimit()`,
`AddRateLimitMiddleware()`, `ConfigureRateLimitRules()`, `RateLimitMiddleware`,
`RateLimitingMiddleware`, `RateLimitOptions`, `RateLimitingOptions`, and the rule
model that served them.

**What to do.** Delete the calls. Rate limiting counts through the cache, so
`AddRedisCache(prefix)` or `AddInMemoryCache(prefix)` is what decides whether the
counters are shared between instances.

**Also changed:** `ClientIdStrategy` and `CustomClientIdExtractor` were written on
an options class and read nowhere — asking for per-origin counted per user.
`EnableIpRateLimiting` and `EnableUserRateLimiting` said the same thing twice and
could disagree. All four are replaced by one setting that is read on every
request:

| Before | Now |
|---|---|
| `EnableUserRateLimiting = true, EnableIpRateLimiting = true` | `Subject = RateLimitSubject.UserThenOrigin` (the default) |
| `EnableIpRateLimiting = true, EnableUserRateLimiting = false` | `Subject = RateLimitSubject.Origin`, or `.PerOrigin()` |
| `EnableUserRateLimiting = true, EnableIpRateLimiting = false` | `Subject = RateLimitSubject.User`, or `.PerUser()` |
| `ClientIdStrategy = Custom` + `CustomClientIdExtractor` | `.PerSubject(context => ...)` |

## 4. A status code reaches the caller

**What changed.** `IServiceCommunicationManager.GetAsync` and `SendRequestAsync`
returned `TResponse?` and mapped every non-2xx onto `null`, so "does not exist",
"exists and the field is empty" and "the far service is broken" were one value.

```csharp
// before
var user = await services.GetAsync<User>("UserService", "/api/users/1");
if (user is null) { /* which of the three? */ }

// now
var answer = await services.GetAsync<User>("UserService", "/api/users/1");
if (answer.IsSuccess) { Use(answer.Value!); }
else if (answer.Status == HttpStatusCode.NotFound) { /* ... */ }
else { logger.LogWarning("{Status}: {Body}", answer.Status, answer.Body); }
```

Only a call that got no answer at all still throws.

**Also changed:** `ResilientHttpPolicyHandler` threw on every non-success status,
and the retry policy turned that into three attempts — a `404` asked three times,
a `401` three times. It now retries only what a second attempt could answer
differently: `408`, `429` and `5xx` except `501`. Everything else comes back on
the first attempt, as the answer it is.

Responses that failed are no longer cached: a remembered `404` keeps answering
after the resource appears.

## 5. The correlation id travels on its own

**What changed.** `LoggingBehavior` reads
`Activity.Current?.GetBaggageItem("CorrelationId")`, and `AddBaggage` appeared
nowhere in Noelia — a reader with no writer. What was written was `SetTag`, and a
tag stays on the span it was written to.

The id therefore travelled only through `ServiceCommunicationManager` or
MassTransit. A bare `HttpClient` sent nothing.

**What to do.** Nothing, if you use `AddNoelia` — `CorrelationPropagation` is in
`UseDefaults()`. Otherwise:

```csharp
services.AddCorrelationIdPropagation();
```

Every client the factory builds then carries the id. One you set yourself is left
alone, and outside a request none is invented.

## 6. The session store joins a transaction you already have

Unchanged from 3.0.1, and repeated here because it is what makes an audit row and
the change it records commit together. See the 3.0 → 3.0.1 section below.

## 7. Nothing else

Identity, permissions, resources, sessions, passwords, token revocation,
messaging, persistence, health checks, telemetry and the sovereignty report are
unchanged.

---

# Noelia 3.0 → 3.0.1

One bug fix. Nothing to change on your side.

## The refresh token store no longer insists on owning the transaction

`EntityFrameworkRefreshTokenStore.TryConsumeAsync` opened a transaction
unconditionally. A caller that already had one — writing an audit row in the same
transaction as the change it records, which is the pattern this library argues
for elsewhere — got:

```
System.InvalidOperationException: The connection is already in a transaction
and cannot participate in another transaction.
```

The line ran through the middle of one interface: `CreateAsync` only saves, and a
save joins whatever is open, so **signing in worked and refreshing did not**.

It now opens a transaction only when none is open, and joins the caller's
otherwise. A rotation inside a caller's bracket commits and rolls back with it.
The store never rolls a caller's transaction back — where a concurrent refresh
has already taken the row, the conditional update matched nothing, there is
nothing to undo, and the caller keeps its work.

No API changed. If you never had a transaction open across a refresh, nothing is
different for you.

---

# Noelia 2.x → 3.0

Five things changed. Three are bug fixes — two can stop a service starting, one
makes something start working that silently did nothing — and two are namespaces
nobody outside Noelia had reason to write.

If you register a cache, inject `IETagGenerator` nowhere, and never named
`ProviderRequirements`, upgrading is a version number.

---

## 1. `AddCQRS()` takes a cache only where you actually cache

**What changed.** `AddCQRS(assemblies)` scans what you hand it. If nothing in
those assemblies implements `ICacheableQuery` or `ICacheInvalidatingCommand`,
`CachingBehavior` and `CacheInvalidationBehavior` are no longer put in the
pipeline at all. If something does, the cache is required — and missing it now
refuses the start rather than surfacing on whichever request happens to reach
the behaviour:

```
Noelia is missing 1 provider registration(s):
  • AddCQRS() (GetJobQuery implements ICacheableQuery) needs IDistributedCacheService — call AddRedisCache(prefix) or AddInMemoryCache(prefix)
Provider packages: Noelia.Redis, Noelia.InMemory, Noelia.Messaging.MassTransit, Noelia.Data.EntityFrameworkCore.
```

The count is missing services, not modules that asked for them: where several
registrations need one cache, that is one line and one thing to register, with
every one of them named.

**Why.** Both behaviours took `IDistributedCacheService?` and checked it for
`null`, which reads as "the cache is optional". The container does not honour C#
nullability: without a default value it throws rather than passing `null`. So
the promise in the type never held, and the first `Send` died on a Noelia type
the caller never wrote. Meanwhile a service that caches nothing had to register
a cache anyway — and a composition root is the place you look to see which
cross-cutting decisions a service took, so it ended up claiming one it had not.

Giving the parameters `= null` would have worked and been worse: marking a query
`ICacheableQuery` with no cache registered would then produce no error, no log
line, and no caching. A feature you switch on that does nothing is worse than
one that is missing.

**What to do.**

- *You cache and you register a cache.* Nothing. This is the common case.
- *You cache nothing.* You may delete `AddInMemoryCache(...)` from your
  composition root if it was only there to satisfy `AddCQRS()`. Nothing is
  cached either way; the difference is that the file stops saying otherwise.
- *You cache and you register no cache.* The service will not start. It was
  never caching — the behaviour swallowed it — so either register a cache, or
  drop `ICacheableQuery` from the query.

Any `IDistributedCacheService` satisfies the requirement. The check asks for the
interface, not for who registered it, so a provider you wrote yourself counts.

---

## 2. `ProviderRequirement` and `ProviderRequirements` moved

**What changed.** Both moved from `Noelia.Infrastructure.Builder` to
`Noelia.Abstractions.Hosting`.

**Why.** `AddCQRS()` lives in `Noelia.Application`, which
`Noelia.Infrastructure` references — so it could not reach the collector that
holds what a registration needs. The types describe a port, not an engine, and
now sit with the other ports where everything can see them.

**What to do.** Change the `using` if you named either type. You almost
certainly did not: `InfrastructureBuilder.RequiresProvider<T>(requiredBy,
remedy)` is unchanged, and it is the only thing that ever wrote to them.

New, and only if you want it: the same declaration is now available on a plain
collection, for registrations that stand outside the `AddSharedInfrastructure`
chain.

```csharp
using Noelia.Application.Hosting;

services.RequiresProvider<IDistributedCacheService>(
    "AddMyThing()", "AddRedisCache(prefix) or AddInMemoryCache(prefix)");
```

---

## 3. The CQRS pipeline no longer needs `AddHttpResponseCaching()`

**What changed.** `CacheInvalidationBehavior` no longer takes `IETagGenerator`.
It clears stale ETags through the `IDistributedCacheService` it already holds.
`IETagGenerator` itself moved from `Noelia.Application.Abstractions` to
`Noelia.Infrastructure.Caching.Http`, beside its implementation and its one
legitimate consumer.

**Why.** `IETagGenerator` is HTTP: its documentation says "for HTTP responses",
its patterns are API paths, and the only thing that ever registered it was
`AddHttpResponseCaching(...)`. A transport-independent layer hung off it, so a
service had to switch on HTTP response caching to make its **command** pipeline
start — and its composition root then said something nobody meant.

Nine methods were reachable through that dependency. The pipeline called one,
`InvalidateETagsByPatternAsync`, and that one is a single
`RemoveByPatternAsync` behind a string concatenation. Splitting the interface
would have been ceremony for a one-liner; the dependency simply goes away.

The shared part is now the key prefix rather than an interface —
`Noelia.Abstractions.Caching.CacheKeys.ETagPrefix`. The agreement about that key
already existed; it was a `private const` and a comment instead of a name.

**What to do.**

- *You inject `IETagGenerator` in a controller or service of your own.* Change
  the `using` to `Noelia.Infrastructure.Caching.Http`. Nothing else about it
  moved — same methods, same behaviour, still registered by
  `AddHttpResponseCaching(...)`.
- *You called `AddHttpResponseCaching(...)` only to satisfy `AddCQRS()`.* Delete
  it. ETag invalidation from commands no longer goes through it.
- *You want HTTP response caching.* Nothing. `AddCaching()` still brings it, and
  `ETagInvalidationPatterns` on your commands still clear the ETags the
  middleware stored — they meet in the cache, under the same prefix as before.

---

## 4. In-memory pattern invalidation actually removes keys now

**What changed.** `Noelia.InMemory`'s `RemoveByPatternAsync` applies the store's
key prefix to the pattern, and matches a wildcard anywhere in it.

**Why.** It matched the raw pattern against keys that had the prefix applied. So
with `AddInMemoryCache("jobs-service")` — and the prefix is not optional there —
a pattern matched nothing and the call removed no keys. Every
`InvalidationPatterns` and `ETagInvalidationPatterns` entry in an in-memory
deployment did nothing, quietly. The Redis store prefixes its `SCAN` pattern and
always did, so the two disagreed about the same contract.

**What to do.** Nothing, unless you were working around it. Check any place you
passed a pattern that already carried the prefix to compensate: it will now be
prefixed twice and match nothing.

---

## 5. `AddHttpResponseCaching()` requires a cache

**What changed.** `AddHttpResponseCaching(...)` declares that it needs an
`IDistributedCacheService`, checked at startup like every other module.
`ETagGenerator` takes one as a mandatory constructor parameter, and the four
branches that did nothing when it was absent are gone.

**Why.** The parameter was `IDistributedCacheService?` with no default value, so
the container threw rather than passing `null` — which made those four branches
unreachable. Read either way it was wrong: calling `AddHttpResponseCaching(...)`
on its own crashed on a Noelia type the caller never wrote, and the degradation
the code appeared to offer was never once taken.

Of the nine methods on `IETagGenerator`, four exist only to store and retrieve
ETags. An ETag store with nowhere to store is not one, so the cache is required
rather than made optional.

**What to do.**

- *You reach it through `AddCaching()`.* Nothing. That module already required a
  cache, and the two requirements are reported as the one registration they are.
- *You call `AddHttpResponseCaching(...)` directly.* Register a cache —
  `AddInMemoryCache(prefix)` or `AddRedisCache(prefix)` — or drop the call. It
  was not working without one.
- *You construct `ETagGenerator` yourself.* Pass a cache instead of `null`.

---

## 6. Nothing else

No other signature changed and no other default moved.

---

# Noelia 1.x → 2.0

Three things were removed and one default changed. Nothing else moved.

If you are on 1.5.x and use none of the four, upgrading is a version number.

---

## 1. Access tokens now last 15 minutes, not 60

**What changed.** `JwtSettings.ExpireMinutes` defaults to `15`.

**Why.** That number is the window in which a signed-out person is still let in
— every service verifies an access token from its signature alone and asks
nothing. Sixty minutes is a long time for something nobody chose, and shipping
it as the default meant most deployments had it.

**What to do.** Nothing, if fifteen minutes suits you. Clients refresh roughly
four times as often; each refresh is one read and two writes in your own
database.

To keep the old behaviour, say so:

```json
{ "JwtSettings": { "ExpireMinutes": 60 } }
```

**How to tell it matters to you.** If you run no revocation store, this is how
long "sign out" takes to reach the services that only verify. Shortening it is
the cheap way to close that window; a revocation store is the way to close it
to zero.

---

## 2. `UserClaims.FirstName` and `.LastName` are gone

**What changed.** Both properties were removed. They were marked `[Obsolete]` in
1.2.

**Why.** Neither was ever written into a token. They were validated as required
— so Noelia refused to issue a token to anyone whose name does not split into
two parts, including mononyms and service accounts — and then discarded.

**Before**

```csharp
var token = await jwt.GenerateTokenAsync(new UserClaims
{
    UserId = subject.ToString(),
    Email = email,
    FirstName = person.FirstName,   // never reached the token
    LastName = person.LastName
});
```

**After**

```csharp
var token = await jwt.GenerateTokenAsync(new UserClaims
{
    UserId = subject.ToString(),
    Email = email
});
```

**If you need a display name in the token**, put it in `CustomClaims`, where it
is actually emitted:

```csharp
CustomClaims = new Dictionary<string, string> { ["name"] = person.DisplayName }
```

Consider whether you want it there at all: a token travels through logs and
proxies, and a name is one of the few things in it that identifies a person to
someone reading over a shoulder.

---

## 3. `IJwtService.GenerateRefreshTokenAsync()` and `TokenResult.RefreshToken` are gone

**What changed.** The method was removed from the interface and the property
from the result.

**Why.** The method returned 64 random bytes that were **stored nowhere and
validated by nothing**, and `TokenResult.RefreshToken` was filled with them. A
developer who saw that field and built a `/refresh` endpoint had nothing to
compare against — so either they wrote the whole store themselves, in which case
Noelia generating the value bought nothing, or they treated the presence of a
token as the check, in which case they had a hole because a library looked as
though it had handled this.

**Before**

```csharp
var result = await jwt.GenerateTokenAsync(claims);
return new { access = result.AccessToken, refresh = result.RefreshToken };
```

**After** — refresh tokens come from `ITokenSessionService`, which records them:

```csharp
builder.Services.AddSharedInfrastructure(config, env, "identity", infra => infra
    .AddJwtAuthentication(o => { /* keys */ })
    .AddTokenSessions());

builder.Services.AddInMemoryRefreshTokens();
// or, for the database you already run:
// builder.Services.AddEntityFrameworkRefreshTokens<AppDbContext>();
```

```csharp
var signIn = await sessions.SignInAsync(subject);
var access = await jwt.GenerateTokenAsync(new UserClaims
{
    UserId = subject.ToString(),
    Email = email,
    SessionId = signIn.Session.ToString()   // so a revocation can name this device
});

return new { access = access.AccessToken, /* signIn.RefreshToken → HttpOnly cookie */ };
```

**If you already built your own refresh flow**, keep it. `ITokenSessionService`
is a service, not a requirement — implement `IRefreshTokenStore` over your
existing table if you want the rotation and reuse detection without changing
where anything lives.

### The EF Core store needs a table

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    modelBuilder.ConfigureNoeliaRefreshTokens();
```

Then add a migration. The table is yours, in your database; Noelia holds no
connection of its own and runs no cleanup — call `PurgeAsync(olderThan,
batchSize)` from whatever already runs your scheduled work.

---

## 4. Nothing else

These stayed exactly as they were, and a 1.x service that uses them compiles
and behaves the same:

- `AddSharedInfrastructure` in both forms, and every module
- `AddJwtAuthentication()` with no arguments — still the shared secret from
  `JwtSettings:Secret` or `JWT_SECRET`
- The whole pipeline builder
- Every provider registration
- Token revocation, in full

---

## Worth doing while you are here

Not required, and not part of 2.0. Each is a change a 1.5 service can make
today.

**Give each service only the key half it needs.** With a shared secret the
verification key *is* the signing key, so every service that checks a token can
mint one — for any subject, with any role. A pair separates them, and the type
enforces it:

```csharp
// the issuing service
o.SigningKey = SigningKey.FromEcdsaPrivateKey(privateKey, kid);
o.ValidationKeys.Add(SigningKey.FromEcdsaPublicKey(publicKey, kid));

// everyone else — holds nothing it could sign with
o.ValidationKeys.Add(SigningKey.FromEcdsaPublicKey(publicKey, kid));
```

Both algorithms can be live at once, so the change costs nobody their session:

```csharp
o.ValidationKeys.Add(SigningKey.FromEcdsaPublicKey(publicKey, kid));
o.ValidationKeys.Add(SigningKey.FromSharedSecret(legacySecret, kid: null));
```

Drop the secret once every token issued under it has expired.

**Stop writing your own password hashing.** `AddPasswordHashing()` gets the salt,
the work factor, the fixed-time comparison and the upgrade path right, with no
package and no licence. Pass `null` for the stored entry when the account does
not exist — that is what keeps an unknown address as slow to answer as a wrong
password.

**Check where your refresh token lives in the browser.** Not `localStorage` and
not `sessionStorage`: script reads both, and a refresh token is worth days where
an access token is worth minutes.
