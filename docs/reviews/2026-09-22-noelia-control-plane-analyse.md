# Noelia und Noelia Control Plane – technische Analyse

Stand: 22.09.2026. Nur Analyse, keine Implementierung oder Veröffentlichung.

## Kurzurteil

Die Trennung ist sinnvoll: Noelia stellt modulare technische Bausteine und begrenzte Prozessbeobachtungen bereit; die Control Plane sammelt, historisiert und signiert diese Beobachtungen. Die aktuelle Umsetzung ist gegenüber 5.0 deutlich weiter. Sie ist aber noch keine belastbare Grundlage für das Versprechen einer vollständigen, dauerhaft belegten Sicherheits-/Souveränitätsaussage.

Die wichtigsten Blocker sind nicht optisch: Ein API-Response verrät Operator-Secrets; unvollständige Authentifizierung öffnet die Control Plane als Admin; Redirects umgehen die HTTP-Egress-Grenze; Audit-Trunkierung bleibt unerkannt; Signaturmetadaten und historische Aussagen sind nicht sauber an ihre Evidenz gebunden. Das ausgelieferte Control-Plane-Image startet ohne zusätzliche Konfiguration nicht.

„P1“ bedeutet hier: vor produktiver Exposition oder Vermarktung des betroffenen Versprechens beheben. „P2“ bedeutet: relevante Funktions-, Zuverlässigkeits- oder Sicherheitslücke. Das sind Review-Prioritäten, keine CVSS-Einstufungen und kein Nachweis eines erfolgten Angriffs.

## 1. Prüfstand und Änderungen seit dem bisherigen Stand

- Noelia: /Users/davidozturk/Projects/Noelia, main, cf54577071cffbe15bc8231047117d4bf7c44a80.
- Vergleichspunkt: b424aa8, bisheriger 5.0-Stand. Seitdem 36 Commits, darunter 28 Nicht-Merge-Commits; 271 geänderte Dateien, +23.215/-1.590 Zeilen.
- Control Plane: /Users/davidozturk/Projects/NoeliaControlPlane, main, 11304ca415f7ca9bfa4ee0e69e014b91080aedab; vollständige Historie mit 18 Commits.
- Beide lokalen HEADs wurden mit dem Remote-Stand abgeglichen.
- Aktive Demo: /Users/davidozturk/Projects/Noelia/demo. Das frühere /Users/davidozturk/Projects/Demo ist historisch und keine zweite aktuelle Referenz.
- ContosoInvoicing wurde als separater NuGet-Verbraucher statisch untersucht. WorkerTransfer blieb ausgenommen.
- Die vorhandene unversionierte appsettings.WorkerTransfer.json in der Control Plane wurde nicht geöffnet oder verändert.

### Neue PRs

Alle acht sind gemergt; in dieser Reihe ist kein offener PR übrig.

| PR | Inhalt |
|---|---|
| [21](https://github.com/DavidOeztuerk/noelia/pull/21) | 5.1: Demo im Repository, drei Umgebungen, vorherige Fehlannahmen korrigiert |
| [22](https://github.com/DavidOeztuerk/noelia/pull/22) | 5.2: Data Protection, Modulverträge, gemeinsame Auditkette |
| [23](https://github.com/DavidOeztuerk/noelia/pull/23) | 5.3: Transactional Outbox, Fehlertexte; Session-/Logout-Korrekturen |
| [24](https://github.com/DavidOeztuerk/noelia/pull/24) | 6.0: OperatorReport und lesbare/verifizierbare Auditspur |
| [25](https://github.com/DavidOeztuerk/noelia/pull/25) | 6.1: Egress-Check und Provider-Domain-Erkennung |
| [26](https://github.com/DavidOeztuerk/noelia/pull/26) | 6.2: lokale Zeitdarstellung |
| [30](https://github.com/DavidOeztuerk/noelia/pull/30) | 6.3: Dateipfade und SaaS-Klassifikation |
| [31](https://github.com/DavidOeztuerk/noelia/pull/31) | 6.4: regulatorische Referenzen, KI-Prüfungen, CLI und Dashboard-Sektionen |

Die Control Plane hat ihre 18 Commits direkt auf main und keine PR-Historie. Beide jüngsten CI-Läufe sind grün. [Release 6.4.0](https://github.com/DavidOeztuerk/noelia/releases/tag/v6.4.0) ist vorhanden; der Publish-Lauf für ccff4d2 war erfolgreich. Der anschließende Commit cf54577 korrigiert den CLI-Umgang mit .localhost und ist nicht Teil dieses Publish-Laufs.

Claude steht bei den 28 neuen Nicht-Merge-Commits von Noelia sowie den 18 Control-Plane-Commits als Co-Author. Der Hauptautor ist weiterhin David. Das ist Metadaten-Attribution, kein Qualitätsmerkmal des Codes. Es wurde keine Historie umgeschrieben.

## 2. Was tatsächlich ausgeführt wurde

| Prüfung | Ergebnis |
|---|---|
| Noelia Release-Tests | 3.461 bestanden, 0 Fehler, 0 übersprungen |
| Control-Plane-Tests | 69 bestanden, 0 Fehler, 0 übersprungen |
| Demo .NET einschließlich Paketproben | 61 bestanden |
| Frontend-Unit-Tests | 6 bestanden |
| Aktuelle Compose-Demo | 15 Container healthy; Monolith/Microservices × Dev/Staging/Production |
| Security-Log-Gate | Keine nicht akzeptierten Fehler; vier Development-Warnungen zur prozesslokalen Auditkette |
| Bestehende Browser-E2E, sechs Kombinationen | Im ersten Durchlauf 57/60 bestanden |
| Wiederholung der drei Fehltests | 3/3 bestanden – ursprüngliche Fehler deshalb nicht verschwiegen |
| Dashboard-Navigation mit echtem Chromium/Playwright | 182 Aufrufe, anschließend 24 History-/Attestation-Seiten; HTTP 200, keine protokollierten Browser-Warnungen/Fehler |
| CSV-/Key-Endpunkte | Vier zusätzliche Requests erfolgreich |
| Control-Plane-Dockerimage aus Git-HEAD | Build erfolgreich, Standardstart scheitert mit SQLite Error 14 |

Die drei ersten E2E-Fehler: Registrierung über micro-dev mit protokollierter Ocelot RegexMatchTimeoutException; früher Logout in micro-dev und mono-staging. Der Logout-Timingfehler wurde anschließend deterministisch nachgewiesen. Die genaue Ursache des einmaligen Ocelot-Timeouts ist noch nicht abschließend isoliert; er trat bei gleichzeitig laufenden Prüfungen auf.

Diese grünen Tests sind kein Sicherheitszertifikat. Insbesondere testen die bestehenden 69 CP-Tests keine echte OIDC-Anmeldung, keinen vollständigen HTTP-Autorisierungsdurchlauf und keinen Containerstart.

### Praktische Gegenproben

Nur lokale Ziele und synthetische Daten, keine fremden Dienste angegriffen:

- Konfiguriertes echtes Demo-Operator-Secret im Response nur auf Gleichheit geprüft, nie ausgegeben.
- OIDC-Claim-Mapping der unveränderten Implementierung aufgerufen.
- Erlaubtes HTTP-Ziel auf ein gesperrtes lokales Ziel umgeleitet; zusätzlich 307 mit synthetischem POST-Body.
- Collector-Redirect mit einem ausdrücklich synthetischen Canary-Header.
- Fehlerhaftes JSON eines Flottenmitglieds neben einem gültigen Mitglied.
- Erste, letzte und alle Auditzeilen ausschließlich in einer isolierten Testkette entfernt.
- Fingerprint einer isoliert signierten Attestation ersetzt.
- Frischen View mit älterem History-Snapshot kombiniert.
- Zwei echte KeyRing-Repositories mit kontrolliert gleichzeitigem Indexzugriff.
- Echter InMemory-Cache mit vorgerückter Test-Uhr.
- Logout während absichtlich verzögerter Session-Erneuerung im Browser.

## 3. Priorisierte Befunde

### R01 · P1 · Operator-Secrets werden an Leser ausgegeben

**Bestätigt, echter HTTP-Response.** GET /fleet.json?fleet=mono-prod liefert ohne Anmeldung in der lokalen Production-Testinstanz HTTP 200 und enthält das konfigurierte Operator-Secret. Auch mit korrekt eingerichteter OIDC-Authentifizierung läge der Endpunkt nur unter der Reader-Policy.

FleetMember enthält OperatorSecret als serialisierbare Property; FleetView reicht das Objekt über Answers/Reachable/Unreachable an Results.Ok weiter. Dadurch erhält ein Leser Zugangsdaten der abgefragten Dienste. Bei global gemeinsamem Secret reicht die Auswirkung möglicherweise über die sichtbare Flotte hinaus.

**Anders:** interne Credentials strikt von öffentlichen Report-DTOs trennen; Secret-Freiheit als Regressionstest; no-store. Falls eine betroffene Instanz bereits anderen zugänglich war, betroffene Schlüssel nach der Korrektur rotieren und Zugriff prüfen. Keine Rotation wurde eigenständig vorgenommen.

Quellen: [FleetCollector.cs:15](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetCollector.cs:15), [Program.cs:157](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Program.cs:157), [FleetView.cs:23](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetView.cs:23).

### R02 · P1 · Fehlende oder unvollständige Auth-Konfiguration öffnet als Admin

**Bestätigt, Code und Production-Lauf.** Authority UND ClientId müssen gesetzt sein; fehlt eines, wird automatisch LocalOperator aktiviert. Dieser authentifiziert jeden Besucher als Admin aller Flotten. Es gibt weder Development-Beschränkung noch Loopback-Prüfung noch explizites Opt-in. Das Dockerimage lauscht auf allen Containerinterfaces.

Eine Lizenzbindung ist keine Zugriffsbarriere. Bei gesetzter Authority und fehlender ClientId wird die konfigurierte Authority sogar unabhängig vom aktiven Auth-Modus zur Lizenzprüfung verwendet. Die dokumentierte deutlich sichtbare Warnung auf jeder Seite fehlt; „admin“ ersetzt diese Warnung nicht.

**Anders:** unvollständige Auth-Konfiguration als Startfehler behandeln; lokalen Evaluationsmodus ausdrücklich aktivieren und auf Loopback/Development begrenzen.

Quelle: [Access.cs:86](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Access.cs:86), [Access.cs:250](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Access.cs:250).

### R03 · P1 · OIDC-Gruppenmapping funktioniert nicht zuverlässig

**Deterministisch reproduziert.** Map enumeriert Claims und fügt derselben Identity währenddessen neue Claims hinzu. Bei einer tatsächlich zugeordneten Gruppe entsteht InvalidOperationException. Daneben suchen RequireRole/IsInRole anhand von options.RoleClaim, während die erzeugten Rollen unter ClaimTypes.Role abgelegt werden. Im Harness: Rollenclaim vorhanden, IsInRole("admin") dennoch false.

**Anders:** externe Gruppen zuerst materialisieren; normalisierte interne Identity mit festem RoleClaimType erzeugen. Echte IdP-/Callback-Tests für mehrere Gruppen, keine Zuordnung und Rollen pro Flotte ergänzen. Ein kompletter externer IdP-Login wurde hier nicht durchgeführt.

Quelle: [Access.cs:160](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Access.cs:160), [Access.cs:197](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Access.cs:197).

### R04 · P1 · Redirects umgehen die Egress-Prüfung und können Collector-Secrets mitnehmen

**Beides reproduziert.** Noelia prüft nur die ursprüngliche URI im DelegatingHandler. Automatische Redirects erfolgen darunter im HTTP-Transport. Ein direkt blockiertes Ziel wurde über einen erlaubten Ursprung erreicht; bei 307 erreichte auch der synthetische POST-Body das verbotene Ziel.

Die Control Plane hat denselben Transportmechanismus und hängt X-Noelia-Operator an. Ein Redirect auf einen anderen lokalen Hostnamen erhielt den Canary-Header. Das ist kein Beweis gegen beliebige interne Collector-Adressen; der konkrete Risikopfad ist ein umleitender bzw. kompromittierter konfigurierter Dienst.

**Anders:** automatische Redirects deaktivieren oder jeden Hop explizit gegen Ziel-/Origin-Policy prüfen; Credentials niemals blind zur nächsten Origin mitnehmen. Für prozessweite Egress-Garantien zusätzlich kontrollierten Proxy/Netzwerkregeln verwenden.

Quellen: [EgressGuardHandler.cs:23](/Users/davidozturk/Projects/Noelia/src/Noelia.Infrastructure/Sovereignty/EgressGuardHandler.cs:23), [FleetCollector.cs:110](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetCollector.cs:110).

### R05 · P1 · „Vollständige Liste erreichbarer Hosts“ ist nicht belegt

**Code und Oberfläche geprüft.** Die Seite erklärt „Complete, not estimated“ und leitet aus fehlenden Einträgen Unerreichbarkeit ab. Tatsächlich sind DeclareDependency, ConnectionStrings und Allow getrennte Informationsquellen. Erlaubte Hosts müssen nicht im Dependency-Inventar erscheinen; Loopback/private Netze sind standardmäßig breit erlaubt. Direkte Clients, SDKs, Datenbanktreiber und andere Netzwerkpfade werden durch einen IHttpClientFactory-Handler nicht automatisch kontrolliert.

Zusätzlich prüft EgressIsEnforcedEverywhere nur erreichbare Mitglieder. Fehlende Dienste können weitere Ziele haben. Lizenzbedingt abgeschnittene Dienste fehlen im signierten Modell; bei gültiger Lizenz wird sogar der HTML-Hinweis auf ausgelassene Dienste durch einen frühen Return unterdrückt. Private Hostnamen/IPs beweisen ferner weder Betreiber noch Rechtsraum.

**Anders:** getrennte Felder für deklarierte Abhängigkeiten, erlaubte Ziele/Netzbereiche und tatsächlich beobachtete Aufrufe. Dazu configured/observed/unreachable/omitted, Messzeit, Kontrollumfang und Unknown/Partial. Ein vollständiger Nachweis braucht einen vollständigen Scope und eine tatsächlich durchgesetzte Grenze.

Quellen: [SovereigntyReport.cs:32](/Users/davidozturk/Projects/Noelia/src/Noelia.Infrastructure/Sovereignty/SovereigntyReport.cs:32), [SovereignPlatformBuilder.cs:17](/Users/davidozturk/Projects/Noelia/src/Noelia.Infrastructure/Builder/SovereignPlatformBuilder.cs:17), [FleetView.cs:55](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetView.cs:55), [FleetPage.cs:256](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetPage.cs:256).

### R06 · P1 · Audit-Verifikation erkennt Abschneiden nicht

**Reproduziert.** Vollständige Testkette sowie Restketten ohne ersten, letzten oder sämtliche Einträge ergeben jeweils IsIntact=true und IsPartial=false. Der Verifier prüft Hashes und innere Verbindungen, aber keinen erwarteten Genesis-/Endpunkt und keine unabhängig erwartete Anzahl.

Eine mathematisch konsistente Restkette ist kein Nachweis der Vollständigkeit. Ein unverschlüsselter Hash allein schützt auch nicht gegen einen Schreiber, der die komplette Historie neu berechnet; diese Einschränkung wird an anderer Stelle immerhin dokumentiert.

**Anders:** Konsistenz und Vollständigkeit getrennt ausweisen; Sequenzen, erwarteten Genesis/Head und unabhängig gehaltene Checkpoints prüfen; konsistente Snapshots für konkurrierende Writes. Niemals ohne Zusatzbeleg aus „keine inneren Brüche“ auf „nichts fehlt“ schließen.

Quelle: [AuditChainVerifier.cs:56](/Users/davidozturk/Projects/Noelia/src/Noelia.Infrastructure/Audit/AuditChainVerifier.cs:56).

### R07 · P1 · Fingerprint ist austauschbar, obwohl Verify gültig meldet

**Reproduziert, auch am HTTP-Endpunkt.** Ein beliebig ersetzter Signature.KeyFingerprint wird mit valid=true zurückgemeldet. Die eigentliche Signaturprüfung verwendet den beigefügten PublicKey korrekt; der Fingerprint wird jedoch nicht daraus abgeleitet oder gegen ihn geprüft.

Verifikation gegen einen eingebetteten Schlüssel ist ausdrücklich beabsichtigte Integritätsprüfung und an sich kein ECDSA-Fehler. Aber eine beliebige Person kann selbst signieren und den behaupteten Fingerprint einer anderen Installation eintragen. So wird gerade die manuelle Vertrauensprüfung irreführend.

**Anders:** Fingerprint selbst berechnen; Signaturgültigkeit, unterstütztes Format und vertrauenswürdigen Aussteller getrennt beantworten; vertrauenswürdige Schlüssel unabhängig vom Dokument beziehen. Algorithmus/Curve und Key-Rotation vertraglich festlegen.

Quelle: [Attestation.cs:338](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Attestation.cs:338), [Program.cs:371](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Program.cs:371).

### R08 · P1 · „Unverändert seit“ gehört nicht zwingend zum signierten Zustand

**Erster Fall im Harness reproduziert, weitere Fälle statisch.** Die Attestation kombiniert einen frisch abgefragten View mit UnchangedSince aus einem unabhängigen älteren Poll. Ändert sich der Zustand dazwischen, bekommt die neue Aussage das Alter der alten. MAX(since) über derzeitige Fakten bildet außerdem Beobachtungslücken nicht ab und kann nach Entfernen der jüngsten Tatsache zurückspringen.

**Anders:** exakt den persistierten, versionierten Snapshot signieren; Snapshot-Digest, firstSeen, lastConfirmed, Abfragefehler und Lücken erfassen. Stichproben können „bei unseren Beobachtungen unverändert“ belegen, nicht unterbrechungslose Wahrheit dazwischen.

Quelle: [Program.cs:350](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Program.cs:350), [FleetHistory.cs:208](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetHistory.cs:208).

### R09 · P1 · Schlüsselring verliert Einträge durch Nebenläufigkeit und Ablaufzeit

**Zwei konkrete Gegenproben.**

1. Zwei echte NoeliaXmlRepository-Instanzen lesen denselben Indexstand und schreiben unabhängig zurück: zwei Elemente gespeichert, nur eines über den Index lesbar. Kontrollierter serialisierender Test-Cache, kein Redis-Lasttest.
2. „No expiry“ wird mit SetAsync ohne Ablaufzeit implementiert. Der echte InMemory-Provider setzt dann 30 Minuten. Nach 31 Minuten Testzeit ist der lesbare Schlüsselring leer.

Das bedeutet nicht, dass jede bereits laufende DataProtection-Instanz genau nach 30 Minuten sofort ausfällt; intern gecachte Schlüssel können weiterleben. Neu lesende Instanzen/Neustarts erhalten jedoch nicht mehr denselben Ring. Auch Widerrufselemente benötigen zuverlässige Speicherung.

**Anders:** dedizierter persistenter KeyRing-Storage mit atomarem Append, nicht bloß generischer Cache; expliziter identischer Vertrag für unbegrenzte Aufbewahrung. Replica-, Rotation-, Revocation- und Restart-Tests.

Quellen: [DataProtectionKeyRing.cs:168](/Users/davidozturk/Projects/Noelia/src/Noelia.Infrastructure/Security/Keys/DataProtectionKeyRing.cs:168), [InMemoryDistributedCacheService.cs:80](/Users/davidozturk/Projects/Noelia/src/Noelia.InMemory/Caching/InMemoryDistributedCacheService.cs:80).

### R10 · P1 · Control-Plane-Dockerimage baut, startet aber standardmäßig nicht

**Mit sauberem Git-HEAD-Image reproduziert.** Non-root-Prozess, WORKDIR /app, relativer HistoryPath fleet-history.db. Nur SigningKeyPath liegt im beschreibbaren Volume. Ergebnis: SQLite Error 14, „unable to open database file“. Die lokale Analyseinstanz funktioniert, weil dafür ausdrücklich ein beschreibbarer temporärer HistoryPath konfiguriert wurde.

**Anders:** History und Schlüssel auf vorgesehene persistente Pfade legen; nicht nur Build, sondern Start, Schreibzugriff und Restart im Image-Smoke-Test prüfen. OIDC-DataProtection-Keys müssen ebenfalls einen bewussten Persistenzvertrag bekommen.

Quelle: [Dockerfile:29](/Users/davidozturk/Projects/NoeliaControlPlane/Dockerfile:29), [FleetCollector.cs:186](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetCollector.cs:186).

### R11 · P1/P2 · Demo-Sicherheitszustand ist flüchtig; Reset-Anleitung löscht zu viel

**Konfiguration und Anleitung geprüft; kein destruktiver Reset ausgeführt.** Valkey in Staging/Production deaktiviert Snapshot und AOF und hat keine persistente Datenablage. Dort liegen nicht nur Cacheeinträge, sondern Audit, Revocations, DataProtection und weitere Sicherheitszustände. Die README empfiehlt außerdem FLUSHALL gegen diese gemeinsamen Instanzen zum Zurücksetzen von Rate-Limits.

**Anders:** Testläufe durch eigene Instanzen/Namespaces isolieren, nur konkrete Rate-Limit-Zustände gezielt zurücksetzen. Persistenz-/Restore-Szenario für Sicherheitsdaten zeigen. Wenn bewusst flüchtig: deutlich „ephemere Demo, kein Betriebsnachweis“ nennen.

Quellen: [docker-compose.yml:191](/Users/davidozturk/Projects/Noelia/demo/docker-compose.yml:191), [README.md:145](/Users/davidozturk/Projects/Noelia/demo/README.md:145).

### R12 · P1/P2 · Demo inventarisiert falsche Abhängigkeiten und lässt Gateways aus

**Statisch und in Reports nachvollzogen.** DemoProviders deklariert pauschal Sessions/sqlite für alle Hosts, auch Todo und Gateway ohne entsprechende Session-Datenbank. Die tatsächlichen Gateway-Ziele User/Todo fehlen dagegen in dieser Deklaration. In den Staging-/Production-Control-Plane-Flotten fehlt das Gateway ganz, weil dort kein Dashboard angeboten wird.

**Anders:** tatsächliche Provider-/Feature-Registrierung als Ausgangspunkt des Inventars; Negativtests gegen erfundene Dependencies. Maschinenreport und optionale HTML-Oberfläche trennen, damit ein Gateway ohne öffentliches Dashboard trotzdem vollständig inventarisiert werden kann.

Quelle: [DemoProviders.cs:75](/Users/davidozturk/Projects/Noelia/demo/src/shared/Demo.Platform/DemoProviders.cs:75).

### R13 · P2 · Ein ungültiger Service-Report kippt die ganze Erhebung

**Ungültiges JSON reproduziert.** AskAsync fängt HTTP- und Timeoutfehler, aber keine JsonException. Task.WhenAll verwirft dadurch auch die gültigen Antworten des gleichen Durchlaufs. Zusätzlich fehlen definierte Antwortgrößen sowie Abgleich von SchemaVersion, erwarteter Serviceidentität und Frische.

**Anders:** Fehler je Mitglied isolieren; unterstützte Schemas und vollständige Pflichtfelder validieren; Streaming/Größenlimits; Identitäts- und Zeitkonflikte als eigene Evidenzzustände statt still akzeptieren.

Quelle: [FleetCollector.cs:60](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetCollector.cs:60).

### R14 · P2 · Alarmierung hat keine zuverlässige Zustellung und keine laufende Kettenprüfung

**Statisch.** Die Historie wird vor dem Webhook geschrieben; bei 500/Netzfehler gibt es keinen dauerhaften Zustellauftrag und keinen Retry. Der nächste Poll sieht dieselbe Tatsache und erzeugt keinen neuen Übergang. Ein nicht durch Shutdown verursachter Timeout kann spätere Alarmrouten verhindern.

Kettenalarmierung ist zusätzlich nicht verdrahtet: Der Recorder sammelt immer mit verifyChains:false. Manuelle Prüfungen werden nicht in seine History zurückgeschrieben. Normale chain-Routen erhalten so keine Kettenänderungen.

**Anders:** persistente Alarm-Outbox mit Backoff/Idempotenz und sichtbar fehlerhaften Zustellungen; separate begrenzte, regelmäßig gespeicherte Kettenprüfung.

Quellen: [FleetRecorder.cs:70](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetRecorder.cs:70), [Alerts.cs:186](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Alerts.cs:186).

### R15 · P2 · Entfernte Fehler werden als Heilung behandelt

**Statisch.** Jede Transition AUS Fail/BROKEN/silent gilt als Recovery, unabhängig vom neuen Zustand. Ein verschwundener fehlerhafter Check kann dadurch als grüne Besserung erscheinen.

**Anders:** explizite erlaubte Zustandsübergänge nach Pass/Healthy/Intact; „gone“ als Verlust von Coverage, nicht Heilung behandeln.

Quelle: [Severity.cs:40](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Severity.cs:40), [Alerts.cs:66](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Alerts.cs:66).

### R16 · P2 · Reader können teure Kettenprüfung auslösen

**Statisch.** ?verify=1 auf Fleet-HTML und /fleet.json liegt nur unter Policies.Read; ein per-Fleet-CanOperate fehlt. Das ist mehr als Darstellung: Jeder Aufruf kann vollständiges Nachrechnen in allen Diensten anstoßen.

**Anders:** serverseitige Operator-Autorisierung, begrenzte Prüffrequenz und Zusammenfassung paralleler Requests.

Quelle: [Program.cs:109](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Program.cs:109).

### R17 · P2 · Lizenzvertrag, Laufzeit und Preisplan stimmen nicht überein

**Statisch.** Lizenzstatus wird als Singleton zum Startzeitpunkt berechnet, läuft im laufenden Prozess also nicht automatisch ab. Das dokumentierte globale Servicebudget wird pro Flotte angewendet; Recorder sammelt ohne dieses Budget. Alert-Routen sind free=1, jede gültige Lizenz=unbegrenzt, anders als im Preisplan. Die Issue-CLI prüft eine Authority-gebundene Lizenz anschließend ohne deren Authority und kann sie nach Ausgabe als falsch zurückweisen.

**Anders:** zentrale Entitlement-Policy mit aktueller Uhr und globalem Budget; bewusst entscheiden, welche Beobachtung frei bleiben soll; Token vor Ausgabe korrekt selbstvalidieren. Preispunkte sind derzeit Produktannahmen, keine validierte Nachfrage.

Quellen: [Program.cs:20](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Program.cs:20), [Licence.cs:166](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Licence.cs:166), [LicenceCommand.cs:78](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/LicenceCommand.cs:78).

### R18 · P2 · Historische Attestationsformate sind nicht dauerhaft verifizierbar zugesichert

**Statisch.** Die Kommentare versprechen Verifikation von v1. Das aktuelle Content-Modell verlangt jedoch neu hinzugefügte Statement/Obligations. Alte Dokumente scheitern damit bereits an aktuellen Pflichtfeldern; bloßes Optionalmachen verändert wiederum die neu serialisierten signierten Bytes. SchemaVersion ist außerhalb des signierten Content und wird nicht zur Verifikationsauswahl verwendet.

**Anders:** unveränderliche versionierte Signatur-Payloads, echtes Schema-Dispatching und dauerhaft eingecheckte historische Testvektoren. Cross-Language-Verifikation testen, bevor die Dokumente als langfristige Evidenz verkauft werden.

Quelle: [Attestation.cs:24](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Attestation.cs:24).

### R19 · P2 · CSV-Ausgabe schützt nicht vor Formelauswertung

**Statisch, nicht in Excel ausgeführt.** Export quotiert Kommas/Anführungszeichen, neutralisiert aber keine formelartigen freien Felder aus Reports. CSV-Quoting ist keine Festlegung auf den Zelltyp Text.

**Anders:** geeignete Formelneutralisierung für den vorgesehenen Tabellenclient oder Export mit expliziten String-Zellen; unveränderte Rohdaten separat als JSON anbieten.

Quelle: [Export.cs:119](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Export.cs:119).

### R20 · P2 · KI-Checks schließen mehr, als sie beobachten

**Statisch und Demo-Ergebnisse.** Die Erkennung bekannter Hostnamen nennt ihre Grenzen teilweise korrekt. Trotzdem wird aus „kein bekannter Modellhost“ an anderer Stelle „keine Aufzeichnungspflicht“ abgeleitet. Eine lokale/anders benannte KI fällt durch die Heuristik. Die Registrierung eines lesbaren Audit-Sinks beweist außerdem nicht, dass tatsächliche Modellaufrufe erfasst werden.

**Anders:** Beobachtung, Konfigurationsprüfung und fachliche Bewertung getrennt modellieren. KI-Nutzung explizit deklarierbar machen; unbekannt bleibt unbekannt. Automatische Aufzeichnung erst behaupten, wenn ein Modellaufruf tatsächlich einen entsprechenden Auditbeleg erzeugt hat. Das ist keine juristische Vollprüfung. Der zwischenzeitliche Verdacht eines falschen allgemeinen AI-Act-Datums wurde nicht als Befund übernommen; aktuelle Angaben wurden gegen die [EU-Kommissionsseite](https://digital-strategy.ec.europa.eu/en/policies/regulatory-framework-ai) geprüft.

Quelle: [ArtificialIntelligenceChecks.cs:205](/Users/davidozturk/Projects/Noelia/src/Noelia.Infrastructure/Security/Checks/ArtificialIntelligenceChecks.cs:205).

### R21 · P2 · Outbox kann hinter dauerhaft fehlerhaften Nachrichten verhungern

**Statisch.** Claim wählt die ältesten noch offenen Einträge. Release macht einen Fehler sofort wieder claimbar; AttemptsBeforeAlarm ändert nur die Logstufe. Ein ganzes Batch alter dauerhaft fehlerhafter Nachrichten kann dadurch neuere zustellbare Nachrichten unbegrenzt verdrängen.

**Anders:** nextAttemptAt, Backoff und sichtbare Quarantäne/Dead-Letter-Zustände ohne stilles Löschen. Zusätzlich Lease-Owner/Fencing und Idempotenz unter konkurrierenden Dispatchern prüfen; dieser Nebenläufigkeitsaspekt wurde nicht als eigener reproduzierter Fehler gewertet.

Quellen: [EntityFrameworkOutbox.cs:82](/Users/davidozturk/Projects/Noelia/src/Noelia.Data.EntityFrameworkCore/Messaging/EntityFrameworkOutbox.cs:82), [OutboxDispatcher.cs:123](/Users/davidozturk/Projects/Noelia/src/Noelia.Infrastructure/Messaging/OutboxDispatcher.cs:123).

### R22 · P2 · Control Plane hat schwächere eigene Schutz- und Evidenzstandards

**Code und HTTP-Header geprüft.** Eigene Antworten haben keine CSP/no-store/Anti-Framing-Vorgaben; kein eigener Audittrail dokumentiert Nachweiserstellung und Downloads. OIDC-DataProtection-Persistenz ist im Container nicht eingerichtet. AllowUntrustedCertificates ist auch in Production möglich; dieser Verlust der Transportauthentizität steht nur im Log, nicht im signierten Ergebnis.

**Anders:** dieselben Anforderungen an den Sammler wie an beobachtete Services; Schutzheader, Zugriffsaudit, persistente Schlüssel, eigener Betriebsreport; Authentizitätsqualität signiert mitführen. Ein vorgeschalteter Proxy kann Teile liefern, ist aber kein Ersatz für einen vollständigen ausgelieferten Betriebsvertrag.

Quelle: [Program.cs:56](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/Program.cs:56).

### R23 · P2 · Release-Gates decken das reale Produkt nicht ab

**Statisch und durch die Laufzeitfehler illustriert.** Noelia-CI baut Paketkandidaten und führt .NET-/JS-Unit-Tests aus, aber keine Compose-/TLS-/Browser-/Security-Matrix. Publish prüft Noelia selbst, ist aber nicht an eine vollständige Verbraucherabnahme gebunden. CP-CI baut das Image, startet es nicht.

Das alte Demo-Docker-Gate nennt eine entfernte Compose-Datei, alte Ports, eine feste 13er-Paketanzahl und startet ohne die heutigen Profile. Es ist keine aktuelle Release-Abnahme.

**Anders:** ein verbindlicher Kandidatenlauf über Architektur × Stage, mit tatsächlichen Paketen, Containern und Browser; Publish nur für exakt diesen geprüften Commit/Artefaktsatz.

Quellen: [ci.yml:31](/Users/davidozturk/Projects/Noelia/.github/workflows/ci.yml:31), [test-docker-health.sh:46](/Users/davidozturk/Projects/Noelia/demo/eng/test-docker-health.sh:46).

### R24 · P2 · Security-Gate kann fehlende Services/Checks übersehen

**Statisch.** Das Skript verlangt lediglich mindestens ein gefundenes Ergebnis und bewertet dann nur die gefundenen Logzeilen. Ein komplett fehlender Dienst oder erwarteter Check ist kein expliziter Fehler. Historische und aktuelle Logergebnisse haben keine verbindliche Run-ID.

**Anders:** erwartete aktuelle Services und Check-IDs aus der Composition gegen vollständige abgeschlossene Runs prüfen; fehlende/frühere Ergebnisse als Fehler oder Unknown behandeln.

Quelle: [security-checks.py:133](/Users/davidozturk/Projects/Noelia/demo/eng/security-checks.py:133).

### R25 · P2 · Zwei Browser-/Routingfehler und ein offener Timeoutbefund

**Logout bestätigt:** Abmelden ist sofort enabled, der Listener kommt erst nach requireSession. Beim Klick während verzögerter Refresh-Antwort wurden null Logout-Requests gesendet; nach Initialisierung blieb die Person angemeldet. Zweiter Klick funktioniert.

**Redirect bestätigt:** HTTP auf einem Staging-/Production-Host sendet 308 nach https://host/ auf Port 443, obwohl Compose TLS auf 8443 veröffentlicht.

**Ocelot offen:** normaler Registrierungsrequest scheiterte einmal mit RegexMatchTimeoutException im RegExUrlMatcher unter paralleler Prüflast; Wiederholung erfolgreich. Kein belegter ReDoS, keine abschließend bewiesene Ursache.

**Anders:** UI-Aktionen bis zur tatsächlichen Bereitschaft deaktivieren; externen TLS-Port korrekt abbilden; Last-/Kaltstart-Test samt Gatewaydiagnostik ergänzen.

Quellen: [index.html:18](/Users/davidozturk/Projects/Noelia/demo/src/frontend/index.html:18), [dashboard.js:89](/Users/davidozturk/Projects/Noelia/demo/src/frontend/js/pages/dashboard.js:89), [nginx.conf:139](/Users/davidozturk/Projects/Noelia/demo/src/frontend/nginx.conf:139).

### R26 · P2 · Production-Demo beweist keine dauerhafte fachliche Datenhaltung

**Statisch.** User und Todos bleiben InMemory, während Refresh-Sessions persistent sein können. Nach Prozessneustart fehlen Benutzer/Domain-Daten, aber Sessionzustand kann noch existieren. Die Implementierung ist als Demo erläutert; der Name Production darf daraus keinen Betriebsnachweis machen.

**Anders:** entweder echten persistenten Fachprovider für dieses Szenario zeigen oder die ephemere Grenze sichtbar machen. Restart-Verhalten explizit abnehmen.

Quelle: [Registration.cs:19](/Users/davidozturk/Projects/Noelia/demo/src/services/UserService/UserService.Infrastructure/Registration.cs:19).

### R27 · P2/P3 · Zu breite Proxy-Vertrauenszone

**Statisch.** DemoEnvironment vertraut allen RFC1918-Bereichen. Die Begründung, nur der Edge könne sich verbinden, stimmt im gemeinsam genutzten Compose-Netz nicht: Auch andere Container können Verbindungen und Header senden.

**Anders:** tatsächlich benötigte Proxy-IP/Subnetze und Netzwerksegmente; Vertrauenskette Edge→Gateway→Service ausdrücklich testen. Für die lokale Demo eine bewusste Einschränkung, kein ungeprüft zu kopierendes Production-Muster.

Quelle: [DemoEnvironment.cs:66](/Users/davidozturk/Projects/Noelia/demo/src/shared/Demo.Platform/DemoEnvironment.cs:66).

### R28 · P2/P3 · Unterschiedliche Module sind nicht automatisch Drift

**Produkt-/Semantikbefund.** Der Flottenvergleich meldet unterschiedliche Modulsätze. Gateway, User-Service und Todo-Service sollen aber unterschiedliche Module verwenden. Ohne Soll-Komposition pro Dienstrolle oder zeitliche Baseline sind viele dieser Unterschiede bestimmungsgemäß.

**Anders:** „Composition differences“ für den Quervergleich; echte Drift nur gegen deklarierte Erwartung oder vorherigen passenden Snapshot. Andernfalls produziert gerade die gewollte Modularität unnötige Alarme.

Quelle: [FleetView.cs:164](/Users/davidozturk/Projects/NoeliaControlPlane/src/Noelia.ControlPlane/FleetView.cs:164).

## 4. Passt das zu eurer Vision?

### Was bereits passt

- Monolith und Microservice-Anwendung nutzen dieselben Fachbausteine. Nginx vor dem Monolith ist ein Edge/Webserver, kein notwendiges fachliches Gateway; Ocelot ist dafür nicht erforderlich.
- Die Demo konsumiert NuGet-Pakete statt bequem die Framework-Projekte direkt zu referenzieren. Das bleibt eine wichtige echte Verpackungsgrenze.
- Einzelpaketproben existieren und prüfen Modulkomposition. Das Dashboard baut auf einem gemeinsamen Reportmodell auf.
- TLS, Cookieflags, Headerzuständigkeiten, Secret-Dateien, konkrete Healthchecks und Dashboard-Zugriffsaudit wurden gegenüber früher verbessert.
- Browser-E2E existieren inzwischen. Die frühere Aussage „keine Browsertests“ wäre heute falsch.
- Pull statt Telemetrie zu einem fremden Anbieter und Offline-Lizenzierung passen zum selbst gehosteten Produkt.
- Einschränkungen wie „keine Zertifizierung“ und Abhängigkeit von wahrheitsgemäßen Dienstangaben stehen teilweise ausdrücklich im Code/Dokument.

### Wo die Richtung korrigiert werden sollte

Noelia sollte nicht suggerieren, aus registrierten Interfaces und Hostnamen den gesamten Netzwerkverkehr oder die Rechtslage beweisen zu können. Die Control Plane sollte diese begrenzten Informationen nicht zu stärkeren absoluten Aussagen hochstufen.

Der zentrale Produktvertrag sollte lauten:

1. **Beobachtung:** Was wurde wann und aus welcher Quelle gesehen?
2. **Prüfung:** Welche konkrete Eigenschaft wurde wie getestet?
3. **Abdeckung:** Was fehlt, ist unbekannt, nicht erreichbar oder außerhalb der Kontrolle?
4. **Evidenz:** Welcher unveränderliche Snapshot wird mit welchem vertrauenswürdigen Schlüssel belegt?

Health bleibt Erreichbarkeit/Betriebsfähigkeit. Securitychecks prüfen konkrete Schutzkonfigurationen/-verhalten. Inventar ist eine eigene Art Beobachtung. Regulatorische Zuordnung unterstützt eine menschliche Bewertung, ersetzt sie nicht.

Contoso ist als fremder Verbraucher wertvoll: Er fand laut README reale Onboardingfehler, die die eigene Demo nicht sah. Er führt aber keine echten Rechnungen, Zahlungen oder Modellaufrufe aus. Er beweist Einbindung und Deklaration, nicht End-to-End-Egress-Enforcement. Er sollte als automatisierte externe Probe erhalten bleiben.

### Fehlende Abnahmeszenarien

- Einzelmodul installiert → ausschließlich zutreffende Module/Checks im Dashboard; mehrere wichtige Minimalzusammensetzungen im echten Browser.
- Noelia ohne Control Plane vollständig benutzbar.
- Monolith ohne fachliches Gateway.
- Tatsächlich erlaubter und tatsächlich gesperrter externer Aufruf, Redirect, direkter SDK-/HttpClient-Pfad.
- Custom-/lokales Modell, nicht nur bekannte Providerdomain.
- Fehlender/veralteter/inkompatibler Service und lizenzbedingt ausgelassener Dienst: niemals vollständiges Grün.
- Zwei Replicas: Schlüsselrotation, Widerruf, Auditkontinuität.
- Restart/Backup/Restore und unterbrochene Pollinghistorie.
- Echte Rollen-/Mandantenisolation mit einem IdP.
- Ausfall eines Alarmziels und später erfolgreiche Zustellung.
- Historische Attestation nach Softwareupgrade unverändert verifizierbar.

## 5. Empfohlene Reihenfolge

1. **Exposition und Geheimnisse:** R01–R04, Dockerstart und sichere Auth-Defaults. Abnahme: keine Secrets in DTOs; ohne vollständige Auth kein Productionstart; echter OIDC-Flow; Redirect-Gegenproben.
2. **Beweisgrenzen:** R05–R08, R12/R18/R20. Bis dahin starke Vollständigkeits-/Kontinuitätsaussagen nicht als Produktversprechen verwenden. Abnahme: fehlende Evidenz bleibt im signierten Dokument sichtbar, Fingerprint wird berechnet, historische Testvektoren.
3. **Dauerhaftigkeit und Nebenläufigkeit:** Schlüsselring, Auditanker, persistenter Demo-Sicherheitszustand, zuverlässige Outbox/Alarme. Abnahme: Replica-/Crash-/Restore- und Poison-Message-Tests.
4. **Demo zur Abnahmeumgebung machen:** falsche Deklarationen, verlorene Gateways, UI-Race, Redirect, enge Proxy-Vertrauenszone; Contoso und Minimalmodule in die Matrix aufnehmen.
5. **CI und Release:** sämtliche Gates auf denselben Kandidaten und identische Artefakte beziehen; Containerstart und Browser dazugehören lassen. Ein grüner Bibliothekslauf allein reicht nicht.
6. **Produktvertrag und Betrieb:** Lizenzmodell konsistent umsetzen; Retention, Upgrades, Support, Schlüsselrotation, Backup und Preisannahmen danach sauber spezifizieren.

Keine dieser Änderungen wurde im Rahmen dieses reinen Analyseauftrags implementiert.

## 6. Grenzen und erhaltene Arbeitsumgebung

- Keine vollständige formale Verifikation, keine unabhängige Zertifizierung, keine juristische Vollprüfung.
- Kein realer externer IdP-Durchlauf, kein produktiver Multi-Replica-Lasttest und kein echter Disaster-Recovery-Test. Kontrollierte Harness-Gegenproben sind entsprechend gekennzeichnet.
- Keine echten externen Payment-/Mail-/KI-Aufrufe und keine fremden Zielsysteme angegriffen.
- Keine Repository-Source-/Config-Änderungen, Commits, Pushes, Veröffentlichungen oder destruktiven Datenresets.
- Builds erzeugten übliche bin/obj-Ausgaben; Browser-E2E erzeugten synthetische Demo-Benutzer/Todos. Die laufenden Dienste protokollierten ihre eigenen Aufrufe.
- Die Demo-Container bleiben für die eigene Sichtprüfung verfügbar. Die zusätzliche isolierte Analyse-Control-Plane auf Port 5200 wird zum Abschluss beendet. Die vorher vorhandene Instanz auf 5199 bleibt unangetastet.
- Der fehlschlagende einmalige Docker-Smoke-Container wurde mit --rm gestartet und beendet sich selbst. Das gebaute lokale Analyseimage und temporäre Belege bleiben erhalten.
- Screenshots und Browserbefunde liegen unter /tmp/noelia-review-76i5JK, weitere Framework-Gegenproben unter /tmp/noelia-review.2Oe4XS.

Belege: [Browser-Navigationen](/tmp/noelia-review-76i5JK/browser-results.json), [Logout-/Attestation-Gegenproben](/tmp/noelia-review-76i5JK/browser-final-results.json), [Control-Plane-Screenshot](/tmp/noelia-review-76i5JK/cp-micro-prod.png), [Monolith-Screenshot](/tmp/noelia-review-76i5JK/mono-prod.png), [isolierter CP/Framework-Harness](/tmp/noelia-review-76i5JK/probe/Program.cs), [KeyRing-/Redirect-Harness](/tmp/noelia-review.2Oe4XS/Program.cs).

## Anhang: vollständige Commit-Inventare

### Noelia: b424aa8..cf54577

```text
4e4732a Noelia 5.1.0: demo im Repo, drei Umgebungen, neun Falschaussagen behoben
4db5e59 ci: die Demo gegen einen gepackten Kandidaten bauen und testen
b0305b7 docs: die beiden Sicherheitsbefunde aus 5.1 aufnehmen, CLAUDE.md nachziehen
57b2309 demo: den Build-Kontext des Edge-Images auf das beschränken, was ausgeliefert wird
df0d0e1 Merge pull request #21
f204435 Noelia 5.2.0: Schlüsselring, Modulverträge, Reichweite der Prüfspur
bc44fec demo: ein Dashboard je Dienst, und eine Widerrufsliste für Development
a05a3ac Noelia 5.2.0: geteilte Prüfspur-Kette, Schlüsselring am Hauptschlüssel
71a0ca8 Merge pull request #22
4fe0f4b Noelia 5.3.0: Transactional Outbox, und Fehlermeldungen ohne Sprachentscheidung
c982d84 Drei Funde aus einer Rückfrage: Sitzungen, Dienst-Hosts, Abmelden
fceb4d9 chore: Playwright-Schnappschüsse gehören nicht ins Repo
c9a27da docs: der fünfte Konstruktorparameter gehört in MIGRATION.md
cc929c3 Merge pull request #23
7f99a2d docs: CLAUDE.md nennt die veröffentlichte Version
1c0e2dc docs: die README hinkte zwei Releases hinterher
9eb5782 Ein Modell hinter dem Dashboard, abrufbar als JSON
16cd531 Die Prüfspur lässt sich zurücklesen und nachrechnen
4700d69 Der Befund reist unter seinem Namen, nicht unter seiner Ordnungszahl
ae201ef Handbuch und Migration für 6.0.0
d6566e1 Zwei doppelte Typen zusammengeführt — und wobei das den Blick freigab
cc809f1 Merge pull request #24
d0bd2cb Die Domain eines Anbieters ohne Subdomain, und eine Prüfung für die tragende Zusage
bb2229d Die Demo zieht mit auf 6.1.0
4ae5c96 Auch die Demo nagelt fest, welche Prüfungen laufen
83b9152 Merge pull request #25
e587edc Zeiten im Dashboard stehen in der Zone des Lesers
177bc8d Merge pull request #26
a4ffef7 Ein Dateipfad ist kein Host, und SaaS ist auch Drittland
493737c Merge pull request #30
c86e7fe Ein Formular, das absendbar ist, bevor es jemand behandelt
2d17e9b Ein Befund nennt den Artikel, der nach ihm fragt
6bf105e Der gedruckte Kompositionswurzel startet jetzt auch
7bc94a5 Eine Schwere, die eine 2 ist, sagt einem Leser nichts
ccff4d2 Merge pull request #31
cf54577 .localhost ist diese Maschine, und dreißig Zeilen davon sind keine
```

### Control Plane: gesamte Historie bis 11304ca

```text
27b6cf6 Eine Flotte wird auskunftsfähig
c36b85e Der Nachweis — das Stück, das verkauft wird
8ff3d3a Ein Image, damit "selbstgehostet" keine Behauptung bleibt
36ee6db Baut gegen nuget.org, ohne lokale Quelle
aa3b248 Das Gedächtnis — seit wann, nicht nur was
592e3c3 Zwei Nullen sind eine Antwort, keine fehlende Angabe
d7294ff Lizenzschlüssel, und die Anbindung endlich dokumentiert
61c1771 Rollen, Filter, Export, Alarme — und die Ortszeit
b127533 Rollen pro Flotte, Alarm-Routing — und endlich Tests
c2422d7 Die Rolle stand im Kopf nur als Parameter, nicht auf der Seite
bd06153 Preisplan, und was ein fremder Dienst über die Anleitung verraten hat
61af7af CI, weil ich sonst der einzige Prüfer bin
8a472f4 Die neue CI hat mich beim ersten Lauf erwischt
12278aa Ein Befehl, der alles hochfährt — und aufhört, wenn es nicht hochkommt
ecf7b97 Der Preisplan verkaufte drei Dinge, die das Produkt verschenkte
dc75f58 Drei Nachweise, weil drei Menschen sie lesen
61c7e1e Eine gültige Lizenz, die ein Dokument nicht deckt, ist nicht „keine Lizenz"
11304ca Wie man gegen einen Noelia-Kandidaten baut, steht jetzt dabei
```


