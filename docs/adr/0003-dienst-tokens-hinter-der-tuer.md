# ADR-0003 — Dienst-Tokens hinter der Tür: Widerruf, Principal und Prüfung je Datensatz

**Status:** Angenommen für `develop` (Fassung 2, Warden-Freigabe nach Delta-Runde), umgesetzt in 7.1.1 · **Datum:** 2026-11-05 · **Gate:** G-0016 (Rank A, `auth`) · **Quest:** Q-0055 · **Issue:** #57 · **Ziel:** 7.1.1, keine neue öffentliche API

## In einfacher Sprache

Noelia 7.1.0 hat Türen eingeführt: Endpoints, die nur bestimmte Dienste aufrufen
dürfen. Ein Dienst weist sich dort mit einem Dienst-Token aus. Dieses Token nennt
bewusst keine Person.

Drei Bausteine, die Dienste hinter die Anmeldung hängen können, kennen das nicht:
die Widerrufsprüfung, der Baustein, der aus dem Token die handelnde Person macht,
und die Prüfung je Datensatz. Alle drei suchen die Person. Finden sie keine, lehnen
sie ab. Damit kommt an solchen Diensten kein berechtigter Dienst durch die Tür.

Der Fix bringt ihnen eine Regel bei: Was das Dienst-Scheme geprüft hat, ist keine
Person und geht sie nichts an. Sie schauen nur noch auf den Rest. Ist kein Rest da,
lassen sie die Anfrage weiter. Ist eine Person dabei, prüfen sie genau diese Person
wie immer.

Dabei fiel ein zweiter, ernsterer Fehler auf: In einem seltenen Aufbau, in dem eine
Anfrage eine Person und einen Dienst zugleich trägt, kam eine **widerrufene Person
durch**. Der Fix schließt das mit.

Was sich für Nutzer ändert: Nichts an den Regeln für Personen. Ein Personen-Token
ohne `sub` wird weiter abgelehnt, ein widerrufenes Token auch. An einer Tür gibt es
keine handelnde Person: `ICurrentPrincipal.Current` ist dort `null`, außer die Anfrage
trägt neben dem Dienst zusätzlich eine angemeldete Person.

## Frage

Wie behandeln `UseTokenRevocation`, `UseNoeliaPrincipal` und
`UseResourceAuthorization` eine Anfrage, die an einer Tür vom Scheme `NoeliaService`
authentifiziert wurde, und was liefert `ICurrentPrincipal` dort?

## Befund, zur Laufzeit belegt

Der rote Test `ServiceCallerFullChainTests` nutzt ein echt ausgestelltes Token und
die Reihenfolge von Workertransfer (`ServiceFoundation.cs`): die Standardkette,
dann `UseTokenRevocation()`, dann `UseNoeliaPrincipal()`.

| Kette hinter `UseNoelia` | Antwort an der Tür | Beleg aus Antwort und Log |
|---|---|---|
| nur Widerruf | 401 | Body `Token cannot be checked for revocation` |
| nur Principal | 401 | Log `The token carries no usable subject claim.` |
| beide | 401 | wie „nur Widerruf“, der Widerruf steht vorn |
| Prüfung je Datensatz, Controller-Tür | nicht 200 | ein `IResourceAuthorizationService`, der ablehnt, schließt die Tür |

Der Ablauf:

1. Die Tür-Policy nennt den Scheme `NoeliaService`. In `UseAuthorization` setzt der
   `PolicyEvaluator` darum `context.User` auf die Dienst-Identität.
2. Die Bausteine stehen hinter `UseAuth()`, sehen also diese Identität.
3. Sie ist authentifiziert und hat nach ADR-0002 kein `sub`. Die Bausteine werten das
   als kaputtes Token oder als unbekannten Nutzer.

ADR-0002 sagt dazu: „die Revocation-Middleware gilt nur für Personen-Tokens“. Das
stand im Design und nicht im Code. `ServiceCallerHarness` läuft mit der
Standardkette, die keinen der Bausteine enthält, deshalb fiel es nicht auf.

## Entscheidung

**1. Ein gemeinsamer interner Helfer, keine öffentliche API.**
`ServiceCallerIdentity.PersonPartOf(ClaimsPrincipal)` liefert den Personen-Anteil.
Ohne Dienst-Identity kommt derselbe Principal unverändert zurück, der Personen-Pfad
bleibt also auch im Objekt derselbe. Mit Dienst-Identity ist der Rest genau die
Menge der Identities, die **authentifiziert** sind und **nicht** vom Dienst-Scheme
stammen. Eine Identity, die niemand authentifiziert hat, zählt nicht als Person und
löst auch keine Ablehnung aus.

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

**5. `ResourceAuthorizationMiddleware` fragt nach dem Personen-Anteil.** Ist er leer,
läuft die Anfrage weiter. ADR-0002 legt fest: Rechte gelten je Tür, nicht je
Datensatz, und über die Tür hat ihre Aufruferliste schon entschieden. Ist eine Person
dabei, wird der `IResourceAuthorizationService` mit genau dieser Person gefragt und
nie mit dem Dienst. Die Regel sitzt in der Middleware, weil auch dieser Port ersetzbar
ist.

**6. `ICurrentPrincipal.Current` ist an einer Tür `null`**, außer die Anfrage trägt neben
dem Dienst eine authentifizierte Person: Dann ist `Current` diese Person. Gründe für
`null`:

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
nicht mehr. Auch `IsAuthenticated(this ICurrentPrincipal)` ist an einer Tür `false`,
obwohl die Anfrage authentifiziert ist: Die Methode beantwortet, ob eine Person hinter
der Anfrage steht. Angepasst werden die XML-Doku von `Current`, `IsAuthenticated`,
`Require()` samt Meldung und die Remarks von `Principal`.

Den Namen des Aufrufers braucht ein Handler in der Regel nicht, die Liste der Tür hat
entschieden. Wer ihn doch will, liest `HttpContext.User` an der Tür; das steht im
README.

## Warum nicht einfach überspringen

Die naheliegende Lösung wäre: Dienst-Identity gefunden, also weiter. Das reißt ein
Loch. Nennt eine Policy zwei Schemes, führt der `PolicyEvaluator` beide Identities
in einem Principal zusammen (ADR-0002 beschreibt das). Das trifft zum Beispiel auf
einen Dienst zu, der Personen per Cookie anmeldet und an derselben Tür ein
Dienst-Token verlangt. Dort stünde auch eine Person im Principal, und ihr Widerruf
fiele still aus.

**Schon 7.1.0 hat dort ein Loch, zur Laufzeit belegt.** `FindFirst` liest quer über
alle Identities. Steht die Dienst-Identity vorn, bekommt der Widerruf `jti` und `iat`
vom frischen Dienst-Token und `sub` von der Person. Ein widerrufenes Personen-Token
fällt nicht auf: Der Test `A_revoked_person_next_to_a_service_is_refused` ist vor dem
Fix rot, und zwar genau in dieser Reihenfolge der Identities. Die andere Reihenfolge
ist grün, weshalb beide laufen. Das Loch verlangt zwei Anmeldewege an einer Tür und
ein gültiges Dienst-Token. Der Personen-Anteil schließt es.

## Verworfene Alternativen

| Alternative | Warum nicht |
|---|---|
| Fehlendes `sub` als Zeichen für einen Dienst | Ein Personen-Token ohne `sub` käme durch. Genau das verbietet die Vorgabe. |
| `client_id` als Zeichen für einen Dienst | Ein Personen-Token darf einen selbst gesetzten `client_id` tragen (`UserClaims.CustomClaims`). |
| Regel in `PrincipalFactory` oder im Resource-Service | Greift nicht für eigene Implementierungen der Ports. |
| Eigener `Capacity`-Fall oder `Principal.Service` | Bruch für Konsumenten, und ein Dienst sähe aus wie ein Principal mit `Subject`. Wäre frühestens 8.0. |
| `context.User` an der Tür umbiegen | Ändert, was Handler und Audit sehen. Die Bausteine lesen nur und lassen den Principal unangetastet. |

## Geprüft, ohne Handlungsbedarf

- **Nur lesend:** Telemetrie, Audit, Rate Limiter (läuft ohnehin vor `UseAuth`) und der
  Cache-Schlüssel von `ResponseCachingHeaderMiddleware` lesen `sub`, lehnen aber nichts
  ab.
- **Nur mit benannter Policy:** `ActiveAccountHandler`, `EmailVerifiedHandler`,
  `ResourceOwnerHandler` und `PermissionAuthorizationHandler` laufen nur, wenn eine Tür
  ihre Anforderung ausdrücklich nennt. Dann ist die Ablehnung, was die Policy sagt, und
  die Tür antwortet mit ihrem 404.
- **`PermissionMiddleware`** lässt eine Tür ohne geforderte Permission durch; das zeigt
  schon `ServiceCallerDoorTests` in der Standardkette, die diesen Schritt enthält.

## Grenzen

- **Zwei dokumentierte Plätze für den Principal.** Die Remarks von `PrincipalMiddleware`
  und `UseNoeliaPrincipal` sagen „vor `UseAuthorization`“, `UsePrincipal` und das README
  sagen „hinter `UseAuth()`“. Der Befund betrifft den zweiten Platz, den auch
  Workertransfer nutzt. Am ersten Platz ist `context.User` noch der Personen-Scheme, der
  ein Dienst-Token ablehnt: Die Anfrage ist dort anonym und `Current` ebenfalls `null`.
  Die Antwort dieses ADR gilt also für beide Plätze.
- **Eigener Defekt, nicht Teil dieses Gates.** Hinter `UseAuth()` ist `Current` während
  der Policy-Prüfung noch leer, deshalb lehnen `ActingForCompany` und `ActingAsSelf`
  dort jede Person ab (Laufzeitprobe: 403). Das hat mit Türen nichts zu tun, blockiert
  7.1.1 nicht und ist als #58 erfasst.
- **`PermissionMiddleware` liest nur die erste Identity.** Meldet ein fremdes Scheme
  `Success` mit einer Identity, die niemand authentifiziert hat, und nennt eine Tür dieses
  Scheme neben dem Dienst-Scheme, kann die Zusammenführung sie nach vorn stellen. Dann
  antwortet dieser Schritt mit 401, obwohl der Dienst berechtigt ist (Laufzeitprobe). Das
  scheitert geschlossen und setzt einen Fehler im fremden Scheme voraus. Die Änderung
  träfe die Standardkette aller Konsumenten und ist deshalb ein eigenes Issue (#59). Der
  Test dieses Falls läuft darum ohne `PermissionEnforcement`.
- **Trennung liegt beim Betreiber.** Ein Dienst, der ein eigenes Scheme oder eine eigene
  `IClaimsTransformation` so baut, dass eine Identity `AuthenticationType =
  "NoeliaService"` trägt, hebt die Trennung auf. Das ist Code des Betreibers und liegt
  außerhalb dessen, was ein Token beeinflussen kann.
- #56 bleibt offen: Der neue Test deckt die Bibliothek ab, kein Gate-Konsument übt
  M2M als Paket aus.

## Tests

Vor dem Fix rot:

- Ein berechtigter Dienst kommt mit Widerruf, mit Principal und mit beiden durch.
- Ein berechtigter Dienst kommt durch eine Controller-Tür hinter der Prüfung je Datensatz.
- Ein Dienst-Token mit `sub` wird an der Tür nicht zur Person und nicht gegen die
  Widerrufsliste geprüft.
- Im zusammengeführten Principal wird eine widerrufene Person abgelehnt, in beiden
  Reihenfolgen der Identities.
- Die Prüfung je Datensatz fragt im zusammengeführten Principal nur die Person.
- Eine nicht authentifizierte Identity neben einem Dienst zählt nicht als Person.

Wächter, die schon vor dem Fix grün sind und per Mutation zeigen, dass sie fallen
können. Zu jedem Wächter ohne Bausteine gibt es eine Kontrollzeile, die zeigt, dass
vorher nichts anderes ablehnt.

- Ein Personen-Token ohne `sub` bekommt 401, je Baustein.
- Ein Personen-Token ohne `sub` mit selbst gesetztem `client_id` bekommt 401, je Baustein.
- Ein Dienst neben einer Person ohne `sub` ist kein Freibrief: 401, je Baustein und in
  beiden Reihenfolgen.
- Ein widerrufenes Personen-Token bekommt 401.
- Ein Evaluator, der alles ablehnt, sperrt die Person und lässt den Dienst durch.

Zum Principal an der Tür: `Current` ist `null`, `Require()` scheitert, `IsAuthenticated()`
ist `false` bei authentifiziertem `HttpContext.User`, eine Kapazitäts-Policy an der Tür ist
ein 404, und eine eigene `IPrincipalFactory` bekommt die Dienst-Identity nie zu sehen.

## Version

Kein neues öffentliches API, nur Verhalten nach ADR-0002 und Doku: **7.1.1**.

## Fassung 2: Auflagen des Wardens

| Befund | Umgang |
|---|---|
| 3 (major): `ResourceAuthorizationMiddleware` lehnt an Controller-Türen ab, der Satz „lehnt nichts ab“ war falsch | Am Quelltext bestätigt. Dritter Baustein, Entscheidung 5, mit Tests. Handler mit benannter Policy unter „Geprüft“. |
| 2 (major): Testlücken im zusammengeführten Principal, „Rest“ undefiniert | Bestätigt. Entscheidung 1 definiert den Rest, die zwei Fälle sind Tests geworden. |
| 1 (minor): `IsAuthenticated()` und Remarks von `Principal` | Übernommen, mit Test. |
| 4 (minor): Testliste | Zum Teil schon erfüllt: `client_id` bei einem Personen-Scheme ist genau der Fall „`AuthenticationType` ist nicht `NoeliaService`“. Der Evaluator, der alles ablehnt, fällt um, sobald er gefragt wird, ist also strenger als ein Aufrufzähler. |
| 5 (minor): Widerspruch zum Platz des Principals | Unter „Grenzen“ geklärt. Die Prüfung ergab den eigenen Defekt mit den Kapazitäts-Policies. |

Die Delta-Runde gab das Design frei und stufte die Kapazitäts-Policies als nicht
blockierend für 7.1.1 ein. Ihr Hinweis, dass `Current` neben einem Dienst auch eine Person
sein kann, steht jetzt in Entscheidung 6.

## Mutationsprobe

Die Wächter sind nicht nur grün, sie fallen auch. Am Helfer `PersonPartOf` wurde jeweils
eine falsche Regel eingesetzt und die Testklasse laufen gelassen:

| Falsche Regel | Rote Tests |
|---|---|
| Dienst erkannt am fehlenden `sub` (von der Vorgabe verboten) | 16 |
| Dienst erkannt am Claim `client_id` | 13 |
| Dienst da, also alles überspringen | 10 |
| Rest ohne den Filter auf authentifizierte Identities | 1 |
