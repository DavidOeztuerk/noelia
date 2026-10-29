# ADR-0002 — Dienst-zu-Dienst-Rechte: Service-Tokens und Aufrufer je Endpoint

**Status:** Vorgeschlagen · **Datum:** 2026-10-29 · **Fassung:** 1 · **Gate:** G-0014 (Rank A, `auth`) · **Ziel:** 7.1.0, rein additiv

## Frage

Wie weist ein Dienst einem anderen nach, *wer* er ist, und wie lässt ein
Endpoint nur die Dienste durch, die ihn brauchen?

Anlass ist Befund WT-1 in Workertransfer: zehn Dienste teilen sich ein Secret
für sieben `/internal`-Türen von identity, darunter zwei, die entschlüsselte
KI-Schlüssel herausgeben. Wer das eine Secret hat, hat alle Türen. Die
Rechtetabelle, das Inventar und die Entscheidungen E1/E4 stehen in der
WT-Design-Notiz `wt-G-0001/.monarch/knowledge/Q-0001-design.md` und bleiben
gültig; dieses ADR liefert den Baustein, mit dem WT (und jedes andere Projekt)
sie umsetzt.

## Was es heute gibt, und warum es nicht reicht

Belege in `.monarch/knowledge/G-0014-scout.md`.

- **Ausstellen:** nichts. `IJwtService.GenerateTokenAsync(UserClaims)` verlangt
  eine E-Mail (`JwtService.cs:90-100`) und kennt genau eine Audience und eine
  Laufzeit aus `JwtSettings` (`:204-209`). Ein Dienst-Token darüber wäre ein
  Personen-Token mit Pseudo-Adresse.
- **Empfangen:** der Bearer-Scheme prüft genau eine Audience
  (`KeyRing.ValidationParameters`, `KeyRing.cs:86-104`). `Roles.Service` ist
  definiert und von keiner Policy benutzt. Abgelehnt wird mit 403 und eigenem
  JSON (`ResourceAuthorizationMiddleware.cs:181-195`) — das verrät, dass es den
  Endpoint gibt.
- **Client:** `ServiceTokenProvider` sendet `{serviceName, servicePassword}` als
  JSON und erwartet `{data:{accessToken, expiresIn}}` (`ServiceTokenProvider.cs:134-205`).
  Das ist das Skillswap-Format, kein OAuth. Er kennt keine Ziel-Audience und legt
  das Token in den `IDistributedCache` (Schlüssel `m2m:token:<ClientId>`).
- **Skillswap als Gegenbeispiel:** jeder Dienst hat ein eigenes Passwort, aber
  der Empfänger prüft nur `RequireRole("Service")`
  (`VideocallControllerExtensions.cs:22` u. a.). Jeder Dienst darf damit jeden
  Service-Endpoint. **Genau diese Lücke darf Noelia nicht haben:** es gibt in
  dieser API keinen Weg, „irgendein Dienst“ zu erlauben.

## Entscheidung

Drei Bausteine, je ein Modul, alle opt-in, nichts davon in `UseDefaults()`:

| Baustein | Modul | Wer bindet ihn ein |
|---|---|---|
| Ausstellung | `NoeliaModule.ServiceTokenIssuing` | der Identity-Dienst (genau einer) |
| Empfänger-Policy | `NoeliaModule.ServiceCallers` | jeder Dienst mit internen Türen |
| Client | `NoeliaModule.ServiceTokenClient` | jeder aufrufende Dienst |

Grundsatz: **Das Token sagt, wer ruft. Was der Aufrufer darf, entscheidet der
Empfänger, im Code.** Das Token trägt keine Rechte (keine Scopes, keine Rollen).
So kann ein Konfigurationsfehler beim Aussteller kein Recht verbreitern; die
Rechtetabelle steht dort, wo die Tür steht, und wird mit ihr reviewt.

### 1. Ausstellung

**Client-Register.** Je Dienst eine Client-ID und das SHA-256 seines Secrets,
in zwei Slots:

- Client-IDs kommen **aus dem Code**, als feste Liste beim Aufruf; die
  Konfiguration liefert nur die Hashes. Eine ID, die in der Konfiguration steht,
  aber nicht im Code, existiert nicht. Form `^[a-z][a-z0-9-]{0,47}$`.
- Slot `current` ist Pflicht, Slot `previous` optional (leer = geschlossen).
- Gespeichert wird nur der Hash, Format `sha256:<base64 der 32 Byte>`. Ein
  anderes Präfix, eine falsche Länge oder ein Klartext-Wert: kein Start.
- Beim Start: alle nicht-leeren Hashes über alle Clients und Slots paarweise
  verschieden, sonst kein Start (zwei Dienste mit einem Secret wären die
  Vereinigung ihrer Rechte; WT-Warden-Auflage 3).

**Warum SHA-256 und nicht `IPasswordHasher`.** Die Secrets sind Zufallswerte mit
256 Bit, keine Passwörter. Ein langsamer Hash (PBKDF2, Argon2) schützt einen
Wert mit wenig Entropie gegen Raten; hier gibt es nichts zu raten, und er
kostete bei jeder Token-Anfrage Rechenzeit, die ein Angreifer ohne Secret
auslösen kann. Ungesalzen ist gewollt: nur so ist die Doppelt-Prüfung beim Start
möglich. Der Aussteller lehnt präsentierte Secrets unter 43 Zeichen ab (Base64
von 32 Byte); der Client verweigert schon den Start damit.

**Prüfung.** SHA-256 des präsentierten Secrets, `FixedTimeEquals` gegen
**beide** Slots ohne frühen Abbruch. Eine unbekannte Client-ID vergleicht gegen
einen festen Dummy-Hash, damit die Arbeit gleich bleibt. Ein leerer Slot ist nie
ein Treffer.

**Token.** JWT, signiert mit dem Signierschlüssel des vorhandenen `KeyRing`
(also `UseJwt(jwt => jwt.Issue(...))`):

| Teil | Wert | Warum |
|---|---|---|
| Header `typ` | `noelia-service+jwt` | explizite Typisierung (RFC 8725 §3.11): ein Dienst-Token ist nie als Personen-Token lesbar und umgekehrt |
| `iss` | `JwtSettings:Issuer` | derselbe Aussteller wie für Personen |
| `aud` | **genau eine** Ziel-Audience | ein Token, das A empfängt, kann A nicht bei B einlösen |
| `client_id` | Client-ID des Aufrufers | der Aufrufer-Claim, den die Policy liest |
| `jti`, `iat`, `nbf`, `exp` | — | Audit; Laufzeit |
| **kein** `sub`, `email`, Rollen, Scopes | — | kein Codepfad soll einen Dienst für eine Person halten |

Laufzeit Default **5 Minuten**, wählbar 1–15 Minuten, alles andere: kein Start.
Die Ziel-Audience fordert der Client an. Der Aussteller verweigert eine
Audience, die gleich `JwtSettings:Audience` ist (das wäre ein Token für die
Personen-API), und leere oder ungültig geformte Werte. Eine Liste erlaubter
Audiences je Client gibt es bewusst nicht: sie wäre eine zweite Rechtetabelle,
die von der am Empfänger abweicht, und brächte nichts, weil der Empfänger
ohnehin nur die Aufrufer durchlässt, die er nennt.

**Token-Endpoint als Baustein.** Das Modul registriert Dienste; den Endpoint
mappt der Identity-Dienst selbst, an einem Pfad seiner Wahl:

```csharp
// identity-service
noelia.UseJwt(jwt => jwt.Issue(config["NOELIA_JWT_PRIVATE_KEY"]!, kid))
      .UseServiceTokenIssuer(issuer => issuer
          .Clients(config, "notification", "advisor", "transfer", "jobs" /* … */)
          .Lifetime(TimeSpan.FromMinutes(5)));

app.MapServiceTokenEndpoint("/m2m/token")       // gibt RouteHandlerBuilder zurück
   .RequireRateLimiting("m2m-token");           // Sache des Dienstes
```

Wire-Format: OAuth 2.0 Client Credentials (RFC 6749 §4.4),
`application/x-www-form-urlencoded`: `grant_type=client_credentials`,
`client_id`, `client_secret`, `audience`. `audience` ist nicht Teil von RFC
6749; der Name folgt der verbreiteten Praxis, `resource` (RFC 8707) verlangte
absolute URIs. Nur `client_secret_post`, kein HTTP-Basic.

- Erfolg: `200 {"access_token","token_type":"Bearer","expires_in"}`.
- **Jede** Ablehnung (unbekannte ID, falsches Secret, leerer Slot, falscher
  `grant_type`, fehlende oder verbotene Audience, kaputter Body): dieselbe
  Antwort `401 {"error":"invalid_client"}`. Eine unbekannte ID ist von einem
  falschen Secret nicht unterscheidbar.
- Beide mit `Cache-Control: no-store`.
- Log: Client-ID (nur wenn bekannt, sonst „unknown client“, nie der gesendete
  Wert), Audience, Ergebnis. Nie Secret, Hash, ein Präfix davon oder das Token.

Voraussetzungen, beim Start geprüft: ein `KeyRing` mit Signierschlüssel, und
dieser asymmetrisch. Mit `FromSharedSecret()` hält jeder prüfende Dienst den
Signierschlüssel und kann Dienst-Tokens für jeden Aufrufer prägen; dann wäre
alles hier Theater. Das Modul verweigert deshalb den Start, in **jeder**
Umgebung.

### 2. Empfänger-Policy je Endpoint

```csharp
// jeder Dienst mit internen Türen
noelia.UseJwt(jwt => jwt.VerifyOnly(config["NOELIA_JWT_PUBLIC_KEY"]!, kid))
      .UseServiceCallers(audience: "identity");

var doors = app.MapGroup("/internal");
doors.MapGet("/account/{s}/identity", …).RequireServiceCaller("advisor", "transfer");
doors.MapGet("/account/{s}/ai", …).RequireServiceCaller("profile", "applications", "messages");
doors.MapServiceFallback();   // alles andere unter /internal: dasselbe 404
```

Für Controller dasselbe als Attribut: `[ServiceCaller("advisor", "transfer")]`.

**Mechanik.**
- Eigener Authentication-Scheme `NoeliaService` (JwtBearer) neben dem
  Personen-Scheme: gleiche Schlüssel aus dem `KeyRing`, gleicher Issuer,
  `ValidAudience` = die eigene Audience aus dem Code, `ValidTypes =
  ["noelia-service+jwt"]`, `NameClaimType = client_id`. Ohne `KeyRing` (nur
  `From(authority)`) oder mit symmetrischem Ring: kein Start.
- `RequireServiceCaller(params string[])` und `[ServiceCaller]` setzen
  Autorisierungs-Metadaten mit Scheme `NoeliaService` und einer
  `ServiceCallerRequirement` (über `IAuthorizationRequirementData`, damit
  Minimal APIs und Controller denselben Weg nehmen). Eine leere Liste wirft beim
  Mappen. **Es gibt keinen Wildcard und keine Rolle „Service“.**
- Der Handler liest `client_id` **nur aus der Identity, die der Scheme
  `NoeliaService` authentifiziert hat**. Ein Personen-Token mit einem
  selbstgesetzten `client_id`-Claim (`UserClaims.CustomClaims`) zählt nicht,
  auch wenn auf demselben Endpoint weitere Policies den Personen-Scheme
  mitlaufen lassen.
- Die Autorisierung läuft in der Authorization-Middleware, also **vor** dem
  Model-Binding. Ein kaputter Body eines Nicht-Berechtigten erreicht den Binder
  nie (das WT-Problem „400 vor 404“, Notiz §11, entfällt).

**Einheitliches 404.** Ein `IAuthorizationMiddlewareResultHandler`, der den
vorhandenen dekoriert: Wenn die Policy eine `ServiceCallerRequirement` enthält
und das Ergebnis nicht `Succeeded` ist — egal ob kein Token, kaputtes Token,
falsche Audience, Personen-Token, fremder Dienst oder ein Fehlschlag einer
weiteren Policy auf demselben Endpoint —, setzt er **404, ohne Body, ohne
eigene Header**, und ruft weder `Challenge` noch `Forbid` (die würden
`WWW-Authenticate` und Noelias 401-JSON schreiben). Alle anderen Endpoints
behandelt der dekorierte Handler wie bisher.

Vergleichsmaßstab ist dieselbe Anfrage an einen Pfad, den es nicht gibt. Weil
die Ablehnung nur einen Statuscode ohne Body setzt, verarbeiten
`UseStatusCodePages`, ProblemDetails und Middleware weiter außen sie genauso
wie ein nicht gefundenes Routing — die Antworten sind gleich, ohne dass Noelia
das 404 des Dienstes nachbauen muss. `MapServiceFallback()` mappt
`{**rest}` für alle Methoden derselben Gruppe auf dieselbe Antwort; damit gibt
eine falsche Methode auf einer existierenden Tür kein 405 mehr.

**Personen-Scheme.** Der bestehende Scheme lehnt Tokens mit `typ =
noelia-service+jwt` zusätzlich ab (TypeValidator). Das ist eine Verengung nur
für einen Token-Typ, den es vor 7.1 nicht gab, also nicht brechend. Dazu
verweigert `UseServiceCallers` den Start, wenn die eigene Audience gleich
`JwtSettings:Audience` ist.

**Selbstprüfung.** Neuer Check `noelia.m2m.caller-policy`
(`Category == Composition`, läuft also immer). Er prüft über die
`EndpointDataSource`:
1. Kein Endpoint trägt `[ServiceCaller]`/`RequireServiceCaller` **und**
   `AllowAnonymous` — eine Gruppe mit `.AllowAnonymous()` würde die Policy
   still aushebeln.
2. Der wirksame `IAuthorizationMiddlewareResultHandler` ist der 404-Dekorierer
   (eine spätere Registrierung eines eigenen Handlers ersetzte ihn still).
3. Ist `ServiceTokenIssuing` komponiert, gibt es genau einen Token-Endpoint.

Zusammenfassung und Abhilfe nennen nur Formen (Endpoint-Name, Modul), nie
Werte.

### 3. Client über `ServiceTokenProvider`

```csharp
// jeder aufrufende Dienst
noelia.UseServiceTokenClient();   // liest NOELIA_M2M_* (Abschnitt 4)

services.AddHttpClient<HttpIdentityLookup>(…)
        .AddServiceToken(audience: "identity");   // DelegatingHandler
```

- `M2MConfiguration` bekommt `Protocol` (`Legacy` | `ClientCredentials`,
  **Default `Legacy`**) und `Audience`. `Legacy` verhält sich exakt wie 7.0.1.
- `ServiceTokenProvider` implementiert zusätzlich das neue Interface
  `IServiceTokenSource` mit `GetTokenAsync(string audience, CancellationToken)`.
  `IServiceTokenProvider` bleibt unverändert (ein neues Interface-Mitglied wäre
  für fremde Implementierungen brechend).
- Im Modus `ClientCredentials`: ein Token **je Audience**, gecacht **nur im
  Prozess**, nie im `IDistributedCache` — ein geteiltes Redis gäbe jedem Dienst
  mit Zugriff die Tokens aller anderen. Erneuerung single-flight, Fenster
  `min(RefreshBeforeExpiry, Laufzeit/5)`.
- `ServiceCommunicationManager` fordert im Modus `ClientCredentials` das Token
  für die Audience = Zieldienstname an (wie in `ServiceEndpoints`).
- Scheitert die Token-Beschaffung, scheitert der Aufruf (Exception). Kein
  Rückfall auf `ServiceCommunication:BearerToken`, kein Aufruf ohne Token.

### 4. Env-Namen (Infisical)

Flache Namen in GROSS_MIT_UNTERSTRICH, wie `NOELIA_JWT_*`: Infisical injiziert
Secrets als Umgebungsvariablen unter ihrem Schlüssel, .NET legt sie unverändert
als Konfigurationsschlüssel ab, und Noelia liest sie ausdrücklich. Kein `__`,
keine Section-Pfade.

| Name | Wo (Infisical-Ordner) | Inhalt |
|---|---|---|
| `NOELIA_M2M_CLIENT_<ID>_SECRET_HASH` | nur identity | Slot `current`, `sha256:…` |
| `NOELIA_M2M_CLIENT_<ID>_SECRET_HASH_PREVIOUS` | nur identity, optional | Slot `previous` |
| `NOELIA_M2M_TOKEN_ENDPOINT` | jeder Aufrufer | URL des Token-Endpoints |
| `NOELIA_M2M_CLIENT_ID` | jeder Aufrufer | eigene Client-ID |
| `NOELIA_M2M_CLIENT_SECRET` | jeder Aufrufer, **nur seiner** | Klartext-Secret |

`<ID>` ist die Client-ID in Großbuchstaben mit `-` → `_` (eindeutig, weil `_`
in IDs nicht vorkommt). Je Dienst ein Infisical-Ordner: das Klartext-Secret
liegt nur im Ordner des Aufrufers, der Hash nur in dem von identity. Die
Audience des Empfängers und die Client-Liste des Ausstellers stehen im Code,
nicht in der Umgebung.

Erzeugen, wie bei `SigningKey.GenerateKeyPair`:
`ServiceClientSecret.Generate("notification").ToEnvironment()` gibt beide
Zeilen aus. Ohne .NET:

```bash
S=$(openssl rand -base64 32)
echo "NOELIA_M2M_CLIENT_SECRET=$S"
echo "NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH=sha256:$(printf %s "$S" | openssl dgst -sha256 -binary | openssl base64 -A)"
```

**Rotation ohne Ausfall:**
1. Neues Secret erzeugen.
2. identity: `…_SECRET_HASH_PREVIOUS` := alter Hash, `…_SECRET_HASH` := neuer
   Hash, ausrollen. Beide gelten.
3. Aufrufer: `NOELIA_M2M_CLIENT_SECRET` := neues Secret, ausrollen.
4. identity: `…_PREVIOUS` leeren, ausrollen. Mit dem alten Secret geholte
   Tokens laufen nach höchstens einer Laufzeit (5 min) aus.

Notfall (Secret gestohlen): beide Slots ersetzen. Neue Tokens werden sofort
verweigert; bereits ausgestellte gelten bis zu ihrem `exp`.

### 5. Rein additiv, 7.1.0

- Neue Typen, drei neue Module, neue Config-Properties mit Defaults, die das
  bisherige Verhalten erhalten (`Protocol = Legacy`).
- Einzige Verhaltensänderung an Bestehendem: der Personen-Scheme lehnt den
  neuen Token-Typ ab, den kein bestehender Aussteller erzeugt.
- Keine neuen Pakete: JWT kommt aus dem vorhandenen
  `Microsoft.IdentityModel.JsonWebTokens`, ASP.NET aus der vorhandenen
  FrameworkReference von `Noelia.Infrastructure`. `PackageDependencyBudgetTests`
  bleibt unverändert; `Noelia.Http` und `Noelia.Dashboard` bleiben ohne
  `PackageReference`. `Noelia.Abstractions` bekommt nur die drei
  `NoeliaModule`-Namen.
- `MIGRATION.md` 7.1.0: „nichts zu tun“, plus der Umstieg von `Legacy`.
  `VersionPrefix` 7.1.0. Release und Tag macht der User.

## Bedrohungsmodell

| # | Angreifer hat … | Erreicht | Begrenzt durch |
|---|---|---|---|
| T1 | Secret eines Aufrufers | Tokens als dieser Aufrufer | nur dessen Türen (Tabelle am Empfänger); Rotation; Aufrufer im Log |
| T2 | ein Dienst-Token (Log, Proxy) | Aufrufe als dieser Aufrufer bei **einem** Empfänger | `aud` einwertig, `exp` ≤ 15 min (Default 5) |
| T3 | einen kompromittierten Empfänger | die Tokens, die er erhält | `aud` = er selbst; bei anderen wertlos |
| T4 | ein Personen-Token | — an Dienst-Türen | `typ` + `aud` → 404 |
| T5 | ein Dienst-Token | — an Personen-Endpoints | `typ` + `aud` → 401 wie bisher |
| T6 | den gemeinsamen Redis | — | Tokens nur im Prozess (Modus `ClientCredentials`) |
| T7 | Netzzugang, kein Credential | Kenntnis der Pfade? | einheitliches 404, Fallback je Gruppe |
| T8 | Netzzugang, Rateversuche | — | 256 Bit; gleiche Antwort; zeitkonstant; Rate-Limit empfohlen |
| T9 | nur die Hash-Konfiguration von identity | — | Hashes sind keine Credentials |
| T10 | einen symmetrischen `KeyRing` | Tokens für jeden Aufrufer | Start verweigert |

### Grenzen, offen benannt

- **identity ist der Vertrauensanker.** Wer identity übernimmt, hält den
  Signierschlüssel und prägt beliebige Dienst- und Personen-Tokens. Das Hashen
  der Secrets schützt nur, wenn Hashes **ohne** den Signierschlüssel abfließen
  (Log, Dashboard, Backup, ein Konfig-Dump eines anderen Ordners). Es macht
  identity nicht weniger kritisch als heute.
- **Bearer, kein Besitznachweis.** Ein gestohlenes Token gilt bis `exp`. DPoP
  oder mTLS-gebundene Tokens sind nicht Teil von 7.1.
- **Kein Widerruf ausgestellter Dienst-Tokens.** Die kurze Laufzeit ersetzt ihn;
  die Revocation-Middleware gilt nur für Personen-Tokens.
- **Rechte je Tür, nicht je Datensatz.** Ein berechtigter Aufrufer kann jede ID
  abfragen (WT: Q-0044 Einwilligungsprüfung).
- **Kompromittierter Aufrufer** hat dessen Türen. Die Policy begrenzt gestohlene
  Credentials, nicht die Übernahme eines Dienstes.
- **Seitenkanäle.** Das 404 ist im Inhalt gleich, nicht zwingend in der Zeit:
  eine Signaturprüfung dauert länger als „kein Header“. Wer Zeiten misst, kann
  „Token geprüft“ von „kein Token“ unterscheiden, nicht aber „Tür existiert“
  von „Tür fehlt“, solange die Gruppe einen Fallback hat.
- **Transport.** Der Token-Endpoint erzwingt kein HTTPS (clusterinterne Netze
  laufen oft ohne). Secrets im Klartext-Netz sind Sache von Netz-Isolation und
  Mesh, nicht dieses Bausteins.
- **OIDC-Authority** (`UseJwt(j => j.From(authority))`) wird für Dienst-Tokens
  in 7.1 nicht unterstützt; wer Keycloak o. Ä. nutzt, nimmt dessen Client
  Credentials.
- **`Legacy` bleibt, wie es ist** (Skillswap-Format, Token im verteilten Cache).
  Es wird dokumentiert als „nicht für neue Dienste“, aber nicht als obsolet
  markiert, weil `[Obsolete]` bei `TreatWarningsAsErrors` Konsumenten bräche.
- **Fail-open beim Aufrufer.** Wertet ein Aufrufer das 404 als „nichts da“
  (WT F1), fällt ein falsches Secret nicht auf. Das ist Aufrufer-Code.

## Was das für Workertransfer heißt (Q-0043 neu, nach dem Release)

- Die Tabelle aus Notiz §5 wird 1:1 zu `RequireServiceCaller(...)` an D1–D7;
  `MapServiceFallback()` ersetzt den geplanten Fallback `/internal/{**rest}`.
- E4 löst sich strukturell: notification-service wird selbst Empfänger
  (`UseServiceCallers("notification")`) mit seinen sieben Sendern; jeder Dienst
  hat genau ein Secret für alle Ziele.
- Die zehn Aufrufer-Clients bekommen `.AddServiceToken("identity")`; der Header
  `X-Notify-Secret` entfällt.
- Helm und Compose: `NOELIA_M2M_*` statt `WORKERTRANSFER_INTERNAL_SECRET_*`.

## Bezug zu offenen Noelia-Quests

- **Q-0026** (Shared-Secret nur mit Opt-in, Breaking, 8.0): unabhängig. Die
  M2M-Module verweigern symmetrische Schlüssel schon in 7.1, unabhängig vom
  Ausgang von Q-0026.
- **Q-0021** (reservierte Claims): `client_id` gehört auf deren Liste. Bis
  dahin schützt die Bindung an den Scheme (Abschnitt 2), nicht das Verbot.

## Umsetzung (Quests)

| Quest | Inhalt |
|---|---|
| Q-0046 | dieses ADR |
| Q-0047 | Ausstellung: Register, Hash, Slots, Token, Endpoint, Startprüfungen, Personen-Scheme lehnt `typ` ab |
| Q-0048 | Empfänger: Scheme, `RequireServiceCaller`/`[ServiceCaller]`, 404-Dekorierer, Fallback, Check `noelia.m2m.caller-policy` |
| Q-0049 | Client: `ClientCredentials` im `ServiceTokenProvider`, `IServiceTokenSource`, `AddServiceToken`, Doku, MIGRATION, 7.1.0 |

Testplan (TDD, vorher rot):
- **Boss Ausstellung:** unbekannte ID, falsches Secret, leerer Slot,
  verbotene Audience → byte-gleiche 401; beide Slots gültig; doppelte Hashes →
  kein Start; symmetrischer Ring → kein Start; Token trägt `typ`, eine `aud`,
  `client_id`, kein `sub`, `exp − iat` = Laufzeit.
- **Boss Empfänger:** Matrix Aufrufer × Tür (berechtigt 2xx, sonst 404); kein
  Token, kaputtes Token, abgelaufenes Token, falsche Audience, Personen-Token,
  Personen-Token mit `client_id`-Claim, falsche Methode, kaputter Body → 404,
  byte-gleich (Status, Header ohne `Date`, Body) zur Antwort für einen
  nicht existierenden Pfad, auch mit `UseStatusCodePages`. Dienst-Token am
  Personen-Endpoint → 401.
- **Check:** Endpoint mit `AllowAnonymous` + `ServiceCaller` → Fail; fremder
  Result-Handler nach dem Dekorierer → Fail.
- **Client:** zwei Audiences → zwei Tokens; kein Schreiben in
  `IDistributedCache`; Fehlschlag → Exception, kein unauthentifizierter Aufruf;
  `Legacy` unverändert (bestehende Tests grün).
- **Wirkung, nicht Rückgabe:** jeder Builder-Aufruf hat einen Test, der rot
  wird, wenn sein Rumpf fehlt.
