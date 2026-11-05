# ADR-0003 — Dienst-Tokens hinter der Tür: Widerruf und Principal

**Status:** Vorschlag (wartet auf die Warden-Runde) · **Datum:** 2026-11-05 · **Gate:** G-0016 (Rank A, `auth`) · **Quest:** Q-0055 · **Issue:** #57 · **Ziel:** 7.1.1, keine neue öffentliche API

## In einfacher Sprache

Noelia 7.1.0 hat Türen eingeführt: Endpoints, die nur bestimmte Dienste aufrufen
dürfen. Ein Dienst weist sich dort mit einem Dienst-Token aus. Dieses Token nennt
bewusst keine Person.

Zwei Bausteine, die viele Dienste hinter die Anmeldung hängen, kennen das nicht.
Die Widerrufsprüfung und der Baustein, der aus dem Token die handelnde Person
macht, suchen beide die Person. Finden sie keine, antworten sie mit 401. Damit
kommt an solchen Diensten kein berechtigter Dienst durch die Tür.

Der Fix bringt beiden Bausteinen eine Regel bei: Was das Dienst-Scheme geprüft
hat, ist keine Person und geht sie nichts an. Sie schauen nur noch auf den Rest.
Ist kein Rest da, lassen sie die Anfrage weiter. Ist eine Person dabei, prüfen sie
genau diese Person wie immer.

Was sich für Nutzer ändert: Nichts an den Regeln für Personen. Ein Personen-Token
ohne `sub` wird weiter abgelehnt, ein widerrufenes Token auch. An einer Tür gibt es
keine handelnde Person: `ICurrentPrincipal.Current` ist dort `null`.

## Frage

Wie behandeln `UseTokenRevocation` und `UseNoeliaPrincipal` eine Anfrage, die an
einer Tür vom Scheme `NoeliaService` authentifiziert wurde, und was liefert
`ICurrentPrincipal` dort?

## Befund, zur Laufzeit belegt

Der rote Test `ServiceCallerFullChainTests` nutzt ein echt ausgestelltes Token und
die Reihenfolge von Workertransfer (`ServiceFoundation.cs`): die Standardkette,
dann `UseTokenRevocation()`, dann `UseNoeliaPrincipal()`.

| Kette hinter `UseNoelia` | Antwort an der Tür | Beleg aus Antwort und Log |
|---|---|---|
| nur Widerruf | 401 | Body `Token cannot be checked for revocation` |
| nur Principal | 401 | Log `The token carries no usable subject claim.` |
| beide | 401 | wie „nur Widerruf“, der Widerruf steht vorn |

Der Ablauf:

1. Die Tür-Policy nennt den Scheme `NoeliaService`. In `UseAuthorization` setzt der
   `PolicyEvaluator` darum `context.User` auf die Dienst-Identität.
2. Beide Bausteine stehen hinter `UseAuth()`, sehen also diese Identität.
3. Sie ist authentifiziert und hat nach ADR-0002 kein `sub`. Beide werten das als
   kaputtes Token.

ADR-0002 sagt dazu: „die Revocation-Middleware gilt nur für Personen-Tokens“. Das
stand im Design und nicht im Code. `ServiceCallerHarness` läuft mit der
Standardkette, die keinen der beiden Bausteine enthält, deshalb fiel es nicht auf.

## Entscheidung

**1. Ein gemeinsamer interner Helfer, keine öffentliche API.**
`ServiceCallerIdentity.PersonPartOf(ClaimsPrincipal)` liefert den Principal ohne
die Identity, die der Dienst-Scheme authentifiziert hat. Ohne Dienst-Identity
kommt derselbe Principal unverändert zurück, der Personen-Pfad bleibt also auch im
Objekt derselbe.

**2. Erkannt wird an der geprüften Identität, nie an einem fehlenden Claim.**
Maßstab ist `ClaimsIdentity.AuthenticationType == "NoeliaService"`, also dieselbe
Stelle, auf die sich schon der Tür-Handler stützt. Dieser Wert stammt aus der
Konfiguration des Schemes (`ServiceCallerSchemeConfiguration`), nicht aus dem
Token, und die Parameter sind ein eigenes Objekt je Scheme. Weder `sub` noch
`client_id` noch `typ` spielen eine Rolle.

**3. `TokenRevocationMiddleware` prüft den Personen-Anteil.** Ist er leer, läuft die
Anfrage weiter, und der Evaluator wird nicht gefragt. Das setzt ADR-0002 um: Ein
Dienst-Token kennt keinen Widerruf, die kurze Laufzeit ersetzt ihn. Ist eine
Person dabei, wird sie geprüft und ihre Claims kommen nur aus ihrer Identity.

**4. `PrincipalMiddleware` übergibt dem Factory den Personen-Anteil.** Die Regel sitzt
in der Middleware und nicht in `PrincipalFactory`, weil `IPrincipalFactory`
ersetzbar ist: Eine eigene Factory bekäme den Fehler sonst zurück.

**5. `ICurrentPrincipal.Current` ist an einer Tür `null`.** Gründe:

- ADR-0002 verlangt: „kein Codepfad soll einen Dienst für eine Person halten“.
- `Principal.Subject` ist Pflicht, ein Dienst hat keinen.
- `Capacity` ist eine geschlossene Hierarchie, auf die Konsumenten erschöpfend
  matchen. Ein dritter Fall wäre ein Bruch und passt in keine 7.1.1.
- `null` scheitert geschlossen:
  - `Require()` wirft, Code für Personen läuft also nicht versehentlich für einen
    Dienst.
  - `TryGetTenant` und `TryGetSubject` liefern `false`.
  - `ActingAsSelf` und `ActingForCompany` sind nicht erfüllt.
  - Der dokumentierte Mandantenfilter fällt auf `TenantId.None` zurück und vergleicht
    auf Gleichheit, zeigt also keine Mandantendaten.

Damit stimmt der bisherige Text „`null`, wenn die Anfrage nicht authentifiziert ist“
nicht mehr. Die XML-Doku und die Meldung von `Require()` werden angepasst.
Den Namen des Aufrufers braucht ein Handler in der Regel nicht, die Liste der Tür
hat entschieden. Wer ihn doch will, liest `HttpContext.User` an der Tür; das steht
im README.

## Warum nicht einfach überspringen

Die naheliegende Lösung wäre: Dienst-Identity gefunden, also weiter. Das reißt ein
Loch. Nennt eine Policy zwei Schemes, führt der `PolicyEvaluator` beide Identities
in einem Principal zusammen (ADR-0002 beschreibt das). Das trifft zum Beispiel auf
einen Dienst zu, der Personen per Cookie anmeldet und an derselben Tür ein
Dienst-Token verlangt. Dort stünde auch eine Person im Principal, und ihr Widerruf
fiele still aus.

Schon 7.1.0 hat dort einen Fehler: `FindFirst` liest quer über alle Identities. Der
Widerruf bekommt `jti` und `iat` vom frischen Dienst-Token und `sub` von der Person.
Eine widerrufene Person fiele damit nicht auf. Der Personen-Anteil schließt das mit
und bekommt einen eigenen roten Test.

## Verworfene Alternativen

| Alternative | Warum nicht |
|---|---|
| Fehlendes `sub` als Zeichen für einen Dienst | Ein Personen-Token ohne `sub` käme durch. Genau das verbietet die Vorgabe. |
| `client_id` als Zeichen für einen Dienst | Ein Personen-Token darf einen selbst gesetzten `client_id` tragen (`UserClaims.CustomClaims`). |
| Regel in `PrincipalFactory` | Greift nicht für eigene Factories. |
| Eigener `Capacity`-Fall oder `Principal.Service` | Bruch für Konsumenten und ein Dienst sähe aus wie ein Principal mit `Subject`. Wäre frühestens 8.0. |
| `context.User` an der Tür umbiegen | Ändert, was Handler und Audit sehen. Beide Bausteine lesen nur und lassen den Principal unangetastet. |

## Geprüft, ohne Handlungsbedarf

Die übrigen Leser von `sub` in der Kette lehnen nichts ab, sondern lesen nur:
Telemetrie, Audit, Rate Limiter (läuft ohnehin vor `UseAuth`) und der Cache-Schlüssel
von `ResponseCachingHeaderMiddleware`. `PermissionMiddleware` lässt eine Tür ohne
geforderte Permission durch; das zeigt schon `ServiceCallerDoorTests` in der
Standardkette, die diesen Schritt enthält.

## Grenzen

- Die Reihenfolge laut Remark von `PrincipalMiddleware` (vor `UseAuthorization`) war
  nie betroffen: Dort ist `context.User` noch der Personen-Scheme, der ein
  Dienst-Token ablehnt, die Anfrage ist also anonym und `Current` ebenfalls `null`.
- Ein Dienst, der sein Person-Scheme selbst mit `AuthenticationType = "NoeliaService"`
  konfiguriert, hebt die Trennung auf. Das ist Konfiguration des Betreibers und liegt
  außerhalb dessen, was ein Token beeinflussen kann.
- #56 bleibt offen: Der neue Test deckt die Bibliothek ab, kein Gate-Konsument übt
  M2M als Paket aus.

## Tests

Vor dem Fix rot:

- Ein berechtigter Dienst kommt mit Widerruf, mit Principal und mit beiden durch.
- Ein Dienst-Token mit `sub` wird an der Tür trotzdem nicht zur Person.
- Im zusammengeführten Principal wird eine widerrufene Person abgelehnt, in beiden
  Reihenfolgen der Identities.

Wächter, die schon vor dem Fix grün sind und per Mutation zeigen, dass sie fallen
können:

- Ein Personen-Token ohne `sub` bekommt 401, je Baustein.
- Ein Personen-Token ohne `sub` mit selbst gesetztem `client_id` bekommt 401.
- Ein widerrufenes Personen-Token bekommt 401.
- Ein Evaluator, der alles ablehnt, sperrt die Person und lässt den Dienst durch.

Zum Principal an der Tür: `Current` ist `null`, `Require()` wirft, eine Kapazitäts-Policy
an der Tür ist ein 404.

## Version

Kein neues öffentliches API, nur Verhalten nach ADR-0002 und Doku: **7.1.1**.
