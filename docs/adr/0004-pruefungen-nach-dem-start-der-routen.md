# ADR-0004 — Prüfungen, die Routen lesen, laufen nach dem Start der Routen

**Status:** Angenommen für `develop` (Fassung 2, Warden-Freigabe nach Delta-Runde) · **Datum:** 2026-11-20 · **Gate:** G-0017 (Rank A) · **Quest:** Q-0056 · **Issue:** #62 · **Ziel:** Patch, keine neue öffentliche API

## In einfacher Sprache

Beim Start führt Noelia seine Sicherheitsprüfungen aus und legt das Ergebnis in den
Bericht (`/noelia/report.json`, Dashboard). Eine dieser Prüfungen, `noelia.m2m.caller-policy`,
liest die gemappten Routen: Welche Türen gibt es, hat jede einen Fallback, gibt es genau
einen Token-Endpoint?

Das Problem: Die Prüfungen laufen, bevor ASP.NET die Routen der Anwendung in den
Container übertragen hat. Die Prüfung sieht darum **null** Routen.

- Der Aussteller meldet deshalb immer Fail („0 token endpoints“), obwohl alles stimmt.
- Ein reiner Empfänger meldet vermutlich Pass über null Türen. Das ist die schlimmere
  Hälfte: ein Pass, der nichts geprüft hat. Eine offene Tür fiele im Bericht nicht auf.

Der Fix: Die Prüfungen laufen noch einmal, **sobald alle Dienste gestartet sind** (dann stehen
die Routen). Bis dahin meldet die Routen-Prüfung weder Pass noch Fail, sondern „Routen noch nicht
gebaut“. Der Bericht hält das zweite Ergebnis. Eine Prüfung, die Routen braucht, darf außerdem nie
Pass über null Türen melden, wenn Empfänger komponiert sind: Sie meldet dann Warning, und echte
Fehler behalten Vorrang.

Was sich für Nutzer ändert: Der Bericht zeigt nach dem Start den wirklichen Stand. Ein
korrekter Aussteller meldet Pass statt Fail. Ein Empfänger mit offener Tür meldet Fail statt
Pass; ein Empfänger ganz ohne Tür meldet Warning statt Pass. Keine neue API, keine Konfiguration.

## Frage

Wann laufen Prüfungen, die vom Routing abhängen, und was darf eine solche Prüfung melden,
wenn sie keine Routen sieht?

## Befund (Warden-Messung, Microsoft.AspNetCore.App 10.0.14)

`SecurityCheckStartupService` ist ein `IHostedService` und läuft in `StartAsync`. Erst
`GenericWebHostService.StartAsync` baut die Pipeline und trägt die Routen in die
`EndpointDataSource` des Containers ein. Ob der Dienst davor läuft, hängt von der
Registrierungsreihenfolge ab: Über `WebApplicationBuilder` (Noelia auf `builder.Services`) ist
`GenericWebHostService` der letzte Dienst, im `StartAsync` sind 0 Routen sichtbar. Beim
`HostBuilder` mit `ConfigureWebHost` gilt dasselbe für innerhalb registrierte Dienste, für danach
registrierte nicht. Gemessen: im `StartAsync` 0, in `IHostedLifecycleService.StartedAsync` und bei
`ApplicationStarted` 2. Gemessen in WorkerTransfer: `startup=Fail | rerun=Pass`. Noelias eigene
Tests riefen die Prüfung nach dem Start auf und sahen es nicht.

## Entscheidung

1. `SecurityCheckStartupService` implementiert zusätzlich `IHostedLifecycleService` (interner Typ,
   keine neue öffentliche API). `StartedAsync` läuft, nachdem alle `StartAsync` gelaufen sind, ist
   asynchron, führt den zweiten Lauf durch und wartet ihn ab, **bevor `app.StartAsync()` zurückkehrt**. Kein
   `async void`, kein sync-over-async.
2. Ein interner Zustand (`routes built`, ein Singleton je Container, nie statisch) wird in `StartedAsync` vor dem
   zweiten Lauf gesetzt. Im ersten Lauf meldet `ServiceCallerPolicySecurityCheck`, unabhängig davon, ob Module
   komponiert sind (eine Tür ohne Modul ist der Grund für Category Composition), Warning „routes not built
   yet, checked again after startup“ statt Pass oder Fail; das Log steht auf Information. So bleibt
   kein bekannt falsches Ergebnis im Bericht, auch nicht, wenn der zweite Lauf scheitert.
3. Fehler im zweiten Lauf werden gefangen und geloggt; eine Exception aus `StartedAsync` bricht
   sonst den Start ab. Der Bericht behält dann den ersten, ehrlichen Lauf (der Code trägt das, weil `Replace` nicht erreicht wird; getestet ist nur, dass der Start nicht abbricht und der zweite Lauf stattfand).
4. Bei komponierten Empfängern und null gemappten Türen wird „no service door is mapped, nothing was
   checked“ ein Eintrag in `warnings`. Fehler (ersetzter Ablehnungs-Handler, 0 oder 2 Token-Endpoints
   beim Aussteller, `AllowAnonymous`) behalten Vorrang und bleiben Fail.
5. Die Pass-Zusammenfassung nennt je Weg nur, was wirklich geprüft wurde: Auf dem Weg „nur Aussteller“
   steht nichts über Handler oder Türen.
6. README (Abschnitt zu „runs them once at startup“) und `MIGRATION.md` (7.1.x, deutsch) nennen den
   Verhaltenswechsel: zwei Läufe, Empfänger ohne Tür ist Warning statt Pass. Das liefert die Folge-Quest
   Q-0065, weil beide Dateien nicht im Scope von Q-0056 liegen.

## Grenze

`StandaloneDashboardSecurityReport` (`src/Noelia.Dashboard`) hält einen zweiten Bericht und führt die
Prüfungen nur in `StartAsync` aus. Es registriert keine eingebaute Prüfung, die Routen liest; nur eine
Prüfung eines Nutzers wäre betroffen. Das liegt außerhalb des Scopes dieser Quest und wird als Folge-Issue
geführt (Nummer: wird vor dem Merge eingetragen). Ein Test hält fest, dass die Prüfung `noelia.m2m.caller-policy` auf diesem Weg nicht registriert ist und keine `EndpointDataSource` besteht; eine künftige Prüfung, die Routen liest, erkennt er nicht.

## Verworfen

- **Erster Lauf entfällt:** Der Vorteil des ersten Laufs ist nur das kurze Fenster vor dem zweiten.
  Dafür lohnt kein Umbau; der ehrliche Zustand „noch nicht gebaut“ reicht.
- **`ApplicationStarted`:** Der Callback ist synchron; `async void` ließe `StartAsync` zurückkehren, bevor
  der Lauf fertig ist, und eine entkommende Exception beendet den Prozess.
- **Die Prüfung liest den Aufbau statt der Routen:** Sie würde nicht sehen, was wirklich gemappt ist.

## Prüfung

Rote Tests zuerst, über zwei Wege: `WebApplication` und `HostBuilder` mit `ConfigureWebHost` (wie
`ServiceCallerHarness`). Fälle: Aussteller korrekt Pass; Empfänger mit Tür und Fallback Pass; Empfänger mit
`AllowAnonymous`-Tür Fail; Empfänger ohne Tür Warning; Empfänger plus Aussteller, 0 Türen, kein Token-Endpoint Fail.
Gelesen wird `ISecurityCheckReport.Latest` **direkt nach** `StartAsync`; `/noelia/report.json` liest dieselbe Quelle, ein eigener Test dafür gehört zum Dashboard-Folge-Issue. Die Demo bekommt
einen echten Aussteller; das Release-Gate erwartet dort Pass.
