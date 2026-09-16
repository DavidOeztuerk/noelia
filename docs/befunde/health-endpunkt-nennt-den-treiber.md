# Der Health-Endpunkt liefert den Ausnahmetext des Treibers aus

- **Noelia-Fassung:** bis 5.0.0
- **Gefunden beim:** Registrieren der Redis-Prüfung im Demo-Stack, 16.09.2026
- **Art:** Informationspreisgabe (CWE-209: Fehlermeldung mit sensiblen Angaben)
- **Einstufung:** niedrig bis mittel — hängt daran, ob `/health` erreichbar ist und was im Verbindungstext steht
- **Blockiert:** nein
- **Stand:** in 5.1.0 behoben

## Was passiert

Der Antwortschreiber der drei Health-Endpunkte gibt die Ausnahme jeder
fehlgeschlagenen Prüfung im Wortlaut aus:

```csharp
checks = report.Entries.Select(e => new
{
    name = e.Key,
    status = e.Value.Status.ToString(),
    …
    error = e.Value.Exception?.Message
})
```

`/health`, `/health/live` und `/health/ready` sind nicht authentifiziert — sie
sind es an keiner Stelle, an der sie üblicherweise veröffentlicht werden, weil
eine Sonde von einem Lastverteiler ohne Zugangsdaten abgefragt wird.

Was in diesem Feld landet, entscheidet der Treiber. StackExchange.Redis nennt
den Endpunkt, mit dem er gesprochen hat; eine Datenbankausnahme nennt Server
und Datenbank, je nach Anbieter mehr. Wer die Verbindungszeichenfolge mit
Zugangsdaten baut — was verbreitet ist — legt sie damit auf einen Endpunkt, der
jedem antwortet.

Dieselbe Zeile stand dreimal in `EnhancedHealthCheckExtensions`, und die dortige
HTML-Ansicht schrieb sie zusätzlich unescaped in eine Tabellenzelle.

## Warum es Noelias ist

```csharp
// nur Noelia
services.AddRedisConnection("cache.internal.example:6379,password=…", "probe");

// Redis abschalten, dann:
//   GET /health/ready
// bis 5.0.0:
//   { "name": "redis", "status": "Unhealthy",
//     "error": "It was not possible to connect to the redis server(s).
//               … cache.internal.example:6379 …" }
```

## Was es kostet

Ein Aufrufer, der nur `/health` kennt, erfährt die Namen und Ports der
Infrastruktur hinter dem Dienst — und zwar zuverlässiger als durch Raten, weil
er den Ausfall nicht abwarten muss: es genügt, dass irgendeine Prüfung einmal
fehlschlägt.

## Behebung

Der Schreiber gibt `description` aus — das, was die Prüfung selbst formuliert
hat, wo ihr Autor entschieden hat, was gesagt werden darf. Die Ausnahme wird
protokolliert, wo sie einen Leser hat, der sie sehen darf.

`RedisHealthCheck` hängt die Ausnahme zusätzlich gar nicht mehr an das
Ergebnis: Was nicht da ist, kann auch ein fremder Antwortschreiber nicht
ausliefern.

**Gegenprobe, die das festhält:**
`A_driver_failure_does_not_reach_the_response_body` lässt den Treiber mit einer
Verbindungszeichenfolge samt Zugangsdaten scheitern und prüft, dass weder
Hostname noch Passwort in der Beschreibung stehen.
