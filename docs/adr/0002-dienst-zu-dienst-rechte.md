# ADR-0002 — Dienst-zu-Dienst-Rechte: Service-Tokens und Aufrufer je Endpoint

**Status:** Vorgeschlagen, Warden-Freigabe Design (Delta-Runde approve), User-Freigabe ausstehend · **Datum:** 2026-10-29 · **Fassung:** 2 · **Gate:** G-0014 (Rank A, `auth`) · **Ziel:** 7.1.0, rein additiv

Fassung 2 arbeitet die Design-Runde des Wardens ein (approve-with-conditions,
sechs Auflagen B1–B6 und vier Grenzen). Was sich geändert hat, steht unter
[Fassung 2: Auflagen des Wardens](#fassung-2-auflagen-des-wardens).

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
- **Secret-Format:** genau 43 Zeichen Base64url ohne Padding (`A–Z a–z 0–9 - _`),
  die zu 32 Zufallsbytes dekodieren. Keine `+`, `/`, `=`: die überstehen
  Form-Encoding und Shell nicht verlässlich. Ein präsentiertes Secret in einer
  anderen Form wird **vor** dem Hashen als `invalid_client` abgewiesen.
- Gespeichert wird nur der Hash: `sha256:` + Base64url ohne Padding des
  SHA-256 über die 43 ASCII-Zeichen des Secrets (zusammen 50 Zeichen). Ein
  anderes Präfix, eine falsche Länge oder ein Klartext-Wert: kein Start.
- Beim Start: alle nicht-leeren Hashes über alle Clients und Slots paarweise
  verschieden, sonst kein Start (zwei Dienste mit einem Secret wären die
  Vereinigung ihrer Rechte; WT-Warden-Auflage 3).

**Warum SHA-256 und nicht `IPasswordHasher`.** Die Secrets sind Zufallswerte mit
256 Bit, keine Passwörter. Ein langsamer Hash (PBKDF2, Argon2) schützt einen
Wert mit wenig Entropie gegen Raten; hier gibt es nichts zu raten, und er
kostete bei jeder Token-Anfrage Rechenzeit, die ein Angreifer ohne Secret
auslösen kann. Ungesalzen ist gewollt: nur so ist die Doppelt-Prüfung beim Start
möglich. Das trägt **nur bei 256 Bit Entropie**: über ein menschlich gewähltes
Secret wäre ein ungesalzener SHA-256 offline zu brechen. Die Form beweist keine
Entropie; unterstützt sind nur Secrets aus `ServiceClientSecret.Generate` oder
dem Rezept in Abschnitt 4 (Grenze, siehe unten). Der Client verweigert mit
einem Secret in falscher Form schon den Start.

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

**Ziel-Audiences.** Der Aussteller kennt eine flache Menge gültiger
Ziel-Audiences, **im Code** deklariert (`.Audiences("identity",
"notification", …)`, Form wie Client-IDs). Ausgestellt wird nur für eine davon;
alles andere ist `invalid_client`. Beim Start verweigert: eine leere Menge und
eine Audience gleich `JwtSettings:Audience` (das wäre ein Token für die
Personen-API). Die Menge trägt keine Rechte — sie gilt für alle Clients
gleich und ist keine zweite Rechtetabelle; sie fängt Tippfehler ab und
verhindert Tokens für fremde Audiences, etwa die Personen-Audience eines
anderen Dienstes. Eine Liste erlaubter Audiences **je Client** gibt es bewusst
nicht: sie wiche von der Tabelle am Empfänger ab und brächte nichts, weil der
Empfänger ohnehin nur die Aufrufer durchlässt, die er nennt.

**Token-Endpoint als Baustein.** Das Modul registriert Dienste; den Endpoint
mappt der Identity-Dienst selbst, an einem Pfad seiner Wahl:

```csharp
// identity-service
noelia.UseJwt(jwt => jwt.Issue(config["NOELIA_JWT_PRIVATE_KEY"]!, kid))
      .UseServiceTokenIssuer(issuer => issuer
          .Clients(config, "notification", "advisor", "transfer", "jobs" /* … */)
          .Audiences("identity", "notification")
          .Lifetime(TimeSpan.FromMinutes(5)));

app.MapServiceTokenEndpoint("/m2m/token")       // gibt RouteHandlerBuilder zurück
   .RequireRateLimiting("m2m-token");           // Sache des Dienstes
```

Wire-Format: OAuth 2.0 Client Credentials (RFC 6749 §4.4),
`application/x-www-form-urlencoded`: `grant_type=client_credentials`,
`client_id`, `client_secret`, `audience`. `audience` ist nicht Teil von RFC
6749; der Name folgt der verbreiteten Praxis, `resource` (RFC 8707) verlangte
absolute URIs. Nur `client_secret_post`, kein HTTP-Basic.

**Eingabe.** Der Endpoint bindet nichts über `[FromForm]` — das brächte
Antiforgery und eigene 400/415 vor jeder Prüfung. Er liest den Body selbst:
- Content-Type genau `application/x-www-form-urlencoded`, Body höchstens 4 KiB
  (gelesen wird nicht mehr), Antiforgery für diesen Endpoint aus;
- jedes der vier Felder genau einmal, mit Höchstlänge (Secret 43, IDs 48),
  unbekannte Felder ignoriert, doppelte Felder sind eine Ablehnung;
- Längen- und Formprüfung **vor** dem Hashen, damit niemand ohne Credential
  beliebig lange Werte hashen lässt;
- jede Ausnahme beim Lesen ist eine Ablehnung.

Ob `UseInputSanitization` Form-Bodys verändert oder abweist, sichert ein Test
mit der vollen `UseNoelia`-Kette; das Secret-Alphabet ist so gewählt, dass es
nichts zu bereinigen gibt.

- Erfolg: `200 {"access_token","token_type":"Bearer","expires_in"}`.
- **Jede** Ablehnung (unbekannte ID, falsches Secret, leerer Slot, falscher
  `grant_type`, fehlende oder unbekannte Audience, falscher Content-Type,
  zu großer, kaputter oder doppelt belegter Body): dieselbe Antwort
  `401 {"error":"invalid_client"}`. Eine unbekannte ID ist von einem falschen
  Secret nicht unterscheidbar.
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
  ["noelia-service+jwt"]`, `NameClaimType = client_id`,
  `MapInboundClaims = false`. Eigene, leere Events: kein Token aus dem
  Query-String, kein eigener Header (auch kein `Token-Expired`), nichts von der
  Konfiguration des Personen-Schemes geerbt. Ohne `KeyRing` (nur
  `From(authority)`) oder mit symmetrischem Ring: kein Start.
- `RequireServiceCaller(params string[])` und `[ServiceCaller]` setzen
  Autorisierungs-Metadaten mit Scheme `NoeliaService` und einer
  `ServiceCallerRequirement` (über `IAuthorizationRequirementData`, damit
  Minimal APIs und Controller denselben Weg nehmen). Eine leere Liste wirft beim
  Mappen. **Es gibt keinen Wildcard und keine Rolle „Service“.**
- Der Handler liest `client_id` **nur aus der Identity, die der Scheme
  `NoeliaService` authentifiziert hat**: Auswahl über
  `ClaimsIdentity.AuthenticationType == "NoeliaService"`, nie über
  `User.FindFirst`. Kombiniert ein Endpoint `[Authorize]` und `[ServiceCaller]`,
  führt die Policy-Auswertung beide Identities in einem Principal zusammen; ein
  Personen-Token mit selbstgesetztem `client_id`-Claim
  (`UserClaims.CustomClaims`) zählt trotzdem nicht.
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

**Vergleichsmaßstab** ist dieselbe Anfrage an einen nicht existierenden Pfad
**derselben Gruppe**, nicht irgendein unbekannter Pfad: endpoint-getriebene
Mechanik (Rate-Limit, Caching, CORS, Result-Writer) sieht an einer Tür
Metadaten, die ein Pfad ohne Endpoint nicht hat, und ein 429 an der Tür bei 404
daneben wäre ein Existenz-Orakel. Deshalb:

- `MapServiceFallback()` mappt `{**rest}` für **alle Methoden** der Gruppe und
  trägt selbst eine `ServiceCallerRequirement`, die niemand erfüllt (intern,
  nicht über die öffentliche API erreichbar). Jede Anfrage an den Fallback
  nimmt also denselben Weg wie eine abgelehnte Tür: dasselbe Scheme, derselbe
  Dekorierer, dasselbe 404 — auch für einen berechtigten Aufrufer auf einem
  Pfad, den es nicht gibt. Eine falsche Methode auf einer existierenden Tür
  trifft den Fallback statt des 405 (Präzedenz im Test mit echtem Routing
  belegt, nicht angenommen).
- Rate-Limit-, Caching- und CORS-Metadaten gehören auf die **Gruppe**, nie auf
  eine einzelne Tür. Der Check erzwingt das (unten).
- Weder Dekorierer noch Fallback schreiben einen Body (kein
  `Results.NotFound()` mit ProblemDetails). Weil nur der Statuscode gesetzt
  wird, verarbeiten `UseStatusCodePages`, ProblemDetails und Middleware weiter
  außen die Ablehnung genauso wie jedes andere 404 — Noelia baut das 404 des
  Dienstes nicht nach.
- Belegt wird das mit der vollen `UseNoelia`-Kette, nicht mit einem nackten
  Host, einschließlich der Frage, ob `UseAuthorization` dort nach dem Routing
  und vor der Endpoint-Ausführung hängt.

**Audit am Empfänger.** Nach außen gleich, nach innen nicht stumm: Der
Dekorierer schreibt je Anfrage ein Log-Ereignis und zählt
`noelia.m2m.requests` (Tags `outcome`, `reason`, `door`, bei Erfolg und bei
„nicht in der Liste“ zusätzlich `caller`).
- Erfolg: `client_id` und Tür (Route-Muster).
- Ablehnung: nur eine Grundklasse — `no_token`, `invalid_token`, `expired`,
  `wrong_type`, `wrong_audience`, `caller_not_allowed`, `other_policy` — plus
  Tür. Die Klasse legt das `OnAuthenticationFailed`-Ereignis des Service-Schemes
  in `HttpContext.Items` ab; sie verlässt den Prozess nur über Log und Metrik.
- Nie Token, Signatur, ungeprüfte Claim-Werte oder Teile davon. Eine
  `client_id` erscheint nur aus einem gültig signierten Token.

Ohne das wäre ein Tippfehler in der Audience oder ein rotiertes Secret im
Betrieb unsichtbar, genau wie beim Fail-open der WT-Aufrufer (F1).

**Personen-Scheme.** Tokens mit `typ = noelia-service+jwt` werden in **jedem**
Validierungspfad für Personen abgelehnt: im Bearer-Scheme mit `KeyRing`, im
Pfad ohne `KeyRing` (`keys is null`, `ServiceCollectionExtensions.cs:431`), in
`JwtService.ValidateTokenAsync` und in `GetPrincipalFromExpiredTokenAsync`
(Refresh mit `validateLifetime: false`). Umgesetzt als `TypeValidator` mit
**Sperrliste**, nicht als `ValidTypes`-Whitelist, damit bestehende
Personen-Tokens mit beliebigem `typ` weiter gelten. Das ist eine Verengung nur
für einen Token-Typ, den es vor 7.1 nicht gab, also nicht brechend. Dazu
verweigert `UseServiceCallers` den Start, wenn die eigene Audience gleich
`JwtSettings:Audience` ist.

**Selbstprüfung.** Neuer Check `noelia.m2m.caller-policy`
(`Category == Composition`, läuft also immer). Ohne eines der drei Module und
ohne `[ServiceCaller]`-Endpoints liefert er `Pass` mit „nicht verwendet“ — 7.1
ändert für bestehende Konsumenten kein Ergebnis. Sonst prüft er über die
`EndpointDataSource` und den aufgelösten Container:
1. Kein Endpoint trägt `[ServiceCaller]`/`RequireServiceCaller` **und**
   `AllowAnonymous` — eine Gruppe mit `.AllowAnonymous()` würde die Policy
   still aushebeln. → Fail.
2. Der aus dem Container **aufgelöste** `IAuthorizationMiddlewareResultHandler`
   ist der 404-Dekorierer (eine spätere Registrierung eines eigenen Handlers
   ersetzte ihn still). → Fail.
3. Keine Tür trägt Rate-Limit-, Caching- oder CORS-Metadaten, die der Fallback
   ihrer Gruppe nicht trägt. → Fail. Eine Tür ohne Fallback, der sie abdeckt. →
   Warn.
4. Ist `ServiceTokenIssuing` komponiert: genau ein Token-Endpoint (sonst Fail),
   und er trägt Rate-Limit-Metadaten (sonst Warn).

Zusammenfassung und Abhilfe nennen nur Formen (Route-Muster, Modul), nie
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
- `IServiceTokenProvider.GetTokenAsync()` ohne Audience nimmt im Modus
  `ClientCredentials` `M2MConfiguration.Audience`; ist die leer, wirft er
  (`InvalidOperationException`), statt `null` zu liefern.
- Scheitert die Token-Beschaffung, scheitert der Aufruf (Exception), und es
  geht kein Request hinaus. Heute ist das **nicht** so: liefert der Provider
  `null` oder leer, überspringt `ServiceCommunicationManager.cs:373-389` den
  Header, und der Block `:395-404` setzt `ServiceCommunication:BearerToken`
  oder schickt den Aufruf ohne Token. Im Modus `ClientCredentials` wirft diese
  Stelle bei `null`/leer und überspringt den Rückfall-Block; dasselbe gilt für
  `AddServiceToken`. Im Modus `Legacy` bleibt die Stelle, wie sie ist.
- `Legacy` bleibt auch dort unverändert, wo `UseServiceTokenClient` nicht
  aufgerufen wird (also bei jedem bestehenden Konsumenten).

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
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
S=$(openssl rand 32 | b64url)                       # 43 Zeichen
echo "NOELIA_M2M_CLIENT_SECRET=$S"
echo "NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH=sha256:$(printf %s "$S" | openssl dgst -sha256 -binary | b64url)"
```

Ein Test hält Generator und Rezept zusammen: dasselbe Secret ergibt in beiden
denselben Hash.

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
| T9 | nur die Hash-Konfiguration von identity | — | Hashes sind keine Credentials, **solange** die Secrets 256 Bit Zufall sind (Grenze) |
| T10 | einen symmetrischen `KeyRing` | Tokens für jeden Aufrufer | Start verweigert |

### Grenzen, offen benannt

- **identity ist der Vertrauensanker.** Wer identity übernimmt, hält den
  Signierschlüssel und prägt beliebige Dienst- und Personen-Tokens. Das Hashen
  der Secrets schützt nur, wenn Hashes **ohne** den Signierschlüssel abfließen
  (Log, Dashboard, Backup, ein Konfig-Dump eines anderen Ordners). Es macht
  identity nicht weniger kritisch als heute.
- **Entropie ist nicht prüfbar.** Die Form (43 Zeichen Base64url) sagt nichts
  darüber, ob ein Mensch das Secret gewählt hat. Ein solches Secret macht den
  ungesalzenen Hash offline angreifbar. Unterstützt sind nur `Generate` und das
  Rezept in Abschnitt 4.
- **Gemischte Flotte 7.0.x/7.1.** Die `typ`-Sperre gibt es nur in Diensten ab
  7.1. Ein 7.0.x-Dienst, dessen **Personen**-Audience zufällig einer
  Ziel-Audience des Ausstellers gleicht, nähme ein Dienst-Token als
  Personen-Token an (ohne `sub`, ohne Rollen, aber authentifiziert). Abhilfe:
  Dienst-Audiences und Personen-Audiences nie gleich benennen, und alle Dienste
  auf 7.1 heben, bevor der Aussteller Tokens ausgibt. Der Aussteller kennt nur
  seine eigene Personen-Audience und kann fremde nicht prüfen.
- **`Token-Expired`-Header.** Der Personen-Scheme setzt bei einem abgelaufenen,
  sonst gültigen Personen-Token `Token-Expired: true`
  (`ServiceCollectionExtensions.cs:466-470`). Er läuft in `UseAuthentication`
  für jeden Pfad, ist an Tür und Fallback also gleich und verrät nichts über
  Türen — wohl aber dem Inhaber, dass sein Token abgelaufen ist. Akzeptiert;
  im Test festgehalten.
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
| Quest | Inhalt | braucht |
|---|---|---|
| Q-0046 | dieses ADR | — |
| Q-0047 | Ausstellung: Secret-Form, Register, Hash, Slots, Ziel-Audiences, Token, Endpoint mit eigener Form-Lesung, Startprüfungen; `typ`-Sperre in allen Personen-Pfaden | Q-0046 |
| Q-0048 | Empfänger: Scheme, `RequireServiceCaller`/`[ServiceCaller]`, 404-Dekorierer mit Audit, Fallback, Check `noelia.m2m.caller-policy`; Wirkungstest der `typ`-Sperre (T4/T5) in der Matrix | Q-0047 (Aussteller als Test-Helfer) |
| Q-0049 | Client: `ClientCredentials` im `ServiceTokenProvider`, `IServiceTokenSource`, `AddServiceToken`, kein Rückfall; README (auch Modultabelle), MIGRATION, 7.1.0 | Q-0047, Q-0048 |

Die drei neuen `NoeliaModule`-Namen ziehen Katalog-, Dashboard- und
Check-Zähltests mit, wo es sie gibt (`tests/…/Security/Checks/SecurityCheckTests.cs`,
`tests/…/Dashboard/ComplianceViewTests.cs`).

Testplan (TDD, vorher rot):
- **Boss Ausstellung:** unbekannte ID, falsches Secret, leerer Slot,
  unbekannte Audience, Secret in falscher Form, falscher Content-Type,
  Body > 4 KiB, doppeltes `client_secret`, falscher `grant_type` → byte-gleiche
  401 mit `no-store`; beide Slots gültig; doppelte Hashes → kein Start (auch
  der alte gemeinsame Wert als `PREVIOUS` mehrerer Clients, WT §11);
  symmetrischer Ring, leere Audience-Menge, Audience = Personen-Audience →
  kein Start; Token trägt `typ`, eine `aud`, `client_id`, kein `sub`,
  `exp − iat` = Laufzeit; Generator und Shell-Rezept ergeben denselben Hash;
  volle `UseNoelia`-Kette inkl. Input-Sanitization verändert nichts.
- **`typ`-Sperre:** Dienst-Token in Bearer-Scheme mit und ohne `KeyRing`,
  `ValidateTokenAsync` und `GetPrincipalFromExpiredTokenAsync` → abgelehnt;
  Personen-Token mit fremdem `typ` (z. B. `at+jwt`) → weiter gültig.
- **Boss Empfänger** (volle `UseNoelia`-Kette, echtes Routing): Matrix
  Aufrufer × Tür (berechtigt 2xx, sonst 404); kein Token, kaputtes Token,
  abgelaufenes Token, falsche Audience, Personen-Token, Personen-Token mit
  `client_id`-Claim auf einer Tür mit zusätzlichem `[Authorize]`, doppelter
  `Authorization`-Header, falsche Methode, HEAD, OPTIONS, kaputter Body → 404,
  byte-gleich (Status, Header ohne `Date` und Korrelations-ID, Body) zur Antwort
  auf einen nicht existierenden Pfad **derselben Gruppe**, auch mit
  `UseStatusCodePages`; abgelaufenes Personen-Token: `Token-Expired` an Tür und
  Fallback gleich. Minimal API und Controller. Dienst-Token am
  Personen-Endpoint → 401. Audit: je Ablehnung die richtige Grundklasse im
  Log, nie ein Token-Wert.
- **Check:** ohne Module → Pass; `AllowAnonymous` + `ServiceCaller` → Fail;
  ein nach dem Dekorierer registrierter Handler, aus dem Container aufgelöst →
  Fail; Tür mit eigenem Rate-Limit ohne denselben am Fallback → Fail;
  Token-Endpoint ohne Rate-Limit → Warn.
- **Client:** zwei Audiences → zwei Tokens; kein Schreiben in
  `IDistributedCache`; Token-Endpoint antwortet 401 → Exception und **kein**
  Request an das Ziel (weder ohne Token noch mit `BearerToken`);
  `GetTokenAsync()` ohne konfigurierte Audience → Exception; `Legacy`
  unverändert (bestehende Tests unverändert grün).
- **End-to-end:** Aussteller, Client und Empfänger in einem Test: Aufrufer X
  kommt durch, Y bekommt 404.
- **Wirkung, nicht Rückgabe:** jeder Builder-Aufruf hat einen Test, der rot
  wird, wenn sein Rumpf fehlt.

## Fassung 2: Auflagen des Wardens

Design-Runde 1 (29.10.2026): approve-with-conditions. Eingearbeitet:

| # | Befund | Wo |
|---|---|---|
| B1 | Secret-Form widersprüchlich (43 vs. 44 Zeichen, `+/=`) | §1 Secret-Format, §4 Rezept, Grenze „Entropie“ |
| B2 | Audience beim Aussteller ungebunden | §1 Ziel-Audiences, Grenze „Gemischte Flotte“ |
| B3 | Empfänger ohne Audit | §2 Audit am Empfänger |
| B4 | Form-Bindung, Antiforgery, Längen | §1 Eingabe |
| B5 | Vergleichsbasis des 404 | §2 Vergleichsmaßstab, Fallback mit eigener Requirement, Check Punkt 3 |
| B6 | Rückfall im Client-Code | §3 |
| 7 | `typ`-Sperre in allen Pfaden, als Sperrliste | §2 Personen-Scheme |
| 8 | `Token-Expired`, Events des neuen Schemes | §2 Mechanik, Grenze |
| 9 | Rate-Limit am Token-Endpoint | Check Punkt 4 (Warn) |
| 10 | Zusammengeführter Principal | §2 Mechanik |
| 11–15 | Check ohne Module, Testplan, Quest-Abhängigkeiten, Zähltests | §2 Selbstprüfung, Testplan, Quest-Tabelle |

Delta-Runde (29.10.2026): **approve**, keine Blocker. Vier Präzisierungen
gehen als Auflagen in die Umsetzung:

- **F1 → Q-0048.** Ohne `Authorization`-Header feuert `OnAuthenticationFailed`
  nicht. `no_token` leitet der Dekorierer ab: Ergebnis ohne Authentifizierung
  und ohne Fehlereintrag in `HttpContext.Items`. Ein doppelter Header und ein
  Schema, das nicht `Bearer` ist, zählen als `invalid_token`, nicht als
  `no_token`. `other_policy` zeigt nach außen nur das 404. „Alle anderen
  Endpoints wie bisher“ heißt: Endpoints, deren Policy **keine**
  `ServiceCallerRequirement` enthält.
- **F2 → Q-0048.** `RequireServiceCaller`/`[ServiceCaller]` und
  `MapServiceFallback` legen ein internes Metadatum mit dem Präfix des
  Fallbacks ab. Darüber ordnet der Check eine Tür ihrem Fallback zu, mit
  Vergleich nach Metadaten-Typ und Policy-Name. Controller-Türen deckt ein
  `MapServiceFallback("/internal")` mit ausdrücklichem Präfix ab. Eine Tür ohne
  passenden Fallback bleibt Warn.
- **F3 → Q-0049.** Im Modus `ClientCredentials` wirft auch der Zweig „M2M an,
  aber kein Provider“ (`ServiceCommunicationManager.cs:390`), statt in den
  `BearerToken`-Rückfall zu laufen. Test: kein Provider ergibt eine Exception
  und keinen Request.
- **F4 → Q-0047.** Verglichen wird nur der Media-Type, sodass `; charset=utf-8`
  zulässig ist. Das Secret wird strikt als kanonisches Base64url dekodiert: die
  ungenutzten Bits der 43. Stelle sind 0, sonst Ablehnung.
