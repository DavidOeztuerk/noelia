# Umsetzungs- und Übergabeplan: Noelia / Noelia Control Plane

Stand: 30.09.2026. Verbindlicher Arbeitsstand für Menschen und beliebige Coding-Provider.
Auftrag: Die 28 Review-Befunde schrittweise korrigieren, wirksam testen und diesen Plan nach jedem abgeschlossenen Arbeitspaket aktualisieren.

## Sofort weiterlesen: Übergabe

- **Aktuell:** AP10, AP11, AP13, AP14, AP16 ERLEDIGT; AP15 implementiert, volle Browsermatrix offen. Noelia **138 + 3438**, CP **279**, Demo **80**, Frontend **23**, Gate-Unittests **21**, jeweils 0 skipped. Keine umfassende Security-/Releasefreigabe.
- **Letztes abgeschlossenes Paket:** AP16 – siehe Journal vom 30.09.
- **Nächster konkreter Schritt:** AP17/AP18 schlank: ein lokales Release-Gate-Skript (Kandidat packen, Demo + CP gegen denselben Kandidaten, Compose-Smoke, Security-Gate, e2e je Stage), CI nur CP-Image-Start und Unit-Suites. Demo-Baseline an die neuen KI-Check-Ergebnisse anpassen, sobald sie gegen den Kandidaten läuft. Versionsentscheidung (brechende Änderungen → Major). AP12 (Lizenzen) vor dem ersten Verkauf. Keine Veröffentlichung.
- **Arbeitszweige:** `security/review-2026-09` in beiden Repositories (lokal committet, nicht gepusht). Keine Pushes/Tags/Publishes ohne Freigabe.
- **Baseline:** Noelia cf54577071cffbe15bc8231047117d4bf7c44a80; Control Plane 11304ca415f7ca9bfa4ee0e69e014b91080aedab.
- **Nicht anfassen:** bereits vorhandene unversionierte NoeliaControlPlane/src/Noelia.ControlPlane/appsettings.WorkerTransfer.json. WorkerTransfer ist nicht im Auftrag.
- **Laufende Umgebung:** CP-Abnahmeläufe beendet; isolierter Kandidatenlauf `/tmp/noelia-ap09-candidate-cp-dakR2G`. AP09-Produzent aus unverändertem .4 unter `/tmp/noelia-ap09-producer-7r7kqI`; eigener Prozess auf Port 59411 wird nach Browserabnahme gezielt beendet. Browserartefakte `noelia-browser-smoke-OHKPNT` (synthetisch), `noelia-browser-smoke-ybZMUu` (echter Produzent), beide Exit 0. Letzte komplette Demo-Docker-Abnahme bleibt AP05 vom 27.09.; keine fremden Stacks verändert.
- **Autorisierung:** Implementierung und lokale Tests erlaubt. Nicht automatisch veröffentlicht, Konten geändert, Secrets rotiert, Historie umgeschrieben oder Daten gelöscht.
- **Quelle:** [vollständiger Review](reviews/2026-09-22-noelia-control-plane-analyse.md). Die R-Nummern unten beziehen sich darauf.
- Keine Fortsetzung darf Kenntnis des Chats oder temporärer /tmp-Dateien voraussetzen.

## Arbeitsregeln und Definition „fertig“

1. Vor jeder Fortsetzung: diesen Plan, git status beider Repositories und die betroffenen Diffs lesen. Vorhandene Änderungen nicht zurücksetzen.
2. Pro Paket zuerst Regressionstest/Gegenprobe, dann kleinste saubere Korrektur, dann fokussierte und passende Gesamttests.
3. „Implementiert“ ist nicht „abgenommen“ und „gebaut“ ist nicht „gestartet“. Nicht ausgeführte Prüfungen ausdrücklich offen lassen.
4. Nach jedem abgeschlossenen Paket Checkliste UND Fortschrittsjournal aktualisieren: Dateien, Verhalten/Migration, tatsächlich ausgeführte Befehle/Ergebnisse, nächste Aktion.
5. Bei Unterbrechung laufende Arbeit als IN ARBEIT kennzeichnen; konkrete Restschritte und offene Prozesse/Artefakte nennen. Kein Quotenverbrauch als Fertigmeldung.
6. Keine neuen Provider-Abhängigkeiten im providerfreien Kern. README und MIGRATION bei öffentlichem Verhalten aktualisieren; vorhandene Architektur-/Dependency-Guards beachten.
7. Keine Secrets in Git, Plan, Logs oder Tests. Nur synthetische Canary-Werte verwenden. Keine FLUSHALL-, globalen Cache-Purge- oder fremden Datenlöschungen.
8. Neue Fehler gegen reale Wirkung prüfen, nicht nur Registrierung/Fluent-Return. Gute bestehende Tests behalten.
9. Releaseversion erst am Releasepaket entscheiden; Paketformate/Breaking Changes dokumentieren. Keine bestehende 6.4.0 still mit geänderten Inhalten öffentlich überschreiben.
10. Keine AI-Coauthor-Trailer oder andere Autorenmetadaten eigenständig hinzufügen. Keine zusätzlichen Agenten erforderlich; Arbeit ist sequenziell fortsetzbar.

## Repositories und Orientierung

- Noelia: /Users/davidozturk/Projects/Noelia; Bibliothek src/, Tests tests/, aktuelle NuGet-Verbraucherdemo demo/.
- Control Plane: /Users/davidozturk/Projects/NoeliaControlPlane; src/Noelia.ControlPlane, tests/Noelia.ControlPlane.Tests.
- Fremdverbraucher: /Users/davidozturk/Projects/ContosoInvoicing. Nur aufnehmen, wo nötig; keine echten Payment-/Mail-/KI-Aufrufe.
- Historisches /Users/davidozturk/Projects/Demo nicht parallel als aktuelle Referenz weiterentwickeln.
- Noelia/CLAUDE.md enthält providerunabhängige Architektur-/Testregeln, die auch für andere Provider relevant sind.
- Produktgrenze: Noelia = technische Module/Beobachtungen; Control Plane = Sammlung, Historie und begrenzte Evidenz. Health, Securitycheck, Inventar und regulatorische Bewertung nicht gleichsetzen.

## Gesamtübersicht

Status: OFFEN / IN ARBEIT / IMPLEMENTIERT (Restabnahme offen) / ERLEDIGT.

| Paket | Priorität | Befunde | Status | Abhängigkeiten |
|---|---|---|---|---|
| AP00 Dauerhafter Plan, Review und Übergabe | P1 | alle | ERLEDIGT | — |
| AP01 Secret-freie Reportgrenze | P1 | R01 | ERLEDIGT | AP00 |
| AP02 Fail-closed Auth und korrektes Rollenmapping | P1 | R02, R03, R16 | ERLEDIGT | AP01 |
| AP03 Redirect-sicherer Transport | P1 | R04 | ERLEDIGT | AP01 |
| AP04 Startbarer, abgesicherter CP-Betrieb | P1/P2 | R10, R22 | ERLEDIGT | AP02 |
| AP05 Ehrlicher Coverage-/Egress-Vertrag | P1 | R05, R12, R28 | ERLEDIGT | AP03 |
| AP06 Auditkonsistenz und Vollständigkeit | P1 | R06 | ERLEDIGT | AP05 |
| AP07 Dauerhafter atomarer Schlüsselring | P1 | R09 | ERLEDIGT | AP00 |
| AP08 Versionierte, vertrauenswürdige Nachweise | P1/P2 | R07, R08, R18 | ERLEDIGT | AP05, AP06 |
| AP09 Robuster Collector | P2 | R13, R22 | ERLEDIGT | AP03, AP05 |
| AP10 Zuverlässige Alarme und Kettenüberwachung | P2 | R14, R15 | ERLEDIGT | AP06, AP08, AP09 |
| AP11 Outbox unter Fehlern/Nebenläufigkeit | P2 | R21 | ERLEDIGT | AP00 |
| AP12 Lizenz-/Entitlement-Konsistenz | P2 | R17 | OFFEN | AP02, AP05 |
| AP13 Sichere Exporte und präzise KI-Checks | P2 | R19, R20 | ERLEDIGT | AP05, AP08 |
| AP14 Wahrheitsgetreue, dauerhafte Demo | P1/P2 | R11, R12, R26, R27 | ERLEDIGT | AP05, AP07 |
| AP15 Browser-/Gateway-Korrekturen | P2 | R25 | IMPLEMENTIERT (Restabnahme offen) | AP00 |
| AP16 Vollständiges Security-Gate | P2 | R24 | ERLEDIGT | AP05, AP14 |
| AP17 CI und reproduzierbares Release-Gate | P2 | R23 | OFFEN | AP01–AP16 |
| AP18 Gesamtabnahme, Migration und Releasevorbereitung | P1 | alle | OFFEN | AP17 |

## Konkrete Umsetzung und Abnahme

### AP00 – Dauerhafte Unterlagen

- Review in Noelia/docs/reviews übernehmen; dieser Plan ist die einzige maßgebliche Fortschrittsliste.
- In Control Plane einen kurzen Einstieg mit Verweis auf diesen Plan und eigene lokale Testbefehle anlegen.
- Nach jedem Paket Übergabeabschnitt aktualisieren; kein zweiter auseinanderlaufender Plan.
- Abnahme: beide Dateien vorhanden; sämtliche R01–R28 zugeordnet; keine Secrets/temporären Voraussetzungen.

### AP01 – Reports ohne Credentials

- Öffentliche FleetMember-/Answer-/View-Modelle enthalten keine Collector-Credentials.
- Konfigurations-/Requesttypen halten Secrets intern; Collector projiziert ausschließlich sichere Identität in Antworten.
- Defense in depth: Credential-Properties nie serialisieren; versehentliche ToString-Ausgabe verhindern.
- Regression: erfolgreicher, fehlgeschlagener und gemischter Fleet-Response enthält weder Secretwert noch secret-Feld; globale und per-Service-Secrets prüfen.
- Antwortvertrag weiterhin brauchbar für Seiten/Attestation; bestehende Consumers/Tests anpassen.
- Abnahme: Serialization-Tests und realer HTTP-Response; keine Rohkonfiguration im Export.
- Rotation real exponierter Secrets nur nach konkreter Nutzerentscheidung, nicht als stiller Codefix.

### AP02 – Authentifizierung und Autorisierung

- Ohne vollständige OIDC-Konfiguration standardmäßig Startfehler; partielle Konfiguration niemals still auf LocalOperator zurückfallen lassen.
- Expliziter LocalDevelopment-Modus ausschließlich Development und direkte Loopback-Anfragen, klarer UI/HTTP-Hinweis.
- Externe Gruppen vor Mapping materialisieren; eigene normalisierte Identity, feste interne Rollen-/Grant-Claims. Unzugeordnete/externe Claims dürfen keine internen Rechte einschleusen.
- OIDC-Ereignis nach vollständiger verfügbarer Claimgewinnung wählen; Gruppenquelle (ID-/Access-/UserInfo) bewusst dokumentieren.
- Read/Operate/Administer pro Fleet an tatsächlichen HTTP-Routen testen; ?verify=1 erfordert Operator und darf vor Ablehnung keine Erhebung starten.
- Regression: keine/partielle Config, Production-Local, Remote-Local, unmapped group, mehrere Gruppen, fremde Fleet, gefälschte interne Claims.
- Abnahme: echte Auth-Middleware-/Endpointtests plus lokaler kontrollierter OIDC-Provider-Callback vor endgültiger Freigabe; keine Anmeldung gegen Kundensysteme nötig.
- Migration: Beispiele, Demo-Startskripte und Dockeranleitung an bewusst erforderliche Auth-Konfiguration anpassen.

### AP03 – Redirects

- CP-Collector: AutoRedirect aus; Umleitung als per-Service-Fehler. Operator-Header niemals an eine umgeleitete andere Origin senden.
- Noelia: Factory-Transport so konfigurieren, dass Redirects nicht unterhalb des Guards entkommen. Entweder explizit blockieren oder begrenzt jeden Hop prüfen; vorhandene PrimaryHandler-/Resilience-Konfiguration respektieren.
- Contract klar auf geschützte HTTP-Pfade begrenzen; direkte SDKs/Clients sind keine automatische prozessweite Garantie.
- Regression mit realen lokalen Listenern: 301/302/303/307/308, body-preserving POST, Host-/Scheme-/Portwechsel, Schleifen und erlaubter Direktaufruf.
- Abnahme: blockiertes Ziel erhält weder Request noch Body/Canary-Header; Noelia- und CP-Suiten grün.

### AP04 – Docker und eigene CP-Sicherheit

- HistoryPath und SigningKeyPath explizit unter beschreibbarem persistentem Datenverzeichnis; DataProtection ebenfalls bewusst persistieren.
- Non-root beibehalten, writable paths eng begrenzen, keine blanket chmod-Freigabe.
- CSP, no-store, nosniff, Anti-Framing und Referrer-Policy für eigene Antworten; externe IdP-Redirects nicht kaputt konfigurieren.
- Eigene sicherheitsrelevante Aktionen nachvollziehbar auditieren, ohne Secrets/Payloads zu loggen.
- Insecure TLS höchstens im expliziten lokalen Demomodus; Transportqualität künftig signiert sichtbar.
- Abnahme: frisches Image startet unter erlaubter Config, kann persistieren und nach Restart lesen; Standard ohne Auth verweigert bewusst mit verständlichem Fehler.
- Datenmigration für bestehende relative history/key-Dateien dokumentieren, nicht automatisch löschen.

### AP05 – Scope, Coverage und Egress

- Versionierter Report mit getrennten declared dependencies, allowed host/network policy und observed calls (sofern tatsächlich erhoben).
- Coverage: erwartete/gesammelte/fehlende/ausgelassene Services, unterstütztes Schema, Freshness, Control Scope.
- Unknown/Partial nicht als Pass oder vollständiges Grün anzeigen; private IP nicht als Rechtsnachweis interpretieren.
- Falsche Überschriften in UI, README, Export und Attestation gemeinsam korrigieren.
- Pro Rolle erwartete Composition definierbar; Unterschiede zunächst differences, nicht pauschal drift.
- Lizenzkürzung muss sichtbar und signierbar bleiben; kein vollständiger Nachweis über abgeschnittene Flotte.
- Abnahme: fehlender, unerreichbarer, abgeschnittener, unbekannter Dienst und Allow-ohne-Declare können keine vollständige Aussage erzeugen.

### AP06 – Auditnachweise

- Bestehende Hash-/Linkprüfung behalten, aber Konsistenz/Vollständigkeit/Partial/Unknown auseinanderziehen.
- Vollständigkeit nur mit definiertem Genesis, Sequenz, erwarteter Anzahl/Head und geeignetem unabhängigem Checkpoint; Storage-Snapshot/Race-Verhalten definieren.
- Redis-Read darf fehlende Payloads nicht still verschlucken.
- Kein WORM-/Manipulationssicherheitsversprechen aus unkeyed hash alone.
- Regression: Präfix/Suffix/alles entfernt, Payload geändert, innere Lücke, Neuberechnung, zeitlicher Teilbereich, paralleler Append.
- Abnahme: abgeschnittene Kette nicht als vollständig intakt; Migration bestehender unanchored Logs bleibt ehrlich Unknown/Partial.
- Reihenfolge: (1) Reader verwirft fehlende/leere/null/malformed oder falsch zugeordnete Payloads nicht mehr still, echte Redis-Regressionen. (2) Versionierter Verifikationsvertrag trennt Hash-/Linkkonsistenz, Lesefehler, Bereich und belegte Vollständigkeit; bestehende IsIntact-Aufrufer/CP-Labels explizit migrieren. (3) Genesis und erwarteter Head/Count/Sequenz unter definiertem Snapshot prüfen, konkurrierende Appends/Entfernungen testen. (4) Unabhängige Checkpoints bewusst kennzeichnen; ein Head aus demselben veränderbaren Store allein beweist keine Manipulationssicherheit oder Vollständigkeit. (5) Provider-/HTTP-/CP-/Paket-Abnahme mit neuem Kandidaten. AP05-Kandidat nicht überschreiben.

### AP07 – Schlüsselring

- Providerfreier dauerhafter Storage-Vertrag statt unklarer Cache-TTL; atomisches Append/Indexieren über alle Prozesse.
- Redis über atomare Operationen/Lua; InMemory explizit prozesslokal und ohne überraschende TTL; Provider-Verträge testen.
- Verschlüsselung/AAD/Domainseparierung erhalten; bestehende Daten mit dokumentiertem kompatiblem Lese-/Migrationspfad.
- Regression: 31 Minuten Uhrvorlauf, zwei gleichzeitige Writer, Rotation/Revocation, neue Replica, Neustart, fehlendes Element.
- Abnahme: beide Schlüssel sichtbar, Revocations wirksam, keine unbeabsichtigte Eviction; reale Redis-Conformance zusätzlich zum kontrollierten Interleaving.

### AP08 – Attestations und Historie

- Fingerprint ausschließlich aus PublicKey berechnen/vergleichen; signatureValid, issuerTrusted, schemaSupported getrennt.
- Curve/Algorithmus kontrollieren; Trust anchor nicht aus demselben fremden Dokument übernehmen.
- Exakt persistierten Snapshot mit Digest, observedAt, firstSeen/lastConfirmed, Coverage und Beobachtungslücken signieren.
- UnchangedSince nur für passenden Snapshot und deklarierte Stichprobensemantik; entfernte jüngste Fakten dürfen keine Kontinuität rückdatieren.
- Schema-versionierte unveränderliche Payloads; alte Dokumente unverändert prüfen, kein Deserialize/Reserialize in anderes Format.
- Regression: Fake-Fingerprint/anderer Key, fehlende Felder, unsupported schema, alte v1/v2-Testvektoren, Pollinglücke/Zustandswechsel, Keyrotation.
- Abnahme: Negativfälle im HTTP-Verifier und formatübergreifender Referenzvektor; bestehende Schlüssel nicht ersetzen.

### AP09 – Collector-Ausfallsicherheit

- Fehler je Mitglied isolieren: Transport, Status, Größe, JSON, Schema, Identität, Frische.
- Begrenzte Bodygröße, Timeout und Parallelität; kein unbounded Buffering.
- Falsche Serviceidentität/alte Reports als Konflikt ausweisen; keine ungeprüften Behauptungen signieren.
- Tatsächliche Transport-/TLS-Prüfung je Quelle erfassen und mit AP08 signierbar machen; keine Gleichsetzung von lokal akzeptiertem Testzertifikat und verifizierter Quelle.
- Abnahme: ein kaputter/zu großer/langsamer Service nimmt gültige andere Antworten nicht mit; Cancellation unterscheidbar von Timeout.

### AP10 – Alarmierung

- Zustellabsicht zusammen mit Transition dauerhaft erfassen; Delivery-Status, Retry mit Backoff und stabile Idempotenz-ID.
- Routenfehler/Timeout isolieren, keine Löschung der Beobachtung.
- Regelmäßige begrenzte Kettenprüfung mit Freshness und History-Übernahme.
- Recovery nur bei definiertem positiven Zielzustand, nicht bei gone.
- Abnahme: 500→Restart→200 liefert nach; eine hängende Route verhindert andere nicht; Fail→gone ist Coverageverlust; echte chain-Änderung erzeugt Route.

### AP11 – Noelia-Outbox

- Retryzeit, Backoff, Quarantäne-/Poison-Zustand und manuelle nachvollziehbare Wiederaufnahme.
- Lease-Owner/Fencing gegen späte Ack/Release fremder Claims; at-least-once und nötige Consumer-Idempotenz dokumentieren.
- Keine Rohpayloads/Secrets in LastError/Logs.
- Abnahme: volles Poison-Batch blockiert jüngere gute Nachrichten nicht; zwei Dispatcher plus Leaseablauf verlieren keine Zuständigkeit.

### AP12 – Lizenzvertrag

- Laufende Uhr statt beim Start eingefrorener Gültigkeit; globales Servicebudget, eindeutige Fleet-/Alert-/Domain-Entitlements.
- Freie Beobachtung vs bezahlter Export bewusst von Sicherheitsauth trennen.
- Preisplan, Terms, Recorder, UI und API denselben Vertrag verwenden lassen.
- Issue-CLI mit richtiger Authority vor Ausgabe validieren.
- Abnahme: Ablauf ohne Restart, mehrere Fleets im globalen Budget, Lizenzkürzung sichtbar, Authority mismatch, Domain-/Alertgrenzen.

### AP13 – Exporte und KI-Prüfungen

- Tabellenexport als Text schützen; Formelpräfixe und Zeilenumbrüche testen; Rohwerte weiterhin als JSON nutzbar.
- KI-Klassifikation explizit deklarierbar; unbekannter/local Modellhost bleibt nicht pauschal „keine Pflicht“.
- Sinkregistrierung von tatsächlich belegtem Modellaufruf/Auditereignis unterscheiden.
- Regulatorische Referenzen unterstützen Menschen, ersetzen keine Rechtsbewertung; zeitabhängige Primärquellen bei Textänderungen prüfen.
- Abnahme: custom model, keine deklarierte KI, bekannter Provider, registrierter aber unbenutzter Sink; keine unbelegte Compliance-/Autologging-Aussage.

### AP14 – Demo als belastbare Referenz

- Dienstspezifische echte Dependencies; Gateway maschinenlesbar erfassen, ohne zwingend seine HTML-UI öffentlich zu machen.
- Sicherheitszustand persistent oder ausdrücklich ephemer markiert; kein FLUSHALL zum Rate-Limit-Reset.
- Dauerhafte Fachprovider im gewählten Betriebsszenario oder sichtbare Grenze der User/Todo-InMemory-Daten.
- Nur tatsächliche Proxykette/Subnetze vertrauen, Netzwerksegmente passend schneiden.
- Eigenständige aktuelle Referenz in Noelia/demo; historisches Demo nicht ohne Zustimmung löschen.
- Abnahme: Restart/Restore, richtige Dependencies je Host, vorhandene Gateways, keine verschwundenen Widerrufe, Monolith ohne Ocelot.

### AP15 – Browser und Gateway

- Logout bis zur Handler-/Sessionbereitschaft deaktivieren, inklusive aria-Zustand; Fehler lässt Sitzung bewusst erhalten.
- HTTP→HTTPS mit tatsächlichem externem Port/Origin.
- Registrierungs-RegEx-Timeout unter Kaltstart/Last reproduzieren und Ursache erst dann korrigieren; nicht pauschal Regexlimits hochsetzen.
- Abnahme: verzögerte Refresh-Antwort + früher Klick; erfolgreicher und fehlender Logout; Redirect auf 8443; sechs E2E-Kombinationen samt Browser-/Serverlogs.

### AP16 – Security-Gate

- Erwartete Service-/Checkmenge und pro Run abgeschlossene aktuelle Ergebnisse prüfen.
- Fehlender Service/Check, veraltete oder unvollständige Runs sind nicht erfolgreich.
- Warnungsakzeptanz begründet und auf Scenario/Check beschränkt; keine globalen stillen Ausnahmen.
- Abnahme: fehlender ganzer Host, fehlende Check-ID, alter Pass plus neuer Fail, unvollständiger Run, absichtlich weggelassenes Modul.

### AP17 – CI und Release-Gate

- Kaputtes Healthskript durch aktuelle profil-/hostbasierte Abnahme ersetzen.
- Noelia, CP, Demo und Fremdverbraucher gegen exakt denselben identifizierten Paketkandidaten bauen.
- Kandidaten mit isoliertem Paketcache / eindeutiger Version; keine globalen NuGet-Verzeichnisse löschen.
- CI-Matrix: Mono/Micro × Dev/Staging/Production; .NET, JS, Compose, Security-Gate, Playwright, Header/TLS, Image-Smoke.
- Einzelmodul-Dashboard und Fremdverbraucherprobe automatisieren; keine unbeabsichtigte Gesamtframeworkabhängigkeit.
- Publish nur nach erfolgreicher kompletter Abnahme desselben Commits/Artefaktsatzes; keine Publish-Ausführung in diesem Auftrag.
- Abnahme: künstlicher Docker-/Browser-/Securityfehler blockiert Release; sauberer Rechner kann dokumentiert reproduzieren.

### AP18 – Abschluss und Betriebsvertrag

- Alle offenen Regressionen und Migrationsentscheidungen abarbeiten; keine offene P1 als erledigt markieren.
- Vollsuite plus reale Consumer-/Container-/Browsermatrix aus frischem isoliertem Stand.
- Dokumentierte Backups, Restore, Keyrotation, historische Verifikation, Upgrades und Grenzen.
- Versionierungs-/Migrationsvorschlag und PR-fähige zusammenhängende Änderungen vorbereiten; Nutzer entscheidet Veröffentlichung.
- Letzter Planstand enthält exakte Testcounts, SHAs/Arbeitsdiff, relevante URLs und verbleibende Restrisiken.

## Testbefehle und sichere Durchführung

Aus dem jeweiligen Repository:

```sh
# Noelia (Docker für Integrationsprüfungen erforderlich)
dotnet test Noelia.slnx -c Release

# Control Plane
dotnet test tests/Noelia.ControlPlane.Tests -c Release

# Demo (Version/Feed vorab bewusst wählen)
dotnet test demo/Noelia.TodoDemo.sln -c Release
npm --prefix demo/src/frontend test

# Laufende lokale Demo – keine fremden Stacks herunterfahren
cd demo
docker compose --profile all up -d --build --wait
python3 eng/security-checks.py
```

Browsermatrix mit DEMO_BASE_URL: http://mono-dev.localhost:8080,
http://micro-dev.localhost:8080 sowie https://mono-staging.localhost:8443,
https://micro-staging.localhost:8443, https://mono-prod.localhost:8443,
https://micro-prod.localhost:8443; Playwright im Verzeichnis demo/src/frontend.
Nicht schneller erneut starten als Auth-Rate-Limits erlauben; besser je Run isolieren.
Lokale Tests mit simuliertem IdP/HTTP-Server nicht auf externen Kundendiensten ausführen.

## Fortschrittsjournal

### 30.09.2026 – AP13, AP14, AP15, AP16 – schlanke Umsetzung
- R19 (CP, ERLEDIGT): `Export.cs` stellt jeder CSV-Textzelle, die (auch nach führenden Leerzeichen) mit `= + - @`, Tab, CR oder Vollbreitenform beginnt, ein `'` voran; JSON unverändert. 17 neue Tests mit RFC-4180-Parser, 14 vorher rot. CP **279/279**.
- R20 (Noelia, ERLEDIGT): „nichts erkannt" ohne Deklaration ist `Warning`/„Not determined" statt Pass bzw. „keine Aufzeichnungspflicht". Neu `DeclareArtificialIntelligence(bool, endpoint)`; deklariertes Nein wird als Erklärung ausgewiesen, eine Erkennung schlägt es. Sink-Text behauptet keine Aufzeichnung von Modellaufrufen. Sichtbar: `noelia.ai.inventory` Pass→Warning, `noelia.ai.record-keeping` NotApplicable→Warning ohne Deklaration. Tests nicht red-first geschrieben (Fix vor Test). Noelia **138 + 3438**.
- Outbox-Testharness: zwei Rennen im Test (geteilte `:memory:`-Verbindung; Warten auf Claim statt Zustellung) behoben, 40/40 Läufe grün. Produktionscode unverändert.
- R11/R26/R27 (AP14, ERLEDIGT, schlank): Valkey Staging/Prod mit AOF und Volume, Reset nur `rl:*` statt FLUSHALL; sichtbarer Hinweis auf In-Memory-Konten/Todos; Forwarded-Header nur vom Edge/Gateway in festen Subnetzen je Stage. Keine persistenten Fachprovider (bewusst).
- R25 (AP15, IMPLEMENTIERT): Abmelden gesperrt bis Sitzung bereit, Fehlschlag behält Sitzung; Redirect trägt `NOELIA_HTTPS_PORT` (bei 0/443 ohne Port). Live gegen eigenen Stack geprüft (Redirect, Logout-Tests in mono-dev und micro-prod). Offen: volle e2e-Suite aller sechs Kombinationen, Stage-Hinweis im echten Browser; Ocelot-Timeout (R25c) nicht reproduziert, unverändert.
- R24/R23 (AP16, ERLEDIGT): `security-checks.py` mit Sollmenge aus `security-check-expectations.json` + `composition-baselines.json`, letzter Lauf je Dienst, Freigaben je Stage/Dienst; fehlender Dienst/Check scheitert. Grenze: ohne Run-Marker ist ein nach der letzten erwarteten ID abgebrochener Lauf nicht erkennbar. `test-docker-health.sh` gelöscht. Demo **80/80**, Frontend **23/23**, Python **21/21**; Gate live grün für 12 Dienste, rot für erfundenen Dienst.

### 30.09.2026 – AP11 – ERLEDIGT, Outbox mit Backoff, Quarantäne und Fencing
- Dateien: `IOutbox.cs`, `EntityFrameworkOutbox.cs`, `NoeliaOutboxMessage.cs`, `OutboxDispatcher.cs`, `OutboxTests.cs`, `OutboxDispatcherTests.cs`, README (Outbox), MIGRATION (neuer Abschnitt).
- Verhalten: neue Spalten `NextAttemptAt`, `QuarantinedAt`, `ClaimToken`; Claim nur für fällige, nicht quarantänierte Zeilen ohne lebende Lease, Rücklesen über ein Token je Claim. `MarkDelivered`/`Release`/`Quarantine` sind per Token gefenced und liefern bool. Dispatcher: Backoff `BaseRetryDelay·2^(n-1)` bis `MaxRetryDelay`, ab `MaxAttempts` Quarantäne (nie gelöscht), `RequeueAsync` als manuelle Wiederaufnahme mit Historie in `LastError`. `LastError`/Logs nur Exception-Typname.
- Migration (brechend, kein Obsolete): EF-Migration für drei Spalten und Indizes; `IOutboxReader`-Signaturen und `OutboxMessage.ClaimToken` geändert; `AttemptsBeforeAlarm` ersetzt durch `MaxAttempts`/`BaseRetryDelay`/`MaxRetryDelay`, Validierung beim Start. Alte Dispatcher vor Rollout stoppen. Demo nutzt die Outbox nicht.
- Tests: Poison-Batch blockiert keine gute Nachricht, Backoff, Quarantäne/Requeue, zwei Dispatcher mit Leaseablauf, Canary nicht in `LastError`/Log, Optionsvalidierung. Tests kompilieren gegen den alten Code nicht (neue Signaturen), daher kein realer Rot-Lauf. `dotnet test Noelia.slnx -c Release`: **138 + 3430**, 0 skipped. `git diff --check` sauber.
- Offen: kein Jitter, keine Listen-API für Quarantäne, ContosoInvoicing nicht geprüft.

### 30.09.2026 – AP10 – ERLEDIGT, dauerhafte Alarmzustellung und periodische Kettenprüfung
- Dateien: CP `Severity.cs`, `AlertQueue.cs`, `Alerts.cs`, `FleetHistory.cs`, `FleetCollector.cs` (Optionen), `FleetRecorder.cs`, `Program.cs`, `Attestation.cs`; Tests `AlertDeliveryTests` (umgeschrieben), neu `AlertOutboxTests`, `FleetRecorderChainTests`, `AlertsEndpointTests`; README/MIGRATION.
- Verhalten: Zustellabsicht im selben SQLite-Commit wie die Transition, Lease/Owner-Fencing, Backoff, stabiler Idempotency-Key (Empfänger deduplizieren). `Alerts:MaximumAttempts` (Default 20) → Endzustand `Failed`. `GET /alerts.json?fleet=` mit Leserecht je Fleet, no-store, ohne URL/Payload. `X→gone` ist Coverage-Verlust (Warning), keine Recovery. `ChainVerificationInterval` (Default 6 h, `00:00:00` = aus), letzte Prüfung je Fleet in SQLite; Zwischenrunden übernehmen den letzten Kettenzustand. Neue Tabellen `alert_deliveries`, `chain_verifications`.
- Nebenbefund behoben: `AttestationSigner` erzeugte bei gleichzeitigen Erststarts auf macOS verschiedene Schlüssel (12/20 Läufe rot, bis zu 8 Identitäten), weil `File.Move(overwrite:false)` dort nicht exklusiv ist. Jetzt exklusives `FileMode.CreateNew` direkt auf der Schlüsseldatei; Verlierer warten höchstens 2 s auf die Bytes des Gewinners, nur bei Dateien jünger als 5 s. Danach 30/30 grün.
- Tests: vier R15-Tests vor Fix rot. 500→Neustart→200 mit gleichem Idempotency-Key, veraltete Lease, Atomarität, Max-Versuche, Intervall/Persistenz der Kettenprüfung, Chain-Wechsel erzeugt Zustellung, Endpoint-Autorisierung. `dotnet test tests/Noelia.ControlPlane.Tests -c Release` dreimal **262/262**, 0 skipped. `git diff --check` sauber.
- Offen: manuelle `?verify=1`/Attestation setzen `chain_verifications` nicht; Recorder beachtet `licence.MaxServices` weiterhin nicht (AP12). Kein Browser-/Docker-Lauf für AP10.

### 30.09.2026 – AP09 – ERLEDIGT, begrenzter Collector und Source-Evidenz
- Dateien: `FleetCollector.cs`, neues `CollectionSourceEvidence.cs` mit gemeinsamem `CollectorConcurrency`, `Program.cs`, `FleetRecorder.cs`, `FleetCoverage.cs`, Services-Seite, Attestation-Caveats, README/MIGRATION, `CollectorIsolationTests.cs`, `CollectorTlsTests.cs`, angepasste vollständige Wire-Fixturn und Browserfixture. Keinen eingefrorenen Attestation-Vektor oder die V3-Shape geändert.
- Verhalten: HTTP-ResponseHeadersRead, frühe Content-Length-Prüfung und zusätzlich begrenztes Streaming (Default 1 MiB), memberweiter Zehnsekunden-Budget für Report/Audit einschließlich Body, gemeinsamer Semaphore mit acht Mitgliedern pro Prozess. Startvalidierung für Grenzen und sichere URLs/eindeutige Namen. Schema 1/2/3, vorhandene Kernfelder/-arrays, exakte erwartete Serviceidentität (Alias über ExpectedServiceIdentity) und konfigurierte Frische. Fehler je Mitglied; Rohdaten fehlerhafter Reports nicht aggregiert/signiert. Auditfehler behalten brauchbaren Report mit Unknown-Chain. Caller-Cancellation wird weitergegeben. Error/Logs enthalten stabile Klassen statt Remote-Exceptiondetail/Body.
- Source: CollectionState, verlangter Transport, tatsächliches TLS-Callbackresultat, konfigurierte TLS-Policy und Identitätsresultat in Fleet JSON/Services. Callback fehlt/pooled reuse bleibt NotObserved. Lokales absichtlich akzeptiertes selbst signiertes Zertifikat wird AcceptedUntrusted, regulärer Modus lehnt es ab. Alle Source-Klassen sind in signierten Caveats; kein Jurisdiktions-/Wahrheitsbeweis und kein erfundener positiver TLS-Befund.
- Regression: **6 rot → 6 grün** für ungültiges JSON, null/missing Arrays, falsche Identität, alte Probe und unbekanntes Schema bei gleichzeitig gültiger Antwort. Zusätzlich deklarierter/gestreamter Oversize mit tatsächlicher Bytegrenze, hängender Body, echte Caller-Cancellation, zwei Collector mit gemeinsamer Grenze, Alias, Audit-JSON-Fehler und zwei reale lokale TLS-Handshakes. Bestehende sparse Wire-Fixturn liefern nun die verpflichtenden Kernfelder; ein Compose-Test verwendet kontrollierte Uhr für seine historische Probe. Ein Test für fehlendes modules behandelt den Report jetzt als NotCollected. Alte Ziele/Assertions für Unknown/Partial/keine erfundene Evidenz bleiben erhalten.
- Final `dotnet test tests/Noelia.ControlPlane.Tests -c Release --nologo -m:2`: **242/242**, 0 skipped/Exit 0; isolierter Kandidat **6.4.1-security.20260930.4** erneut **242/242**, 0 skipped/Exit 0. Node/OpenSSL alle drei unveränderten Attestation-Vektoren grün. Echter Schema3Producer aus .4 gebaut, 0 Warnungen/Fehler; synthetischer und realer Browser sammeln erfolgreich, Auditgrenzen/CSP/Zeitzone/keine Browserfehler geprüft, beide Exit 0. Beide git diff --check sauber. Keine Noelia-Paketänderung, Commits oder Publishes.
- Nächster Schritt AP10, dauerhafte Alarm-Outbox und reguläre Kettenprüfungen. Umfassende Docker-/Release-Matrix bleibt AP18.

### 30.09.2026 – AP08 – ERLEDIGT, Trust und dauerhafte Stichprobenevidenz
- Dateien: `Attestation.cs`, neue `AttestationVerification.cs`, `AttestationCanonical.cs`, `AttestationSnapshot.cs`, `FleetSnapshots.cs`; `FleetHistory.cs`, `FleetRecorder.cs`, `Program.cs`, Optionen in `FleetCollector.cs`, Attestation-/History-Seiten, README/MIGRATION, `docs/ATTESTATION-FORMAT.md`, fünf Attestation-Testklassen und feste V1/V2/V3-Referenzdateien sowie `eng/verify-attestation-vectors.mjs`. Eine ältere Audit-Testsequenz verwendet jetzt einen neuen Zeitstempel beim erneuten Beobachten statt eine ältere Probe als neue Probe zu schreiben.
- Verhalten: berechneter Fingerprint, genau P-256/SPKI ohne nachgestellte Bytes, P1363/SHA-256; Signatur, Schema, Payload und unabhängige volle Public-Key-Pins getrennt. Default vertraut keinem Issuer. HTTP prüft ursprüngliches JSON, nicht die aktuelle DTO-Projektion; V1/V2 bleiben historisch verifizierbar. Neue V3-Signatur bindet Zweck und Schema. Negative HTTP-Fälle für Fingerprint, fremden Key, Algorithmus, fehlende Felder, unbekanntes Schema, Digest und Coverage. Feste Shapes/Bytes verhindern eine spätere stille Formatänderung.
- Historie: Snapshots, aktuelle Referenz und Beobachtungslücken als additive SQLite-Tabellen. Ausgabe persistiert Beobachtung und Transition in einer Transaktion, liest Inhalt zurück und signiert diese Evidenz. Snapshot-ID, SHA-256-Digests, Coverage/Composition/Audit, firstSeen/lastConfirmed, deklarierter maximaler Abstand und letzte 32 Lücken samt Gesamtzahl sind signiert. Nur beobachtete passende Projektion; Änderungen zwischen Stichproben sind nicht ausgeschlossen. Entfernte jüngste Fakten rückdatieren nicht mehr. Erhebungslücken/zu lange Intervalle setzen zurück; alte parallele Probe kann nicht überschreiben. Kein rückwirkender Kontinuitätsanspruch aus alten Fact-Zeilen, keine historische Löschung. Zustellfehler allein ist keine Erhebungslücke; dauerhafte Delivery folgt AP10.
- Schlüssel: neue private Datei owner-only/atomar veröffentlicht; 16 gleichzeitige Erststarts convergieren auf denselben Schlüssel. Bestehender P-384-Schlüssel wird ohne Ersatz abgelehnt. Keine echten Schlüssel gelesen, rotiert oder ausgegeben. Rotation wird über unabhängig konfigurierte alte/neue Public-Pins akzeptiert.
- Tests: Fingerprint/Schema/Curve zunächst **4 rot → 4 grün**; History-Entfernung/Pollinglücke zunächst **2 rot → 2 grün**. HTTP-Ausgabeprüfer verifiziert Signatur über die aus SQLite gelesenen Bytes bei Partial-Coverage. Final `dotnet test tests/Noelia.ControlPlane.Tests -c Release --nologo -m:2` **227/227** gegen öffentliche 6.4.0 und isoliert erneut **227/227** gegen Kandidat **6.4.1-security.20260930.4**, 0 skipped/Exit 0. Node/OpenSSL prüft alle drei festen Formate, exakte Bytes/Hashes, Signatur, Trusttrennung und Tampering, Exit 0. `git diff --check` sauber. Kein neues Noelia-Paket nötig; keine Commits/Pushes/Publishes.
- Grenze: V1/V2 banden die Schema-Version historisch nicht in ihre Signatur; das wird nicht nachträglich behauptet. SQLite/Digest allein ist kein unveränderbarer externer Beweis. Snapshots erhöhen Speicherbedarf; keine automatische Retention. Quelle/Transport und fehlerhafte Reports werden in AP09 weiter begrenzt. Nächster Schritt AP09.

### 30.09.2026 – AP07 – ERLEDIGT, atomarer Schlüsselstore
- Dateien: neuer providerfreier `IDataProtectionKeyStore`/`DataProtectionStoredElement`, `InMemoryDataProtectionKeyStore`, `RedisDataProtectionKeyStore`; Cache-/Modulregistrierungen beider Provider, `DataProtectionKeyRing.cs`, `BuiltInSecurityChecks.cs`, README/MIGRATION und Schlüsselringtests einschließlich gemeinsamer `DataProtectionKeyStoreConformance.cs`.
- Verhalten: append-only Elemente ohne TTL; Redis-HSETNX in dediziertem Hash außerhalb des Cache-Namensraums. Identischer Retry erlaubt, abweichendes XML unter gleicher ID abgelehnt. Cache-Patternlöschung berührt den Schlüsselring nicht. InMemory bleibt ausdrücklich prozesslokal. Legacy-Index und XML werden beim Lesen idempotent übernommen; fehlende/leere indexierte Elemente brechen ab, damit verschwundene Revocations nicht ignoriert werden. Verschlüsselung/AAD unverändert.
- Migration: alte Einträge vor ihrem Ablauf und bei koordinierter Replica-Umstellung übernehmen; alte Versionen sehen neue Schlüssel nicht. Bereits abgelaufene Elemente können nicht rekonstruiert werden. Redis benötigt dauerhafte Daten/Backup und eine passende Eviction-Policy; die Demo-Persistenz bleibt AP14. Kein alter Datenbestand gelöscht oder realer Schlüssel rotiert.
- Regressionen: echter Uhrvorlauf um 31 Minuten, gemeinsame InMemory-/Redis-Conformance, zwei Replica-Writer mit 64 Elementen, ID-Konflikte, Revocation, echter ASP.NET-KeyManager mit verschlüsseltem XML und Cookie über Rotation/neue Replica, eigener Redis-AOF-Container nach Stop/Start, Legacy-Import und fehlendes Element. Fokussiert **41/41**; finaler Gesamtbefehl `/usr/local/share/dotnet/dotnet test Noelia.slnx -c Release --nologo -m:1`: Core **138/138**, Infrastructure **3408/3408**, insgesamt **3546/3546**, 0 skipped, Exit 0.
- Finaler neuer Kandidat **6.4.1-security.20260930.4**, 14 nupkg plus Symbole; isolierter CP-Test **203/203** und Demo **73/73**, jeweils 0 skipped/Exit 0. CP enthält bereits vier AP08-Trustregressionen, vorheriger Stand gegen öffentliche 6.4.0 und Kandidat .3 je 199/199. .3 enthält den späteren Missing-Element-Fix nicht und wird nicht als finale AP07-Abnahme verwendet. Beide `git diff --check` sauber. Keine Commits/Pushes/Publishes.
- Nächster Schritt AP08: historisches Raw-JSON unverändert prüfen, unabhängige Trust-Pins und formatübergreifende Vektoren, exakten Snapshot mit Digest und beobachteter Kontinuität persistieren und signieren.

### 30.09.2026 – AP06 – ERLEDIGT, Snapshot- und Checkpointgrenze
- Noelia: `AuditChainVerifier` vergleicht bei Streaming den gemeinsamen Head vor/nach dem Lesen und verweigert bei Bewegung/fehlendem Suffix ein positives Ergebnis. Providerfreier optionaler Snapshot- und Checkpointvertrag; InMemory atomarer Snapshot unter Lock, Redis atomarer begrenzter Lua-Snapshot mit ZCARD-Grenze vor ZRANGE, Sequenzscores, Payload- und Head-Prüfung. Ältere Redis-Indizes und zu große Snapshots fallen ohne Stabilitätsbehauptung auf Streaming zurück. V2-Evidenz nennt Snapshot, Sequenz, Anzahl, Head, Ankerzustand und nur bei exaktem unabhängig signiertem P-256-Checkpoint `Complete`. Signaturschlüssel und Public-Key-Pin liegen außerhalb des veränderbaren Audit-Stores. Unkeyed Hash allein beweist keine Vollständigkeit; Signierung behauptet nur den vom unabhängigen Signer akzeptierten Zustand.
- CP: FleetHistory nutzt die explizite Evidenz; Broken→Unknown/ReadFailed/gone ist keine Recovery. Services/Attestation und Noelia-Dashboard trennen Write-Time, Konsistenz, Lesefehler und belegte Vollständigkeit. V1/Legacy bleiben lesbar und ohne Vollständigkeitsversprechen; V2-Wireadapter verwirft widersprüchliche Felder. README/MIGRATION und Browser-Fixturn aktualisiert. Bestandslogs ohne passenden externen Checkpoint bleiben Unknown/Partial, keine rückwirkende Signierung.
- Regression: echte isolierte Redis-Container für Präfix-/Innen-/Suffix-/Gesamtlöschung, Payload-Änderung und neu berechnete Kette; signierter Anker wird nur bei exaktem Snapshot/Head/Count/Genesis als Complete akzeptiert. Kontrollierter konkurrierender Append ergibt keinen positiven Moving-Head-Befund; Zeit-Suffix bleibt Partial. InMemory-Checkpoint- und CP-V2-Negativfälle grün. Kein fremder Redis-Store gelöscht.
- Volltest `dotnet test Noelia.slnx -c Release --nologo -m:1`: Core **138/138**, Infrastructure **3396/3396**, 0 skipped, Exit 0. CP `dotnet test tests/Noelia.ControlPlane.Tests -c Release --nologo -m:2`: **199/199** gegen veröffentlichte 6.4.0 und erneut **199/199** gegen Kandidat, 0 skipped. Demo gegen Kandidat **73/73**, 0 skipped; öffentlicher Pfad bereits zuvor 73/73. Frontend **9/9**. Erste zwei parallele Noelia-Gesamtläufe hatten je einen isoliert nicht reproduzierbaren Testfehler; serieller vollständiger Lauf ist grün. Kein Test wurde abgeschwächt.
- `dotnet pack` erzeugte **14** Pakete plus Symbole als **6.4.1-security.20260930.2**; `.1` war vor der Redis-ZCARD-Korrektur gebaut und wird nicht wiederverwendet. Isolierter Cache/Build `/tmp/noelia-ap06-candidate-XzSN1e`, keine globale Bereinigung. Echter Schema3Producer aus `.2` gebaut (0 Warnungen/Fehler), normaler CP sammelte ihn im Chromium-Browser; V2-Snapshot und Unknown ohne Anker geprüft. Synthetischer Browserlauf prüfte vier Audit-Zustände. Beide Browserläufe Exit 0, keine Browserfehler, lokale Prozesse gezielt beendet; Artefakte unter `noelia-browser-smoke-Om73f9` und `noelia-browser-smoke-9J4PZz` im macOS-Tempverzeichnis. `git diff --check` beide Repositories sauber. Keine Commits/Pushes/Publishes.
- Grenze: Dies ist die AP06-Abnahme, keine vollständige AP18-Container-/Security-Gate-Matrix. Ohne betrieblich extern aufbewahrten Signerschlüssel und unabhängigen Checkpoint wird in Produktion kein `Complete` behauptet. Nächster Schritt AP07, dedizierter dauerhaft atomarer Schlüsselring.

### 28.09.2026 – AP06 – IN ARBEIT, explizite Audit-Evidenz und CP-Kompatibilität
- Noelia-Abstractions: optionaler Evidence-Vertrag Schema 1 mit Consistency (Unknown/Consistent/Broken/ReadFailed), Completeness (Unknown/Partial), Scope und ausdrücklich falschen Snapshot-/Checkpointflags. Legacy-Booleans bleiben kompatibel, beweisen aber keine Vollständigkeit.
- Verifier: fehlendes Genesis im ganzen Store ist LinkBroken; expliziter Zeit-Suffix bleibt Partial. Leere Logs, entfernte Suffixe/ganze Logs und neu berechnete Ketten erhalten keinen Vollständigkeitsnachweis. Lesefehler werden sanitisiert als ReadFailed ausgegeben; Cancellation bleibt Cancellation. README/MIGRATION angepasst.
- Regression: neue Verifier-Matrix zunächst 11 rot/11 grün, nach Fix 22/22. Echte Redis-Korruptionsfälle prüfen zusätzlich ReadFailed; HTTP-Dashboard-Regression prüft strukturierten Fehler ohne Canary. Vollständiges dotnet test Noelia.slnx -c Release: 138 Core + 3379 Infrastructure = **3517/3517**, 0 skipped.
- CP: expliziter unabhängiger Wire-Adapter CollectedAuditEvidence, keine optimistischen Legacy-/Enum-Defaults. FleetPage zeigt Konsistenz, Lesefehler und begrenzte Vollständigkeit getrennt. Neue Attestationen enthalten Audit-Caveat; ChainVerified nur bei explizitem Consistent/Broken true/false, sonst null. Keine historischen Signaturen umgeschrieben.
- CP-Regressionen: erste 11 Adapterfälle vor Fix rot; nun 13 Fälle einschließlich ungültiger String-Version und Partial. Zusätzliche Exportassertions deckten 3 Fehler auf (ReadFailed/Unknown/Legacy), nach Fix grün. Gesamtsuite **187/187** gegen veröffentlichtes 6.4.0 UND **187/187** gegen lokalen Kandidaten, 0 skipped; jeweils Exit 0.
- Kandidat: dotnet pack erzeugte alle 14 Pakete plus Symbole als **6.4.1-security.20260928.1** im ignorierten demo/.local-feed/security-20260928.1. CP-Test isoliert mit NoeliaPackageVersion, RestorePackagesPath und --artifacts-path unter /tmp/noelia-audit-candidate-7l16Ij; kein Cache-Purge, kein Publish, keine Defaultversion geändert.
- Chromium-Smoketest eng/browser-smoke.mjs: echte lokale CP-Sammlung aller vier synthetischen Audit-Zustände, Services-HTML und JSON, Header/CSP/Local-Notice, keine Browserfehler, unerlaubtes Inline-Script blockiert. Exit 0; Screenshots unter /var/folders/xs/0h1p8vws0xqbx0pvrwc7d7ch0000gn/T/noelia-browser-smoke-szARLW; ReadFailed visuell geprüft. Nicht gleichbedeutend mit echtem Providerfehler im Browser.
- Echter Schema3Producer aus Kandidatenpaketen mit InMemory-Audit gebaut (0 Warnungen/Fehler); Browser gegen normalen CP ebenfalls Exit 0. Geprüft: echter Schema-3-Report, getrennte Deklaration/Policy, echte Audit-Evidenz aus Kandidat, Services/JSON mit Consistent und Completeness Unknown. Keine externen Dienste oder reale Credentials verwendet.
- Visuelle Nachkontrolle deckte noch eine widersprüchliche Services-Fußnote auf („Every chain ... recomputed“ trotz ReadFailed). Regression zuerst 4/13 rot; Fußnote beschreibt jetzt nur die angefragte Verifikation. Danach vollständige CP-Suiten erneut **187/187** jeweils public/candidate, 0 skipped; beide Browserläufe erneut Exit 0. Aktuelle Screenshots XSBifD (ReadFailed) und WpNDrr (echter Kandidat Consistent) visuell geprüft; keine Browserfehler. Rote Darstellung von Coverage Complete bleibt bekannter AP15-UI-Punkt.
- Offene Grenzen: keine stabilen Snapshots, unabhängigen Checkpoints oder Vollständigkeitsgarantie; CP-Historie/Alarme/Attestation-Formatmigration noch offen. Noelia-Audit-Übersicht verwendet noch pauschales intact/broken für Write-Time-Status. Neue komplette Demo-Docker-Matrix nicht ausgeführt (letzte AP05-Matrix siehe unten). git diff --check in beiden Repositories sauber. Keine Commits, Pushes oder Veröffentlichung.

### 27.09.2026 – AP06 – IN ARBEIT, stillen Verlust beim Redis-Lesen schließen
- Neue `RedisAuditReadIntegrityTests` gegen echte eigene Redis-Testcontainer (eindeutiger Key-Prefix pro Test). Zehn Fälle vor Fix **10/10 rot**, danach **10/10 grün**, 0 skipped: fehlender erster/mittlerer/letzter/alle Payloads bei erhaltenem Index, leer, JSON-null, malformed und gültiger Payload unter falscher Index-ID.
- `RedisSovereignAuditSink.ReadAsync` läuft jetzt indexbezogen über Payloads und wirft InvalidDataException statt still weiterzugehen. JSON-null und ID-Abweichung ebenfalls Fehler; JSON-Parserfehler werden in sichere Kategorie/Indexposition übersetzt, ohne Raw-Payload oder innere Parserexception. Kein automatisches Reparieren/Löschen von Bestandsdaten.
- Migration dokumentiert: teilweise gelesene Einträge nach einer Exception sind kein erfolgreich vollständiges Ergebnis. Keine neue öffentliche DTO-Form in diesem Teilschritt, bestehender Verifier propagiert den Lesefehler; explizite Unknown/Partial-/ReadFault-Semantik und CP-Darstellung gehören zum nächsten Teilschritt.
- Elfter Test ergänzt: 501 gültige Einträge in richtiger Reihenfolge, zeitlicher Suffix und fehlender letzter Eintrag nach der 500er-Seitengrenze. Vollständiger Lauf `dotnet test Noelia.slnx -c Release -m:2 --logger 'console;verbosity=quiet'` (71446) **3504/3504 grün**, 0 skipped, Exit 0 (138 Core + 3366 Infrastructure), inklusive aller elf neuen Redis-Fälle. Beide Repositories abschließend git diff --check sauber.
- Nicht geschlossen: entfernte Indexeinträge, komplett geleerter Store, ungeprüfter Genesis-Link, abgeschnittene/recomputierte Ketten, stabile Snapshotgrenze/konkurrierende Reads und unabhängiger Checkpoint. AP06 bleibt ausdrücklich IN ARBEIT. Kein neuer NuGet-Kandidat erzeugt; AP05-Dockerartefakte enthalten diesen Fix noch nicht.

### 27.09.2026 – AP05 – ERLEDIGT, reale Demo-/CP-/Browserabnahme
- Wiederholung Sitzung 75462 mit Exit 0 beendet. Alle zwölf Hosts aus lokalem Kandidaten 6.4.1-security.20260927.1 gebaut/gestartet, Paketversionen im veröffentlichten .deps.json geprüft. Exakte acht Rollenbaselines in Development/InMemory sowie Staging/Production/Valkey PASS, JSON-Routenschutz und Gateway-HTML-Sperren PASS.
- Sechs frische CP-Prozesse sammelten jeweils eine reale Demo-Flotte über den nginx-Edge: micro-/mono-dev, micro-/mono-staging, micro-/mono-prod. Jeweils alle erwarteten Services erreichbar, Coverage Complete, Role composition Match, explizite v3-Egress-Daten, ObservedCalls null. Ein-Flotten-/Drei-Service-Free-Limit eingehalten, keine Lizenzänderung.
- Chromium: Overview, Egress und Composition je Flotte (18 Seiten), HTTP 200, no-store, Local-Evaluation-Hinweis, Rollenanzeige und keine Console-/Pageerrors. Screenshot cp-micro-prod/composition.png zusätzlich visuell geprüft: erwartete Rollendifferenzen getrennt von Match-Beurteilung, begrenzte Aussage sichtbar. Rot gefärbter Coverage-Hinweis auch bei Complete ist eine UI-Semantikauffälligkeit für AP15, kein Collectionfehler.
- Aktueller CP Release-Build 0 Warnungen/Fehler; öffentliche CP-Suite nach Wiederaufnahme erneut **174/174**, 0 skipped. Zuvor Kandidat ebenfalls 174/174, Demo 73/73 auf beiden Paketpfaden, Frontend 9/9. Keine Tests übersprungen oder fehlgeschlagene unterbrochene Läufe als Erfolg gerechnet.
- Reproduktion: CP Release bauen; aus Noelia/demo `NOELIA_CP_SMOKE_MODULE=/absolut/NoeliaControlPlane/eng/demo-live-smoke.mjs PLAYWRIGHT_MODULE=/absolut/Noelia/demo/src/frontend/node_modules/playwright/index.mjs node eng/compose-report-smoke.mjs VERSION /src/.local-feed/EINDEUTIGER_FEED`. Baseline-/Kandidaten-Anleitung in demo/README.md. Temporäre Artefakte sind Belege, keine Voraussetzung für Fortsetzung.
- Berichte, sechs Screenshots und synthetische CP-Daten: `/var/folders/xs/0h1p8vws0xqbx0pvrwc7d7ch0000gn/T/noelia-compose-reports-0E9DVx`. Nur eigene UUID-Testcontainer/-netze/-volumes automatisch entfernt; Container-/Volumeliste anschließend leer. Images und Berichte behalten; keine Nutzerdaten gelöscht. Keine Commits/Pushes/Publishes.
- Grenzen: selbstsigniertes Demo-TLS explizit akzeptiert; keine TLS-Trust-, OIDC-Deployment-, Login/Logout-/Anwendungs-E2E-, Persistenz- oder Auditvollständigkeitsabnahme. Diese bleiben AP06–AP18. AP05 ist nur im dokumentierten Scope abgeschlossen.
- Nächster Schritt AP06: RedisSovereignAuditSink.ReadAsync verschluckt fehlende/null Payloads; AuditChainVerifier akzeptiert den ersten PreviousHash ungeprüft und hat keinen unabhängigen Vollständigkeitsanker. Zuerst Regression gegen echten isolierten Redis-Store, dann gezielte Korrektur; neue Report-/CP-Semantik anschließend ausdrücklich versionieren.

### 27.09.2026 – AP05 – Demo-Rollenbaselines und reproduzierbare Compose-Abnahme
- `demo/eng/composition-baselines.json`: acht explizite Sollsets (Gateway/Issuer/Verifier/Monolith × InMemory/Redis), nicht aus Laufzeitberichten gelernt. Development-Tests vergleichen vier echte Host-Berichte mit diesen Sollsets. Drei neue Tests; vollständige Demo-Suite jetzt **73/73**, 0 skipped, sowohl veröffentlichte 6.4.0 als auch Kandidat 6.4.1-security.20260924.2 (37 Demo.Tests + 7 Monolith + 29 Paket-Probes).
- CP Development-Konfiguration: sechs Demo-Flotten mit passenden Rollen und acht Baseline-Kopien; Contoso bleibt bewusst ohne erfundene Sollcomposition. Neuer Konfigurationstest erst rot, danach komplette öffentliche CP-Suite **174/174**. `node eng/check-demo-baselines.mjs ../Noelia/demo/eng/composition-baselines.json` → PASS. Diese Sollsets belegen keine Security-Wirksamkeit.
- Dockerfile verwendet optionales NOELIA_VERSION konsistent bei Restore und Publish; Compose reicht es an alle Hosts weiter. Edge-Ports optional über NOELIA_HTTP_PORT/NOELIA_HTTPS_PORT (Default 8080/8443, 0 für dynamische Ports). Root-Version und veröffentlichte Defaultpakete bleiben unverändert.
- Neuer eindeutiger lokaler Kandidat **6.4.1-security.20260927.1**, 14 Pakete plus Symbole mit `dotnet pack Noelia.slnx -c Release -p:Version=6.4.1-security.20260927.1 -o demo/.local-feed/security-20260927.1` erfolgreich erzeugt. Bestehende Feed-Pakete/Cache nicht überschrieben oder gelöscht, nichts veröffentlicht.
- Echter Gateway-Dockerbuild mit dieser Version und lokalem Feed erfolgreich: `noelia-demo-gateway:hardening-20260927`. Laufzeitabnahme nicht allein daraus ableiten.
- Neues `demo/eng/compose-report-smoke.mjs`: baut/startet alle Stages mit UUID-Projektnamen, frischen synthetischen Credentials, ohne bestehende .env, dynamischen Loopback-Ports. Prüft tatsächliche .deps.json-Paketversionen aller Hosts, Schema 3, exakte Rollen, JSON-/Audit-Routenschutz und Gateway-HTML-Sperre am echten Edge. Reproduzierbarer Aufruf aus demo: `node eng/compose-report-smoke.mjs VERSION /src/.local-feed/EINDEUTIGER_FEED`. Nur eigene frische Testcontainer/-netze/-volumes werden danach entfernt, Berichte/Images bleiben. Keine CP-/Browser-/TLS-Trust-/Persistenz-/Auditvollständigkeitsabnahme daraus ableiten.
- Erster vollständiger Compose-Smoke **PASS**, Exit 0: alle zwölf Hosts durch echten nginx-Edge, alle acht Rollen inklusive Redis/Valkey, tatsächliche Paketversion 6.4.1-security.20260927.1 in jedem .deps.json, Schema 3, falscher/fehlender Operator-Header auf beiden JSON-Routen in Staging/Production → 404, richtiger → 200, Gateway-HTML/Assets → 404. Frische synthetische Container/Netze/Volumes des Projekts `noelia-report-smoke-ad3af5bf-c687-4ebc-a059-f069dd79919a` automatisch entfernt; Berichte unter `/var/folders/xs/0h1p8vws0xqbx0pvrwc7d7ch0000gn/T/noelia-compose-reports-U8IQGB` bleiben. CP-Kandidatensuite ebenfalls **174/174**, 0 skipped.
- Berichtsinhalte bleiben begrenzt: Redis-RateLimit-Readmodel Unavailable, ObservedCalls Unavailable/null. Einzelne SecurityCheck-Texte (u.a. AI-/Keyring-/Auditversprechen) sind trotz erfolgreicher Collection noch Gegenstand AP06/AP07/AP13; keine pauschale Security-Freigabe.
- Ergänzt: CP `eng/demo-live-smoke.mjs`, optional aus Compose-Smoke über NOELIA_CP_SMOKE_MODULE. Pro Demo-Flotte ein frischer CP-Prozess mit isoliertem ContentRoot (respektiert Ein-Flotten-Free-Limit), eigene Datenpfade, aktuelle checked-in Demo-Rollen, dynamische Edge-Adressen, synthetisches Secret nur im Prozess. Echte Collection plus Chromium Overview/Egress/Composition. Selbstsignierte TLS-Zertifikate nur explizit im lokalen Dev-Modus erlaubt; kein TLS-Trust-Nachweis. Noch laufende Abnahme: Sitzung 70597, Projekt `noelia-report-smoke-22aaecf8-1769-4f71-816e-d83b24408ba3`. Ergebnis vorerst offen.
- Wiederaufnahme 27.09., 16:03 UTC: Sitzung 70597 nicht mehr vorhanden, frühere temporäre Smoke-Verzeichnisse ebenfalls nicht mehr vorhanden. Reproduzierbare CP-/Compose-Abnahme daher neu gestartet (75462, Projekt `noelia-report-smoke-7780a2b3-4bcc-4aca-9f5d-f065feb51682`). Aktueller CP Release-Build erfolgreich mit 0 Warnungen/Fehlern. Frontend-Unit-Tests zuvor erneut **9/9**, beide git diff --check sauber. Ergebnisse der noch laufenden Wiederholung unten ergänzen.

### 27.09.2026 – Wiederaufnahme nach Unterbrechung; Demo-Änderungen vom 24.09. gesichert
- Abschluss der Wiederholung: `dotnet test Noelia.slnx -c Release -m:2 --logger 'console;verbosity=quiet'` → **3493/3493**, 0 skipped (138 Core + 3355 Infrastructure). `frontend-report-smoke.mjs` gegen genau das unten genannte Image → PASS; drei Stage-Indexseiten, Gateway-HTML/Assets an nginx 404, keine unerwarteten Browserfehler. Screenshots `/var/folders/xs/0h1p8vws0xqbx0pvrwc7d7ch0000gn/T/noelia-frontend-smoke-3nRNXA`, Production visuell geprüft. Eigener Wegwerfcontainer entfernt; WorkerTransfer nicht verändert. Diese beiden Restprüfungen sind jetzt abgeschlossen, vollständige Compose-/CP-Abnahme nicht.
- Visuelle Restauffälligkeit: Der separate Operator-Dashboardindex wirkt weitgehend ungestaltet (Browserstandard-Typografie). Links, Stage-Hinweis und Sperren funktionieren; der funktionelle Smoke ist keine visuelle Designabnahme. Bei AP15 mit prüfen, ohne es als Sicherheitsfehler oder fehlgeschlagene Collection umzudeuten.
- Beide HEADs unverändert gegenüber Baseline; beabsichtigte lokale Änderungen noch vorhanden. Ausstehende Tool-Sitzungen 61136 (Frontend-Browser) und 47560 (Noelia-Gesamtsuite) nicht mehr verfügbar. Deren Ergebnis wird NICHT als Erfolg angenommen; beide Prüfungen neu gestartet.
- Demo `DemoDependencies.cs` und `UseDemoProviders`: nur User-Service/Monolith deklarieren SQLite-Sessions; nur Tokenleser Revocation. Gateway-Ziele aus statischen Ocelot-Routen, normalisiert und dedupliziert. Redis-Providerparser statt Split(':') berücksichtigt mehrere Endpunkte/IPv6 und übernimmt keine Credentials. Kein Redis-Host mehr als vermeintlich geschütztes HTTP-Ziel deklariert. Lokale Ressourcen haben ausdrücklich nur localhost-Lokalitätsmarker; keine HTTP-Server-/Traffic-Behauptung. Route-Änderungen brauchen für aktuelle Deklaration einen Restart.
- `OperatorReports` in DemoEnvironment: Operator-Secret zwingend, nur `/noelia/report.json` und `/noelia/audit-chain.json`; HTML/Assets bleiben selbst mit Secret 404. Staging/Production-Gateway komponieren dadurch den Report, Production mit Begründung. Compose mountet Operator-Secret als Datei auch am Gateway, keine JWT-Schlüsselrechte erweitert. nginx routet auf dedizierten Gateway-TLS-Hosts ausschließlich zwei exakte JSON-Pfade; alle anderen 404. CP Development-Fleets enthalten jetzt Gateway auch in micro-staging/micro-prod.
- Gateway-Assemblymarker und echter TestHost ergänzt: gültiger/falscher/fehlender Header, JSON-/Chain-Zugriff, gesperrte HTML/Assets/zusätzliche Pfadsegmente, richtige Downstream-Hosts und keine erfundenen Sessions/Revocation. Echte User-/Todo-Reports prüfen getrennte Session-Deklaration; dieser Test vor Fix rot. Zusätzliche Provider- und Route-Deklarationstests mit synthetischen Credentials.
- Demo-Suite gegen lokalen Kandidaten 6.4.1-security.20260924.2 **70/70**, 0 skipped (35 Demo.Tests, 6 Monolith, 29 Paket-Probes). Separater Wiederholungslauf gegen veröffentlichte 6.4.0 ebenfalls **70/70**, 0 skipped. Keine ProjectReferences aus der Demo ins Framework. Isolierte Outputs `/tmp/noelia-schema3-3mEFET/demo-candidate` und `demo-public`.
- Erster Kandidaten-Restore mit bloßem RestoreSources scheiterte korrekt am bestehenden PackageSourceMapping. Funktionierender Befehl aus Noelia: `NOELIA_SOURCE=/tmp/noelia-schema3-3mEFET/feed dotnet test demo/Noelia.TodoDemo.sln -c Release -m:2 -p:NoeliaVersion=6.4.1-security.20260924.2 -p:RestorePackagesPath=/tmp/noelia-schema3-3mEFET/cache -p:RestoreConfigFile=/Users/davidozturk/Projects/Noelia/demo/NuGet.Docker.Local.Config --artifacts-path /tmp/noelia-schema3-3mEFET/demo-candidate --logger 'console;verbosity=quiet'`. Für neue Runs eindeutigen Feed/Version verwenden; normale Veröffentlichung bleibt unverändert. Dashboard-Probe lokalisierte ihr csproj zunächst fälschlich relativ zu bin; CallerFilePath korrigiert den Test für isolierte Outputs. Ein versehentlich überlappender zweiter Gesamtlauf wurde gezielt beendet; der abschließende Lauf mit -m:2 lief allein und vollständig grün.
- Frontend-Dashboardindex erklärt JSON-only-Gateway statt fehlendem Modul; drei neue JS-Regressionen, `npm test` **9/9**. Demo-README aktualisiert. Frontend-Demo-Zertifikat ergänzt SANs der TLS-Operatorhosts und dokumentiert Image-/Cache-Grenze. Image `noelia-demo-frontend:hardening-20260924-reports`, inspect-ID `sha256:a1552a4096bcae6e7c4204e350eea6f08c4924b5e750357c27bf2fd00e0c1ab6`. Build grün; `docker compose --profile all config --quiet` und nginx -t grün. Dies ist KEINE vollständige Stack-Abnahme.
- Dauerhaftes `demo/eng/frontend-report-smoke.mjs` startet eigenen Frontend-Container mit dynamischen Loopback-Ports, prüft Chromium-Indexseiten in drei Stages und nginx-404 für Gateway-HTML/Assets; beendet nur eigenen Container. Befehl: `PLAYWRIGHT_MODULE=/Users/davidozturk/Projects/Noelia/demo/src/frontend/node_modules/playwright/index.mjs node demo/eng/frontend-report-smoke.mjs noelia-demo-frontend:hardening-20260924-reports`. TLS für diesen isolierten UI-Test ausdrücklich selbstsigniert/ignoreHTTPSErrors; Backend-Proxy-/Auth-Integration muss separat abgenommen werden.
- Offen: konkrete rollenbezogene Demo-Sollsets, vollständige Compose-/CP-Anbindung gegen identifizierten Kandidaten, echte Redis-Stage-/Browsermatrix. AP05 bleibt IN ARBEIT; AP14/AP17 nicht erledigt. Keine Commits/Pushes/Publishes, echte Secrets/WorkerTransfer nicht geändert.

### 24.09.2026 – AP05 – Rollenspezifische Sollcomposition geprüft
- CP `CompositionAssessment.cs` ergänzt: versionierte Auswertung schema 1 neben neutraler `drift`-Differenzliste. Fleet `CompositionRoles` definiert exakte, case-sensitive laufende Modulsets; Service `CompositionRole` weist sie ausdrücklich zu. Keine automatische Rollenableitung; keine Beziehung zu OIDC-/Benutzerrollen. Nichtleere eindeutige Definition erforderlich, leere Baseline in diesem ersten Format bewusst Unknown.
- Gesamtzustände Match/Different nur bei vollständiger Collection und Auswertung aller konfigurierten Mitglieder; Partial bei lückenhafter Bewertung mit mindestens einem ausgewerteten Mitglied, sonst Unknown. Pro Mitglied UnknownBaseline, NotCollected, UnknownComposition, Match oder Different; fehlende und unerwartete Module getrennt. Rollen/erwartete Modulsets werden credential-frei kopiert; spätere Optionsänderungen verändern die Sollmenge dieses Views nicht.
- Collector prüft explizite `composition.modules`-Liste und `isRunning`-Booleans auf dem Wire. Fehlende Felder werden nicht als leere/false-Beobachtung ausgegeben. Explizite leere Reportliste kann als fehlende Sollmodule bewertet werden; fehlende Liste bleibt unknown. Grundlegende JSON-/Shape-Fehlerisolierung weiterhin AP09, nicht erledigt.
- HTTP- und Recorder-Pfad verwenden dieselbe Safe-Projection aus vollständiger Fleetkonfiguration. Neue JSON-Property `compositionAssessment`, eigene Tabelle auf `/drift`, Status im Overview und begrenzte signierte Caveats. Historische Baselinekontinuität/Alarmierung nicht implementiert (AP08/AP10). README/MIGRATION mit Konfiguration, Statussemantik und Grenzen aktualisiert.
- Ein HTTP-Regressionstest vor Implementierung rot; danach 24 zusätzliche Fälle gegenüber 149er-Baseline: Mono/Gateway/Service dürfen unterschiedliche passende Sets haben; exakte Unterschiede; fehlende/duplizierte/leere Baselines; stale/unsupported/missing/omitted/duplicate Reports; Wire-Defaults; Konfigurationsbinding; HTML-Encoding; credential-freie Ausgabe; kopierte Sollmengen. Echter HTTP-Fall mit 4 konfigurierten/3 erlaubten Diensten bleibt Partial und behält das Omitted-Mitglied. Fünf erste Wire-Tests hatten versehentlich text/plain statt application/json und wurden vom Collector korrekt abgewiesen; Fixture korrigiert und explizite Collection-Erfolgsassertions ergänzt.
- Abschließende CP-Gesamtsuite normal **173/173**, 0 skipped; gegen lokalen Noelia-Kandidaten `6.4.1-security.20260924.2` mit isoliertem Cache/Artifacts ebenfalls **173/173**, 0 skipped. Noelia-Quellcode in diesem Schritt unverändert; dessen letzte vollständige Suite weiterhin 3493/3493 aus vorherigem Schritt, nicht erneut als ausgeführt behaupten.
- Browser-Smoke um `/drift`, echte Rollen-Konfigurationsbindung und JSON-Auswertung erweitert. Standard-Fixture: Match; tatsächlicher Schema-3-Paketproducer ohne Baseline: Unknown. Beide Läufe PASS, keine unerwarteten Browserfehler, CSP/Headers/Zeitzonen weiter geprüft. Synthetische Screenshots zuletzt `.../T/noelia-browser-smoke-MKXl1N`, Paketproducer `.../T/noelia-browser-smoke-JVHkVE`; vollständiger Temp-Präfix wie im vorherigen Eintrag. Darstellung der Rollen-Tabelle visuell geprüft.
- Neues Image `noelia-control-plane:hardening-20260924-roles`, inspect-ID `sha256:d513b522887f64df970693c75c0bd02164ce799fc6c37dfeeee9859b03bf823e`; Build und `python3 eng/image-smoke.py noelia-control-plane:hardening-20260924-roles` PASS (non-root, Persistenz/Schlüssel nach Restart und Ersatz, fail-closed). Eigene Smoke-Ressourcen entfernt; Paketproducer auf 61085 wird nach diesem Smoke gezielt beendet. Kein Publish/Push/Commit; git diff --check beide sauber.
- AP05 bleibt IN ARBEIT; nächste Aktion Demo-Anbindung wie oben. WorkerTransfer-Konfiguration weiterhin unberührt.

### 24.09.2026 – AP05 – Schema-3-Vertrag und Paket-/Browser-Abnahme umgesetzt, AP05 weiterhin IN ARBEIT
- Noelia `OperatorReport` liefert schema 3: deklarierte Dependencies, explizite `HttpEgress`-Policy-Metadaten und `ObservedCalls` getrennt. Providerfreier `IHttpEgressPolicyReport` wird durch `AddNoeliaEgressPolicy` registriert; eine allein registrierte Policy behauptet keinen installierten Guard. Factory-Scope, RegistrationAndConfiguration und Redirect-Konfiguration sind Selbstauskunft, kein Wirksamkeits- oder vollständiger Netzwerknachweis.
- Fehlende/defekte Dependency- und Guard-Reports unabhängig behandelt; Fehlertexte enthalten keine Exception-Payload. Kein Traffic-Observer implementiert: ObservedCalls bleibt Unavailable und Count null, niemals erfundener Nullverkehr. Dashboard zeigt die drei Bereiche getrennt, einschließlich Policy ohne Dependency-Provider. README/MIGRATION und API-Dokumentation angepasst; VersionPrefix unverändert 6.4.0.
- Zwei Noelia-Wire-Regressionen zuerst rot, danach grün. Insgesamt sechs neue `EgressReportContractTests`; fokussierte Dashboard-/Redirect-Auswahl 75/75. Gesamtsuite abschließend `dotnet test Noelia.slnx -c Release` → 138 Core + 3355 Infrastructure = **3493/3493**, 0 skipped; auch nach Pack und Wiederherstellung der Standard-Restoreausgabe grün. Keine neue Runtime-Abhängigkeit.
- CP `CollectedEgressEvidence` ist expliziter Wire-Adapter: schema 1/2 nur LegacyPolicyFlag ohne erfundenen Scope/Redirectbeleg; schema 3 verlangt konsistente explizite Metadaten. Fehlende/inkonsistente neue Metadaten führen zu InvalidReportContract/Partial statt Legacy-true-Fallback. UI/JSON und signierte Caveats verwenden normalisierte Evidenz. Strukturierte unveränderliche signierte Payload bleibt AP08.
- Sieben neue Wire-Kompatibilitätsfälle. CP-Gesamtsuite gegen unveränderte öffentliche 6.4.0-Abhängigkeit **149/149**, 0 skipped. Über opt-in `NoeliaPackageVersion` dieselbe Suite gegen lokalen neuen Paketkandidaten ebenfalls **149/149**, 0 skipped; isolierte Outputs, keine ProjectReference und kein globaler Cache-Purge. Der normale Checkout/Image-Build benötigt keine unveröffentlichte Preview.
- Tatsächlich alle 14 Noelia-Pakete plus Symbole lokal mit `dotnet pack Noelia.slnx -c Release -p:Version=6.4.1-security.20260924.2 -o /tmp/noelia-schema3-3mEFET/feed` erzeugt. Paket-Consumer `NoeliaControlPlane/eng/fixtures/Schema3Producer` ausschließlich aus diesem Feed plus nuget.org restauriert, eigener Cache `/tmp/noelia-schema3-3mEFET/cache`. Assets für Abstractions/Dashboard/Infrastructure bestätigen Kandidat `.2`. Frühere explorative `.1`-Pakete sind nicht der abgenommene Kandidat. Keine Veröffentlichung; Versionswahl ist keine Releaseentscheidung.
- Echter Producer auf dynamischem Loopback-Port lieferte getrennt allowed-only.internal und declared-only.internal, Beobachtungszahl null. `NOELIA_PRODUCER_URL=http://127.0.0.1:58262/noelia PLAYWRIGHT_MODULE=/Users/davidozturk/Projects/Noelia/demo/src/frontend/node_modules/playwright/index.mjs node eng/browser-smoke.mjs` → PASS. CP mit veröffentlichtem Basis-DTO sammelt echten Schema-3-Bericht korrekt; Overview/Egress/History/Attestation und Noelia-Sovereignty-Seite besucht. Keine unerwarteten Browserfehler, CSP/no-store/Zeitzonen/Localmodus geprüft; absichtliches Inline-Script blockiert. Screenshots visuell geprüft. Artefakte: `/var/folders/xs/0h1p8vws0xqbx0pvrwc7d7ch0000gn/T/noelia-browser-smoke-XGJ6tC`.
- Aktuelles CP-Image `noelia-control-plane:hardening-20260924` erfolgreich gebaut, `docker image inspect` ID `sha256:0695c8eee0b64b86d20aaab8cb46998c2b160610c7b2bdc83385e050b2fcb985`. `python3 eng/image-smoke.py noelia-control-plane:hardening-20260924` → PASS: non-root, private persistente Daten, Restart UND Containerersatz mit Daten-/Schlüsselerhalt, expliziter Localmodus, fail-closed ohne Auth. Image verwendet veröffentlichtes 6.4.0-Basispaket.
- Reproduzierbare vollständige Anleitung im CP-Repo: `eng/fixtures/Schema3Producer/README.md`, verlinkt in `docs/WEITERARBEIT.md`. Neue Runs erzeugen eigene Kandidaten/Verzeichnisse; keine Abhängigkeit von obigen temporären Artefakten. Eigene Browser-CP automatisch beendet, Producer PID 68569 nach Prozessprüfung gezielt beendet, eigene Image-Smoke-Ressourcen automatisch entfernt; keine fremden Daten gelöscht.
- Noch offen: Rollenbaseline, echte Demo-Dependencies/Gateway und vollständige Consumer-Matrix. Collector-Größenlimits, per-Member-JSON-/Shape-Fehlerisolierung und Identitäts-/TLS-Evidenz bleiben AP09 (JsonDocument derzeit noch unbounded); keine Behauptung, Collector sei umfassend abgesichert. Legacy-Historien-/Alarmsemantik AP08/AP10 und Lizenzkonsistenz AP12 ebenfalls offen.
- Nächster konkreter Schritt steht oben; dieser Eintrag ersetzt die am 23.09. genannten offenen Schema-3-/Paketadapter-Schritte. Keine Commits, Pushes oder Publishes; WorkerTransfer-Konfiguration unberührt.

### 23.09.2026 – AP05 – IN ARBEIT, CP-Coverage und begrenzte Aussagen umgesetzt
- Acht neue EvidenceScope-Regressionen vor Fix alle rot, danach grün: fehlender Service, Absent/Faulted/Unknown-Schema trotz true-Flag, Allow ohne Dependency, private Adresse ohne Besitz-/Rechtsnachweis, leere SecurityChecks ohne All-pass. Zwei weitere Fälle sichern JSON/CSV/signierte Caveats und neutrale Composition-Differences-Bezeichnung.
- `EvidenceScope.cs` bündelt Grenzen. UI sagt deklarierte Dependencies statt vollständige Reachability; kein All-pass bei leerer Findingsliste. `/drift` bleibt kompatible Route, heißt im UI Composition differences, keine validierte Rollenabweichung.
- Neue `FleetCoverage` schema 1 in Fleet JSON: bekannte konfigurierte Mitgliedschaft vor Lizenztrim, Attempted/Collected/Usable/Missing/Omitted und Status je Target (Name+Adresse, ohne Secret). Unknown bei fehlender/leerer/duplizierter Sollmenge; Partial bei fehlenden/ausgelassenen/doppelten/unerwarteten/alten/unsupported Antworten. Complete ist nur Collection-Coverage der konfigurierten Flotte, keine Sicherheits-/Netzwerkvollständigkeit oder Identitätsprüfung.
- Initiale Frischegrenzen: höchstens 5 Minuten alt, höchstens 1 Minute künftig relativ zu CollectedAt; fehlender Zeitpunkt unknown. Konfigurierbare Grenzen/Identity-/Shapevalidierung AP09. Unkonfigurierte Dienste werden ausdrücklich nicht entdeckt.
- `Program.CollectAsync` reicht alle konfigurierten Target-Identitäten vor `Take(MaxServices)` ein; Recorder ebenso. HTTP-Regression: 4 konfiguriert, 3 abgefragt, 1 Omitted, State Partial; keine stille Kürzung. Legacy EgressIsEnforcedEverywhere setzt nun Complete-Collection plus present/enabled HTTP-Guard-Claims voraus.
- Coverage-Summary in UI, neuen signierten Caveats und CSV-Zeilen. Caveats nennen jede nicht gesammelte Zielidentität samt Status. CSV bekommt zwei neue Endspalten scope/coverage. Bestehende Signaturformate nicht umgeschrieben; strukturierter unveränderlicher signierter Coverage-Snapshot bleibt AP08.
- Vier Wire-Schema-Tests ergänzt: fehlendes schemaVersion vor Fix irrtümlich 2 (1 Test rot), explizite 1/2/999 korrekt. Reader-CreateObject setzt Default nun auf unbekannt 0. Abschließende CP-Gesamtsuite: `dotnet test tests/Noelia.ControlPlane.Tests -c Release --no-restore --logger 'console;verbosity=quiet'` → 142/142, 0 skipped. Browser-Fixture liefert explizit Schema 2 und prüft tatsächliche Complete-Coverage.
- Noelia `OperatorReport.cs` XML-Vertrag sprachlich korrigiert; noch KEINE neue Reportform/Version oder NuGet-Veröffentlichung. Noelia-Suite nach XML-Änderung erneut 3487/3487, 0 skipped. CP referenziert weiterhin öffentliches Noelia.Abstractions 6.4.0.
- Abschließender Browser-Smoke nach Wire-Schema-Korrektur grün: `PLAYWRIGHT_MODULE=/Users/davidozturk/Projects/Noelia/demo/src/frontend/node_modules/playwright/index.mjs node eng/browser-smoke.mjs`. Screenshot/synthetische Daten `/var/folders/xs/0h1p8vws0xqbx0pvrwc7d7ch0000gn/T/noelia-browser-smoke-Vu3FTS`; Script reproduzierbar ohne diese Dateien. Vollständige Demo-Integration weiterhin NICHT neu ausgeführt.
- Letztes Image aus genau diesem CP-Stand gebaut: `noelia-control-plane:hardening-20260923`, `docker image inspect` ID `sha256:3064ff59718c7ca3b89f303911180182385167c4a80206c2e27f251a36a8716f`. Anschließendes `python3 eng/image-smoke.py noelia-control-plane:hardening-20260923` ebenfalls grün. Kein Publish, keine Commits/Pushes; git diff --check beide Repositories sauber.
- Noch offen in AP05: Noelia-Reportversion mit expliziter Trennung von Dependencies/Allow-Policy/ObservedCalls und tatsächlichem Guard-Scope; Legacy-Adapter; rollenspezifische Sollcomposition; echte Demo-Dependencies/Gateway-Erfassung; Abnahme über isolierten NuGet-Kandidaten. Nicht beobachtete Calls müssen Unknown bleiben, keine leere Liste als Nullverkehr deuten.
- Fortsetzung konkret: Noelia.Abstractions/Operator/OperatorReport.cs, Dashboard/OperatorReportCollector.cs (Sovereignty), Abstractions/Sovereignty/ISovereigntyReport.cs und Infrastructure/Sovereignty untersuchen; additive Typen/Legacypfad mit API-/Dependency-Guards. CP-Coverage derzeit unterstützt nur Reportschema 1/2 – beim neuen Schema bewusst Adapter/Tests erweitern.
- Historienstrings `enforced everywhere`/`NOT everywhere` sind noch Legacy-Labels, keine vollständige neue Zustands-/Alarmsemantik (AP08/AP10). Recorder-/UI-Lizenzdifferenzen bleiben AP12. Fehlerisolierung/Größenlimits/Null-Shapevalidierung im Collector bleiben AP09.

### 23.09.2026 – AP04 – ERLEDIGT
- SQLite-Operator-Audit implementiert: Start vor Handlerarbeit, Ergebnis vor Serialisierung, keine Query/Payload/Secrets, pseudonyme Actor-ID; Schreibfehler → 503. Journal ist ausdrücklich nicht manipulationssicher, kein Zustellnachweis, keine automatische Retention.
- Zusätzliche negative HTTP-Regression deckte falschen Auditstatus 200 bei Forbid auf (3 Fälle rot); korrigiert auf beabsichtigte 403, Tests wieder grün. Autorisierungsablehnungen vor Route und Recorder werden nicht als Operatorzugriff erfasst.
- CP-Gesamtsuite 114/114, 0 skipped. SQLite-Rekonstruktion, unvollständiger Eintrag, Unixrechte, DataProtection-Entschlüsselung nach Providerneustart, Auditfehler vor/nach Collection, minimaler anonymer Healthprobe und Production-TLS-Sperre geprüft.
- Noelia-Gesamtsuite erneut: 138 Core + 3349 Infrastructure = 3487/3487, 0 skipped; enthält alle 26 Redirectfälle.
- Erstes aktuelles Image gebaut und `python3 eng/image-smoke.py noelia-control-plane:hardening-20260923` grün: non-root, Verzeichnis 700, persistente Datenbanken bei Restart UND Containerersatz, gleicher Signaturschlüssel bei Restart/Ersatz, expliziter Localmodus, verständlicher Auth-Startfehler ohne Config. Nur frische eigene Testcontainer/-volume automatisch entfernt; keine Nutzerdaten darin. CI führt denselben Smoke künftig aus.
- Nach letzter Audit-Statuskorrektur Image erneut gebaut; Wiederholungs-Smoke ebenfalls grün. Damaliger Build-Config-Digest `sha256:f5b097c336f4a5150fb7834e82abf676a7d3f7cbc47cdd4a68bf8df99ae96e44`; Tag inzwischen durch jüngeren AP05-Stand ersetzt (siehe oben). Kein Publish.
- `eng/browser-smoke.mjs` mit bestehendem Demo-Playwright: echter Chromium, eigene synthetische HTTP-Flotte erfolgreich gesammelt, Overview/History/unlizenzierte Attestation 200, lokale Warnung sichtbar, CSP/no-store, History-Zeit lokalisiert, keine Browserfehler, injiziertes Inline-Script blockiert. Eigene Prozesse beendet. Screenshot und synthetische DBs unter `/var/folders/xs/0h1p8vws0xqbx0pvrwc7d7ch0000gn/T/noelia-browser-smoke-rliMNB`; Skript erzeugt alles ohne diese Artefakte reproduzierbar.
- Frühere Browserprobe gegen Demo zeigte nur Unerreichbarkeit, da Demo-Container während der Arbeit nicht mehr vorhanden waren; nicht als erfolgreiche Demo-Integration gewertet. Frühere CP 5199 ebenfalls nicht mehr vorhanden. Eigene zusätzliche CP 5250 (PID 29226) gezielt beendet.
- README/MIGRATION dokumentieren vier Speicherpfade, Schutz/Backup und fehlende At-Rest-Verschlüsselung, Keyerhalt, Bind-Mount-Rechte, Auditgrenzen/503/Retention und Healthprobe. Vollständige Demo-/OIDC-Reverse-Proxy-Abnahme bleibt AP17/AP18.

### 23.09.2026 – AP03 – ERLEDIGT / AP04 begonnen
- Zusätzliche Redirect-Randfälle grün: insgesamt 26/26 fokussierte EgressRedirectTests. Vorheriger Gesamtlauf 3483 grün; vier neue Fälle müssen im nächsten Gesamtlauf nochmals enthalten sein.
- AP04 erste Änderungen: Docker History/Audit/DataProtection-Pfade auf persistentes, nur dem App-User zugängliches Verzeichnis; .dockerignore schließt lokale WorkerTransfer-Konfiguration ohne deren Öffnen aus.
- Globale CP-Header (no-store/CSP mit Hash des einzigen Inline-Scripts/nosniff/Anti-Framing/Referrer), minimaler anonymer /health/live-Probe und Verweigerung untrusted TLS außerhalb expliziter LocalDevelopment-Konfiguration implementiert.
- AP04 ist NICHT fertig: eigenes Audit, Persistenz-/Container-/Browser-Abnahme und Dokumentation noch offen.

### 23.09.2026 – AP03 – Implementierung geprüft, Abschlussmatrix läuft
- CP-Collector folgt keinen Redirects mehr; sein tatsächlicher Transport wird in 5 realen Loopback-HTTP-Tests auf Canary-Weitergabe geprüft, alle grün.
- Durchsetzende Noelia-Policy deaktiviert AutoRedirect nach Named-Client-Konfiguration, sowohl für HttpClientHandler als auch SocketsHttpHandler. Nicht unterstützte PrimaryHandler werden mit klarer Fehlermeldung verweigert. Unbeschränkte Policy behält ihr Transportverhalten.
- 20 Regressionen (301/302/303/307/308 × zwei Transporte × Registrierungsreihenfolge) vor Fix rot, nach Fix grün; unbekannter Transport und unbeschränkter Custom-Transport zusätzlich geprüft.
- Noelia README/SOVEREIGNTY/MIGRATION und CP MIGRATION beschreiben das bewusst geänderte Redirect-Verhalten und die beschränkte HTTP-Reichweite.
- Gesamtläufe: Noelia 138 Core + 3345 Infrastructure = 3483/3483; CP 103/103, jeweils 0 skipped. git diff --check beide sauber.
- Vier ergänzende Noelia-Fälle (Same-Origin-Schleife, Scheme-Wechsel, anderer erlaubter Port, erlaubter Direktaufruf) soeben ergänzt; fokussierter Lauf noch abwarten. Danach AP03 auf ERLEDIGT und AP04 beginnen.

### 23.09.2026 – AP02 – ERLEDIGT
- Fehlende/partielle OIDC-Config bricht Start ab. Lokaler Modus nur explizit AllowLocalDevelopment + Development, ohne OIDC-Konfiguration; echte Peer-/Host-Prüfung, keine Forwarded-Anfragen. HTML-Warnhinweis und Access-Mode-Header.
- Mapping nach UserInfo über OnTicketReceived; externe Claims werden nicht als interne Grants übernommen. MapJsonKey zerlegt Gruppenarrays (ein echter OIDC-Test deckte den JSON-String-Fall von MapUniqueJsonKey auf). Effektive Rollen/Grants dedupliziert, leere Flottenzuordnung gewährt nichts.
- Cookie heißt noelia-control-plane.v2, damit vorher ausgestellte Cookies nicht weitergelten. Reader darf ?verify=1 nicht aufrufen; Ablehnung vor Collectorarbeit.
- README, MIGRATION und lokales Startskript angepasst; keine reale IdP-Konfiguration oder bestehende Instanz verändert.
- Tests: dotnet test tests/Noelia.ControlPlane.Tests -c Release --no-restore → 98/98, 0 skipped. Enthält Config-/Local-Peer-/Rollenfälle, echte HTTP-Routen sowie echte OIDC-Middleware mit kontrolliertem Token-/UserInfo-Backchannel, PKCE, signiertem ID-Token, Callback und Cookie. Zulässige UserInfo-Gruppe erhält Read; unmapped plus eingeschleuste admin/grant-Claims erhält keinen Zugriff.
- Kein externer Kunden-IdP getestet. Test-IdP liefert statische Discovery-Konfiguration und lokale In-Process-Antworten; keine dauerhaften Testschlüssel.
- Noch offene andere Sicherheitsbefunde bleiben ausdrücklich offen. Nächster Schritt: AP03.

### 23.09.2026 – AP01 – ERLEDIGT
- FleetMember ist jetzt ein reines öffentliches Name/Address-DTO. FleetTarget hält den Collector-Auftrag mit internem Secret; kein generierter Record-ToString mit Credentials. Collector und Recorder projizieren sichere Antworten.
- /fleet.json setzt Cache-Control: no-store. Entry-Point für HTTP-Regressionstests geöffnet; Microsoft.AspNetCore.Mvc.Testing 10.0.12 ausschließlich im Testprojekt hinzugefügt.
- CredentialBoundaryTests: 3 Fälle vor Fix rot (200/404/503), danach grün; zusätzlicher Target-Serialisierungs-/ToString-Test.
- Gesamtsuite nach Fix: 73/73; zusätzlicher echter HTTP-Endpunkttest anschließend 1/1: Reader sieht weder globalen noch dienstspezifischen Canary, Upstream erhält beide korrekten Credentials, NoStore gesetzt.
- Keine realen Secrets ausgegeben oder rotiert. Konstruktor/Collector-Input der CP-intern verwendeten Modelle geändert; kein öffentliches Noelia-NuGet-API geändert.
- Nächster Schritt AP02 läuft: Authcode bereits lokal bearbeitet, neue Tests und Dokumentation noch offen.

### 22.09.2026 – AP00 – ERLEDIGT
- Dieser Plan deckt alle 28 Befunde in 19 fortsetzbaren Arbeitspaketen ab.
- Review dauerhaft unter docs/reviews/2026-09-22-noelia-control-plane-analyse.md archiviert; ursprünglicher Prüfstand, keine Behauptung des aktuellen Fixstatus.
- ControlPlane/docs/WEITERARBEIT.md verweist auf den zentralen Plan.
- Keine Quellcodeänderungen in AP00; keine Tests erforderlich, Dateien und Zuordnung geprüft.
- Nächster konkreter Schritt: AP01 Credentials von FleetMember/Responses trennen und Regressionstests hinzufügen.

### 22.09.2026 – Beginn
- Nutzer hat Umsetzung und kontinuierliche schriftliche Übergabe ausdrücklich beauftragt.
- Ausgangsstatus: Noelia clean; CP nur vorhandene untracked WorkerTransfer-Config.
- Vollständiger Review wird neben diesem Plan dauerhaft archiviert.
- Vorherige Testbaseline: Noelia 3461, CP 69, Demo .NET 61, JS 6 grün; E2E erster Lauf57/60, drei Wiederholungen grün; bekannte Fehler bleiben offen.
- Nächste Aktion: AP01.

## Vorlage für jeden weiteren Journal-Eintrag

### Datum – APxx – Status
- Änderung und betroffene Dateien:
- Warum / öffentliche Verhaltensänderung / Migration:
- Regression vorher:
- Tatsächlich ausgeführte Tests und Ergebnisse:
- Noch offen / nicht getestet:
- Laufende Prozesse / geschützte lokale Dateien:
- Nächster konkreter Schritt:
