# Erklärtes Vermittler-Vertrauen wurde nie angewandt

- **Noelia-Fassung:** 4.2 bis 5.0.0
- **Gefunden beim:** Vergleich zweier Architekturen im Demo-Stack, 16.09.2026
- **Art:** Sicherheitslücke (CWE-348: Verlass auf eine nicht vertrauenswürdige Quelle; CWE-614: Cookie ohne `Secure`)
- **Einstufung:** mittel — braucht einen Vermittler vor dem Dienst, was die übliche Betriebsform ist
- **Blockiert:** nein
- **Stand:** in 5.1.0 behoben

## Was passiert

`TrustForwardedHeadersFrom(["10.0.0.0/8"])` belegt
`ForwardedHeadersOptions` und legt einen `ForwardedHeaderTrust` in den
Behälter. **Angewandt wurde nichts davon.** Weder `UseNoelia(...)` noch einer
seiner Schritte ruft `app.UseForwardedHeaders()` auf, und ohne diesen Aufruf
liest die Middleware die Optionen nie.

Die Folge steht in `ClientAddress`, dessen eigene Dokumentation den Aufruf
voraussetzt:

```csharp
// Behind a proxy the connection is the proxy, and the address wanted is one hop
// further out. That is what ForwardedHeaderTrust is for: a named proxy list
// lets the platform rewrite ConnectionInfo.RemoteIpAddress …
var address = context.Connection.RemoteIpAddress;
```

„lets the platform rewrite" — nur schreibt niemand vor, dass die Plattform es
tut. `ClientAddress.Of` entscheidet damit hinter jedem Vermittler auf dessen
Adresse, und das ist die Adresse, gegen die

- die Bremse zählt (`DistributedRateLimitingMiddleware`),
- die Prüfspur ihre Einträge schreibt (`SecurityAuditMiddleware`),
- die Eingabeprüfung ihre Meldungen ausstellt (`InputSanitizationMiddleware`).

Eine einzige Adresse für alle Aufrufer heißt: ein Aufrufer verbraucht das
Kontingent aller, und jeder Eintrag der Prüfspur nennt denselben.

Der zweite Teil ist schwerer zu sehen. Endet TLS am Vermittler, bleibt
`Request.Scheme` auf `http`. Eine Anwendung, die daraus ihren Cookie ableitet —
und das ist die richtige Ableitung —

```csharp
Secure = http.Request.IsHttps,
```

gibt den Refresh-Token **ehrlich ohne `Secure`** aus. Nichts ist falsch
programmiert; die Anwendung bekommt nur eine falsche Auskunft.

## Warum es Noelias ist

```csharp
// nur Noelia
var services = new ServiceCollection();
services.AddNoelia(configuration, environment, "probe", _ => { });
services.TrustForwardedHeadersFrom(["127.0.0.1"]);

// … app.UseNoelia(environment, "probe", mw => mw.UseForwardedHeaders());

var client = host.GetTestClient();
client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

// bis 5.0.0: "127.0.0.1" — die Erklärung stand im Behälter und wirkte nicht
// ab 5.1.0:  "203.0.113.9"
await client.GetAsync("/");
```

Die vorhandenen Proben in `ErklaertesVertrauenTests` haben das nicht gefunden,
weil sie `app.UseForwardedHeaders()` **selbst** aufrufen. Sie belegen damit,
dass die Optionen stimmen — nicht, dass jemand sie anwendet. Das ist genau die
Sorte Test, gegen die 5.0 angetreten ist.

## Und ein Sprung weiter

`UseForwardedHeaders` **verbraucht** die Kopfzeilen: Es schreibt Schema und
Adresse um und entfernt sie, was richtig ist — ein zweiter Durchlauf dürfte sie
nicht noch einmal lesen. Was es nicht wissen kann, ist, dass dieser Dienst
gleich selbst einen Aufruf macht.

Damit beendet ein Gateway die Kette. Im Demo-Stack trug der Refresh-Cookie des
Monolithen `Secure` und der hinter dem Gateway nicht — dieselbe Anwendung,
dieselbe Konfiguration, ein Sprung Unterschied. Ohne den Vergleich zweier
Architekturen sieht das nach einem Anwendungsfehler aus.

## Was es kostet

| | |
|---|---|
| Bremse | ein Kontingent für alle Aufrufer zusammen |
| Prüfspur | jeder Eintrag nennt den Vermittler |
| Sicherheitsmeldungen | beschuldigen den Vermittler |
| Refresh-Cookie | ohne `Secure` hinter TLS-Terminierung |

## Behebung

`UseNoelia(...)` stellt `UseForwardedHeaders()` als **ersten** Schritt ein —
jeder spätere Schritt liest Werte, die dieser festlegt —, aber nur, wenn
`TrustForwardedHeadersFrom` mindestens einen Vermittler benannt hat. Wer nie
Vertrauen erklärt hat, merkt nichts.

Für Dienste, die selbst weiterleiten, kommt `ForwardedOriginHandler` aus
`Noelia.Http` dazu. Der empfangende Dienst entscheidet weiterhin selbst, ob er
etwas davon glaubt.

**Gegenprobe, die das festhält:**
`Die_Noelia_Kette_wendet_die_Erklaerung_selbst_an` und
`Ohne_Erklaerung_stellt_die_Kette_den_Schritt_nicht_ein` gehen durch
`UseNoelia`, nicht durch eine selbst gebaute Kette. Wird der Schritt wieder
entfernt, fällt die erste.
