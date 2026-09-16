# Demo im Repo, drei Umgebungen, Befunde — Noelia 5.1

**Stand:** 16.09.2026 · **Ergebnis:** umgesetzt und abgenommen
**Daneben:** [MASTERPLAN-5.0.md](MASTERPLAN-5.0.md) · [MIGRATION.md](../MIGRATION.md) · [befunde/](befunde/)

Die Demo war ein nicht versioniertes Verzeichnis neben dem Repository
(`~/Projects/Demo`). Sie ist eingezogen, läuft jetzt in drei Umgebungen aus
einem `docker compose`, und dabei sind die Befunde der Bestandsaufnahme vom
15.09.2026 behoben worden — die der Demo und die von Noelia selbst.

Der wichtigste Satz dieses Dokuments steht am Anfang, weil er die Arbeit
zusammenfasst: **Die Bestandsaufnahme suchte nach dem Grund für
`"checks": []` und fand dieselbe Fehlerklasse noch achtmal.** 5.0 hat die
Mechanik gebaut, die „anwesend, aber nicht wirksam" aufdeckt. 5.1 ist, was
diese Mechanik zuerst gefunden hat — angewandt auf Noelia.

---

## 1. Entscheidungen

| Frage | Entscheidung |
|---|---|
| Wo liegt die Demo? | `demo/`, **nicht** unter `src/`, **nicht** in `Noelia.slnx` |
| Bleibt die Fremdverbraucher-Eigenschaft? | Ja, mechanisch: `DemoStaysAForeignConsumerTests` |
| Wie wird gestartet? | Ein `docker-compose.yml`, Profile `dev`, `staging`, `prod`, `all` |
| Wie erreicht man was? | Ein Edge-Proxy, sechs `*.localhost`-Hostnamen, TLS ab Staging |
| Was liegt auf GitHub Pages? | Ein statisches Schaufenster mit echten Aufnahmen |
| Version | 5.1.0 |

`demo/` statt `src/`, weil `PackageDependencyBudgetTests` die Projektmenge unter
`src/` exakt mit den dreizehn ausgelieferten Paketen vergleicht — und zu Recht:
Was dort liegt, ist ein Paket, das jemand installiert.

Der Verweis-Wächter steht als Test da und nicht als Satz in einer Anleitung,
weil das Release-Gate nur etwas beweist, solange die Demo Noelia **als Paket**
bezieht. Im selben Repository ist ein `ProjectReference` eine Zeile weit weg.

---

## 2. Was gebaut wurde

### Ein Stack, drei Umgebungen, zwei Architekturen

15 Container bei `--profile all`: je Umgebung Frontend-Edge, Gateway,
User-Service, Todo-Service und Monolith, dazu je ein Valkey für Staging und
Production. Jede Umgebung hat ihr **eigenes Docker-Netz** — deshalb darf
`ocelot.json` weiter `user-service` sagen, und keine Stufe kann eine andere
erreichen.

| URL | Dahinter |
|---|---|
| `http://micro-dev.localhost:8080` | Microservices, Dashboard offen |
| `http://mono-dev.localhost:8080` | Monolith, Dashboard offen |
| `https://micro-staging.localhost:8443` · `mono-staging` | Operator-Nachweis, Valkey |
| `https://micro-prod.localhost:8443` · `mono-prod` | dasselbe, Freigabegrund festgehalten |

Kein Dienst veröffentlicht einen eigenen Port. Vorher lauschten User- und
Todo-Service auf `0.0.0.0:8081` und `:8082`, wo jeder Client auf der Maschine
das Gateway umgehen konnte.

### Die Umgebungen unterscheiden sich in zwei Dingen

| | `Development` | `Staging` / `Production` |
|---|---|---|
| Cache, Bremse, Widerruf | im Prozess | Valkey |
| Prüfspur | `InMemorySovereignAuditSink` | derselbe — Noelia liefert keine zweite Senke |
| Dashboard (user, todo, mono) | offen | Operator-Nachweis |
| Dashboard (gateway) | offen | **nicht komponiert** |
| Transport | HTTP | HTTPS, HSTS, 308 von HTTP |

Beides steht in `appsettings.{Environment}.json`, nicht im Code. Ein Kunde mit
`dev/int/cons/prod` legt vier Dateien an statt vier Bedingungen.

Das Gateway ist außerhalb von Development **nicht komponiert**, weil es keinen
Schlüssel hält und kein Token prüft — es könnte einen Betreiber gar nicht
erkennen. Ein Dashboard auf drei von vier Diensten wäre die falsche Lehre.

**TLS ist keine Zierde.** Zwei Befunde lassen sich über reines HTTP nicht
beheben, nur verstecken: ein `Secure`-Cookie wird auf einer unsicheren Anfrage
nicht gesetzt, und HSTS in einer Production-Haltung ließe den Browser jede
`http://`-Anfrage umschreiben und scheitern.

---

## 3. Befunde

### Noelia — in 5.1.0

| Befund | Behebung |
|---|---|
| `AddDatabaseHealthCheck()`, `AddRabbitMqHealthCheck()`, `AddExternalApiHealthChecks()`: leerer Rumpf, geben `this` zurück | entfernt; sie lassen sich in einem anbieterfreien Paket nicht füllen |
| Deren Tests nageln den Rückgabewert fest, nicht die Wirkung | entfernt |
| `RabbitMQHealthCheck` verlangt ein `IConnection`, das MassTransit nicht registriert | Klasse und 130 Zeilen Tests entfernt; MassTransits eigener Bus-Check trägt das Etikett `ready` |
| `RedisHealthCheck` ruft `INFO` — ein Admin-Kommando, das außerhalb Development gesperrt ist | auf Schreiben/Lesen/Vergleichen/Löschen umgebaut, kein `INFO` mehr |
| `RedisPerformanceHealthCheck` liest Serverinterna in den Rumpf von `/health` | entfernt; Kennzahlen gehören in die Telemetrie |
| Die Health-Antwort schreibt `exception.Message` auf einen anonymen Endpunkt | `description` statt Ausnahmetext, Detail im Protokoll |
| `/health/ready` meldet `200` ohne eine einzige Prüfung | neuer Check `noelia.health.readiness-coverage`, meldet das als `Fail` |
| `TrustForwardedHeadersFrom` belegt Optionen, die Noelias Pipeline nie anwendet | `UseForwardedHeaders()` als erster Schritt der Standardkette, sofern Vertrauen erklärt wurde |
| Ein Gateway beendet die Kette: `UseForwardedHeaders` verbraucht die Kopfzeilen | neuer `ForwardedOriginHandler` in `Noelia.Http` |
| `AddRedisCache` neben `UseDefaults()` wird vom eingebauten Zähler überschrieben | `UseRedisCache(prefix)`, `UseRedisTokenRevocation(...)`, `UseRedisSecurityAudit()` als Module |
| `InProduction(reason)` druckt die Zeichenzahl statt des Grundes | Grund im Wortlaut auf der Seite und im Ergebnis von `noelia.dashboard.operator-access` |

Der Ursprungs-Fund ist der mit der längsten Kette: Im Monolithen trug der
Refresh-Cookie `Secure`, hinter dem Gateway nicht. Der Grund lag zwei Sprünge
weiter — der Edge schickte `X-Forwarded-Proto`, das Gateway verbrauchte es, und
der Dienst dahinter sah wieder `http` und gab das Cookie **ehrlich** ohne
`Secure` aus. Ohne den Vergleich zweier Architekturen wäre das nicht aufgefallen.

### Demo

| Befund | Behebung |
|---|---|
| nginx-Header fehlen auf `/index.html`, JS und CSS | Ursache: `add_header` wird nicht in einen `location` mit eigenem `add_header` vererbt. `Cache-Control` ist jetzt eine `map`, die vier Header stehen einmal |
| Doppelte und widersprüchliche Header auf `/api/…` | ein Besitzer je Grenze: nginx für die Dateien, die Anwendung für `/api/` und `/noelia` |
| Privater JWT-Schlüssel als Umgebungsvariable | Docker-Secrets, gelesen über `KeyPerFile` aus `/run/secrets` |
| `Secure` aus `Request.IsHttps` hinter TLS-Proxy | gelöst durch die Noelia-Seite; über HTTP bewusst nicht gesetzt, und genau so geprüft |
| `.sln`: 163 Debug- gegen 141 Release-Zuordnungen, 11× `MSB4121` | 22 Zuordnungen ergänzt; Release-Build jetzt wirklich Release |
| „Erledigen" bleibt nach HTTP 500 deaktiviert | `finally` statt `catch` — `Page.guard()` löst auf, statt zu werfen |
| Keine echten Browser-E2E-Tests | sechs Playwright-Tests, gegen alle sechs Hosts |
| Interne Dienste auf `0.0.0.0` | kein Dienst veröffentlicht noch einen Port |
| `/swagger` und `/api-docs` liefern 404 | Modul war komponiert, Pipeline-Schritt fehlte; `/api-docs` in Development |
| Der Todo-Dienst prüft Token, sieht Widerrufe aber nicht | der Widerruf gilt für jeden **Prüfer**, nicht nur für den Aussteller |
| Das Release-Gate prüft die Security-Checks nicht | `eng/security-checks.py`; ein `Fail` ohne begründete Abnahme endet mit Exitcode 1 |
| Alte `Girder.*`-Namen in erzeugten Dateien | verschwinden mit dem sauberen Neuaufbau |
| Data-Protection-Schlüssel flüchtig | offen, siehe unten |

---

## 4. Abnahme

Alles unten gemessen am 16.09.2026, gegen den Kandidaten `5.1.0` aus dem
lokalen Vorrat.

| Grenze | Nachweis | Ergebnis |
|---|---|---|
| Noelia-Quellbaum | Release-Build, vollständige Tests | 0 Warnungen; 132 Core- und 3 210 Infrastructure-Tests |
| Trennung | Verweis-Wächter und Budgettest | grün; `Noelia.Redis` 14 → 15, README angeglichen |
| Demo-Build | `dotnet build -c Release` | 0 Warnungen, **kein** `MSB4121` |
| Demo-Tests | 15 Testprojekte inkl. 13 Paket-Probes | 61 Tests, alle grün |
| Frontend | jsdom-Einheitstests | 6 Tests |
| Browser | Playwright gegen sechs Hosts | 6 × 6 = 36 Tests, alle grün, keine Konsolenmeldung |
| Stack | `--profile all` | 15 Container, alle `healthy` |
| Adressen | sechs Hostnamen, HTTP-Umleitung, Dashboards | wie in Abschnitt 2 |
| Sicherheit | `eng/security-checks.py` | 0 unerlaubte Fehlschläge; 3 Warnungen, alle `revocation.degradation` in Development |
| Header | fünf Pfade, vier Header | überall identisch; auf `/api/` genau einer je Header |
| Cookie | Anmeldung über HTTP und HTTPS | `secure` genau dann, wenn der Transport es ist |
| Anbieter | Valkey nach echtem Verkehr | `rl:ip:…:min/hour/day` — die verteilte Bremse zählt wirklich |
| Geheimnisse | Scan aller eingecheckten Dateien gegen den privaten Schlüssel | kein Treffer |

Die drei Warnungen sind das Ergebnis, nicht ein Rest: Development führt eine
prozessinterne Widerrufsliste, eine zweite Replik sähe sie nicht, und der Check
sagt das, statt die Stufe wie Production aussehen zu lassen.

---

## 5. Offen

1. ~~**Data-Protection-Schlüssel** sind flüchtig und unverschlüsselt.~~
   In 5.2.0 behoben: `UseDataProtection(applicationName)` legt den Ring durch
   `IDistributedCacheService` ab und verschlüsselt ihn mit
   `IDataEncryptionService` — beides Noelia-Ports, also kein neues Paket. Der
   Check `noelia.dataprotection.key-ring` meldet den unversorgten Zustand.
2. **Keine zweite `ISovereignAuditSink`.** Die Prüfspur liegt in jeder Stufe im
   Prozess, also je Replik eine Kette. In 5.2.0 **sichtbar gemacht**, nicht
   behoben: `noelia.audit.chain-scope` meldet `Warning`, solange eine läuft.
   Eine zweite Senke allein genügt nicht — `AuditTrailService` schreibt die
   Kette aus einem eigenen Feld fort, zwei Repliken erzeugen also zwei
   verschränkte Ketten in einem Speicher, die schlechter sind als zwei
   getrennte. Die Security-Prüfspur in `Noelia.Redis` löst dasselbe bereits mit
   Compare-and-Set auf einem gemeinsamen Kopf; die Form der Antwort ist damit
   bekannt, die Arbeit steht aus.
3. **`--profile all` misst 15 Container.** Auf einer kleineren Maschine ist
   `staging` der erste Kandidat zum Weglassen.
4. **`InProduction` ist eine Verhaltensänderung in einer Nebenversion.** Nach
   der Regel in der README wäre es ein Major; die Begründung steht in
   [MIGRATION.md](../MIGRATION.md) und nicht in einer Fußnote.
