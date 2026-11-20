# ADR-0007 — Rechte und Rollen zählen nur aus angemeldeten Identitäten

**Status:** Angenommen für `develop` (Fassung 4). Owner-Entscheid wörtlich eingehalten. Warden: changes-requested in der schmalen Prüfung; Korrektur (Handler ohne angemeldete Person = nicht erfüllt) eingearbeitet; Warden-Bestätigung der Korrektur: approve ([#59, Kommentar](https://github.com/DavidOeztuerk/noelia/issues/59#issuecomment-7051317830)). Die Umsetzung (Q-0070) prüft der Warden in der Gate-Runde. · **Datum:** 2026-11-20 · **Gate:** G-0018 (Rank A, Rechte) · **Quest:** Q-0069 (Design), Q-0070 (Umsetzung) · **Issue:** #59 · **Ziel:** Minor 7.2.0 mit Migrationshinweis, **keine** neue öffentliche API. Dieses Gate veröffentlicht nichts.

## Owner-Entscheid vom 20.11.2026 (wörtlich)

> „#59 bekommt ein eigenes Gate und wird Teil eines Minor-Release (7.2.0), weil der Fix Konsumenten brechen kann. Die Lücke wird geschlossen, nicht nur dokumentiert: Rechte und Rollen zählen nur aus angemeldeten Identitäten, in PermissionMiddleware und PermissionAuthorizationHandler gleich. Der Bruch für Konsumenten mit IClaimsTransformation steht in MIGRATION.md, mit dem Weg, wie sie ihre Claims richtig anhängen."

### Auslegung durch dieses ADR (nicht Teil des Wortlauts)

Dieses ADR setzt den Entscheid um. Es lässt nichts davon offen und wählt keine Alternative, die ihm widerspricht. Dass der Fix über die beiden genannten Pfade hinaus weitere Noelia-Prüfungen erfasst (Entscheidung 3), ist keine Abweichung, sondern seine Voraussetzung: Entscheidung 2 nimmt in der Standardkette die 401 weg, die diese Pfade heute verdeckt.

## In einfacher Sprache

Eine Anfrage trägt in .NET einen „Principal“. Der kann mehrere Identitäten enthalten, zum Beispiel
die Person, die sich angemeldet hat, und eine zweite, die ein Baustein später angehängt hat. Jede
Identität weiß von sich, ob sie angemeldet ist, also ob sie einen `AuthenticationType` trägt.

Noelias Rechteprüfung fragt aber nur die **erste** Identität, ob sie angemeldet ist, und liest dann
Rechte und Rollen aus **allen**. Dadurch gibt es zwei Fehler:

1. Hängt jemand an eine angemeldete Person eine zweite Identität an, die **nicht** angemeldet ist, aber
   ein Recht trägt, dann **zählt dieses Recht**. Die Person kommt durch, obwohl ihr dieses Recht nie
   ausgestellt wurde. Das ist offen, nicht „geschlossen gescheitert“.
2. Steht die nicht angemeldete Identität vorn, wird die Anfrage abgelehnt, obwohl eine angemeldete Person
   dabei ist. Das ist nur lästig.

Beim Prüfen zeigte sich: Der Fehler sitzt nicht nur in diesen zwei Pfaden, sondern in fast allen Prüfungen,
die Noelia selbst mitbringt (Katalog- und Rollen-Policies, Kontostand, Besitz, Mandant). Heute verdeckt die
Rechte-Middleware das in der Standardkette zum Teil. Würde man nur sie reparieren, kämen die anderen Wege
frei. Darum gilt die Regel überall dort, wo Noelia selbst Rechte, Rollen, Nutzer-ID, Kontostand oder Mandant liest.

Die Regel nach dem Fix: **Was zählt, kommt nur von angemeldeten Identitäten.** Eine Identität ohne
`AuthenticationType` wird dafür behandelt, als wäre sie nicht da. Alle diese Prüfungen benutzen denselben
Baustein, also kann keine von ihnen etwas anderes antworten als die anderen.

Was sich für Nutzer ändert:

- Wer Claims über `IClaimsTransformation` mit `principal.AddIdentity(new ClaimsIdentity(...))` und **ohne**
  `AuthenticationType` anhängt, bekommt künftig 403 statt 200. Die Claims zählen nicht mehr. Das scheitert
  geschlossen, bricht aber diese Konsumenten. Deshalb ein Minor mit Migrationsweg, kein Patch.
- Wer die Claims an die angemeldete **Personen**-Identität hängt, merkt nichts.
- Eine Anfrage mit nur nicht angemeldeten Identitäten bekommt 401, mit einer angemeldeten ohne das Recht 403.
- Eine **widerrufene** Person kommt in keinem Aufbau mehr durch, auch nicht, wenn zwei Personen-Schemes im
  selben Principal stehen (Entscheidung 4).
- Eigener Code der Konsumenten ist nicht betroffen: `[Authorize(Roles=…)]`, `RequireRole`, `RequireClaim` und
  `User.IsInRole` zählen in ASP.NET weiter alle Identitäten. Die Regel gilt für Noelias eigene Prüfungen.

## Frage

Welche Identitäten eines Principals dürfen Rechte, Rollen, Nutzer-ID, Kontostand und Mandant liefern, und wie
stellen wir sicher, dass Middleware, Handler, Widerruf, Prüfung je Datensatz und Principal dasselbe beantworten?

## Befund, zur Laufzeit belegt

### Die beiden Pfade aus #59 (Warden-Probe)

Endpoint mit `[RequirePermission("orders:delete")]`, Principal mit zwei Identitäten:

| Fall | Identitäten | `PermissionMiddleware` | `PermissionAuthorizationHandler` |
|---|---|---|---|
| A | angemeldet ohne Recht + **unangemeldet mit `permission`** dahinter | **200** | **Succeeded** |
| B | unangemeldet mit `permission` vorn + angemeldet ohne Recht | 401 | nicht erfüllt |
| C | eine Identität, angemeldet, ohne Recht | 403 | nicht erfüllt |

Ursache: `PermissionMiddleware.cs:64` und `PermissionAuthorizationHandler.cs:46` fragen `User.Identity.IsAuthenticated`
(die **erste** Identität); `GetUserPermissions` (`PermissionMiddleware.cs:112–123`,
`PermissionAuthorizationHandler.cs:76–92`) liest `permission` und Rollen über **alle**; die Nutzer-ID kommt per
`FindFirst(NameIdentifier)` über alle (`PermissionMiddleware.cs:84`, `PermissionAuthorizationHandler.cs:52`).

### Dieselbe Fehlerklasse an weiteren Stellen (Warden-Runde 1, Laufzeitprobe gegen 28c4750)

Kontrolle jeweils: eine angemeldete Identität ohne Recht, Konto `Suspended`; alle Kontrollen ergaben „abgelehnt“.

| Stelle | Fehler | Probe |
|---|---|---|
| `Security/AuthorizationExtensions.cs:42–45` (`RequireClaim("permission", p)` je Katalogrecht) und `:27–34` (`RequireRole` Admin/Moderator/User); `Security/Authorization/AuthorizationExtensions.cs:52–56` (`AdminOnly`/`SuperAdminOnly`) | ASP.NET wertet `RequireClaim`/`RequireRole` über alle Identitäten aus; die Katalog-Policies haben kein `RequireAuthenticatedUser` | erfüllt in A, in B und sogar bei nur unangemeldeten Identitäten |
| `Security/Authorization/AuthorizationExtensions.cs:136–140` (`ResourceAuthorizationHandler`) und `:313` (`OwnershipAuthorizationHandler`) | geben das ungefilterte `context.User` weiter bzw. lesen `FindFirst(NameIdentifier)` über alle; die Provider lesen ebenfalls per `FindFirst` (`Noelia.InMemory/.../InMemoryResourceAuthorizationService.cs:30`, `Noelia.Redis/.../ResourceAuthorizationService.cs:81`) | Datensatz des Opfers freigegeben |
| `Security/ActiveAccountHandler.cs:20`, `Security/EmailVerifiedHandler.cs:20` | `FindFirst` über alle: ein gesperrtes Konto kommt durch, wenn eine unangemeldete Identität `account_status=Active` trägt | erfüllt |
| `Security/ResourceOwnerHandler.cs:30`, `:40` | `FindFirst` und `IsInRole(Admin)` über alle | erfüllt |
| `Security/Identity/PrincipalFactory.cs:13`, `:18–19`, `:26` | prüft die erste Identität, liest `sub` **und `tenant`** über alle: ein unangemeldetes `tenant` hinter der Person macht sie zu „handelt für Firma T“, `TenantForQueryFilter` wird T, `ActingForCompany` ist erfüllt (Mandantentrennung, `NoeliaTenant`-Filter, ADR-0006) | erfüllt |
| `Security/TokenRevocationMiddleware.cs:46–48`, `:90–92` | Entscheidung an der ersten Identität, solange keine Dienst-Identität im Principal liegt; `jti`/`sub`/`iat` per `FindFirst` über alle, dabei wird `NameIdentifier` vor `sub` gezogen, die Werte können aus verschiedenen Identitäten stammen; bei zwei Personen-Schemes wird nur ein Token geprüft | (Code belegt) |
| `Security/Authorization/ResourceAuthorizationMiddleware.cs:40–48`, `:65` | dasselbe Muster | (Code belegt) |
| `Security/Audit/SecurityAuditMiddleware.cs:75`, `Security/SecurityAuditMiddleware.cs:62`, `Security/Monitoring/SecurityAlertService.cs:484`, `Builder/Modules/MiddlewarePipelineModule.cs:87` | `FindFirst` bzw. Entscheidung über alle: eine unangemeldete Identität vorn kann dem Prüfprotokoll eine fremde ID unterschieben | (Code belegt) |

Das Gefährliche: **Ein Fix allein in der Middleware öffnet Wege.** Heute verdeckt die 401 aus
`PermissionMiddleware.cs:64` in Fall B jede Handler-Entscheidung, die aus der vorn stehenden unangemeldeten Identität
getroffen wurde (`UseAuthorization` läuft vor `PermissionMiddleware`, `MiddlewarePipelineModule.cs:239` gegen `:262`,
`ServiceCollectionExtensions.cs:229–231`). Mit `Identities.Any(...)` allein käme außerdem eine **widerrufene Person durch**
(Probe: nächster Schritt läuft, 200), sobald eine Policy das Personen-Scheme und ein fehlerhaftes zweites Scheme nennt.

## Entscheidungen

1. **Ein gemeinsamer interner Baustein.** In `Noelia.Infrastructure.Security` entsteht ein interner Helfer
   (Arbeitsname `AuthenticatedIdentities`):
   - `IsAuthenticated(principal)`: wahr, wenn **irgendeine** Identität angemeldet ist.
   - `Of(principal)`: ein `ClaimsPrincipal` mit **nur** den angemeldeten Identitäten, in ihrer Reihenfolge.
   Er ist `internal`; es entsteht keine neue öffentliche API. Er liegt neben `ServiceCallerIdentity`.
   **Kriterium und Grenze:** „angemeldet“ heißt `ClaimsIdentity.IsAuthenticated`, also ein gesetzter `AuthenticationType`. Dass
   dahinter wirklich ein Scheme geprüft hat, kann der Baustein nicht wissen: Setzt ein Konsument `AuthenticationType` selbst
   auf eine Identität, die kein Scheme geprüft hat, zählt sie. Diese Trennung liegt beim Betreiber (wie in ADR-0003).

2. **Rechte, Rollen, Wildcards und Nutzer-ID nur aus `Of(principal)`.** `PermissionMiddleware` und
   `PermissionAuthorizationHandler` fragen zuerst `IsAuthenticated` (keine: Middleware 401, Handler nicht erfüllt) und lesen
   danach ausschließlich aus dem gefilterten Principal: `permission`, Rollen über den Katalog, `*`, `kategorie:*`, `Permission:…`
   mit `requirement.Resource`, und die Nutzer-ID für Log und Alarm. Die Reihenfolge der Identitäten spielt keine Rolle. Fall A
   und Fall B ergeben beide **403**; nur „gar keine angemeldete Identität“ ergibt 401.

3. **Dieselbe Regel für jede Stelle, an der Noelia selbst Rechte, Rollen, Nutzer-ID, Kontostand oder Mandant liest.** Jede Zeile
   der Befundtabelle oben liest künftig aus einem der beiden Bausteine, und zwar **je Stelle festgelegt, nicht „bzw.“**:
   `Of(principal)` für **Rechte und Rollen** (Entscheidung 2 und die Katalog- und Rollen-Policies), `PersonPartOf` für alles, was
   **eine Person** betrifft (Datensatz, Besitz, Nutzer-ID, Principal, Kontostand, Audit). Wo eine Aussage zu einer Person gehört,
   wird sie **je angemeldeter Personen-Identität einzeln** gelesen, mit `identity.FindFirst` und nie über die Grenze zweier
   Identitäten (Entscheidung 4). Im Einzelnen:
   - **Katalog- und Rollen-Policies** (`RequireClaim("permission", …)`, `RequireRole`, `AdminOnly`, `SuperAdminOnly`) werden
     durch interne Requirements mit Handlern ersetzt und bekommen `RequireAuthenticatedUser`. Die Handler werten **die
     Framework-Regel auf `Of(principal)` aus**, damit die Bedeutung bleibt: exakter Wert, **kein** `*`, **kein** `kategorie:*`,
     **keine** Katalogrollen; Claim-Typ `OrdinalIgnoreCase`, Wert `Ordinal`; Rollen je Identität über deren `RoleClaimType`
     (`identity.HasClaim(identity.RoleClaimType, rolle)` bzw. `Of(principal).IsInRole(rolle)`), nicht fest `ClaimTypes.Role`, damit Schemes mit eigenem `RoleClaimType` (OIDC) weiter funktionieren.
     Die Katalog-Policy zählt weiter nur den `permission`-Claim; die offene Roadmap-Frage `README.md:2218–2224` wird nicht
     nebenbei entschieden. Policy-Namen und öffentliche API bleiben gleich. Der gemeinsame `HasPermission` wird dafür **nicht**
     wiederverwendet (er kennt Wildcards und Katalogrollen).
   - **Die fünf Personen-Handler** (`ActiveAccountHandler`, `EmailVerifiedHandler`, `ResourceAuthorizationHandler`,
     `OwnershipAuthorizationHandler`, `ResourceOwnerHandler`) **gelten als „mindestens eine angemeldete Personen-Identität **und** jede
     besteht“. Keine Person heißt nicht erfüllt**, und zwar durch `context.Fail()` (wie heute: Nutzer-ID bzw. Claim fehlt; heute rufen alle
     fünf `Fail()`). Bei `ResourceAuthorizationHandler` stimmt „wie heute“ nur dank der Noelia-Provider; mit einem eigenen Provider ist die
     Regel strenger als heute, und `MIGRATION.md` sagt das. **Für die Rechte- und Rollen-Handler** (`PermissionAuthorizationHandler` und
     die Handler der Katalog- und Rollen-Policies) gilt etwas anderes, ausdrücklich: Es reicht **irgendeine** angemeldete Identität
     (nicht „jede“), eine **Dienst-Identität zählt mit** (ihr `permission`-Claim gilt unverändert, Entscheidung 6), und kein angemeldetes
     Konto heißt nicht erfüllt. Der Absatz hier gilt nicht für sie.
     Das „Durchlassen ohne Person“ aus Entscheidung 4 gilt **nur** für die beiden Middlewares,
     die bloß filtern; in einem Handler hieße es „Succeed“, also eine Zulassung für anonyme Aufrufer und reine Dienst-Principals.
     Dazu bekommen `RequireActiveAccount`, `RequireVerifiedEmail`, `ResourceRead`, `ResourceWrite`, `ResourceDelete` und
     `ResourceOwner` zusätzlich `RequireAuthenticatedUser`.
   - **`ResourceAuthorizationHandler` und `OwnershipAuthorizationHandler`** wie die Middleware (Entscheidung 4): je Personen-Identität,
     jede muss bestehen; eine Identität ohne Nutzer-ID ergibt wie heute „nicht erfüllt“.
   - **`ActiveAccountHandler`, `EmailVerifiedHandler`:** **jede** angemeldete Personen-Identität muss `account_status=Active`
     bzw. `email_verified=true` sagen. Fehlt die Aussage oder sagt sie „nein“, wird abgelehnt. Das folgt `UserClaims.cs:27–28`
     und `:43`: eine fehlende Aussage darf zu „nein“ führen, nie zu „ja“.
   - **`ResourceOwnerHandler`** wird wie `OwnershipAuthorizationHandler` behandelt: je Personen-Identität, jede muss bestehen
     (Besitz über deren Nutzer-ID, Admin über deren eigene Rollen); so geben beide Besitz-Handler bei mehreren Personen dieselbe
     Antwort. Die Audit- und Alarmstellen (`MiddlewarePipelineModule.cs:87` eingeschlossen) lesen aus `PersonPartOf`; die
     Nutzer-ID im Protokoll stammt aus der ersten angemeldeten Personen-Identität.
   - **`PrincipalFactory`** liest `sub` **und** `tenant` aus **derselben** Identität, der ersten angemeldeten Personen-Identität.
     So entsteht nie „Person A mit dem Mandanten von B“. Die Fabrik ist öffentlich und filtert selbst, damit auch eine
     Eigenverwendung durch Konsumenten dasselbe erhält.

4. **Widerruf und Prüfung je Datensatz gehen je angemeldeter Personen-Identität einzeln.**
   - **`TokenRevocationMiddleware`** prüft jede angemeldete Identität, die keine Dienst-Identität ist, **einzeln**; `jti`, `sub`,
     `iat` **und die Sitzung** stammen aus derselben Identität (`identity.FindFirst`; die Sitzung über einen internen Overload
     von `SessionClaims.Read` bzw. `new ClaimsPrincipal(identity)`; heute liest `SessionClaims.cs:23–27` `session_id` vor `sid`
     über alle Identitäten, aufgerufen in `TokenRevocationMiddleware.cs:105`). Ist eine davon widerrufen oder nicht prüfbar,
     wird abgelehnt (401).
   - **`ResourceAuthorizationMiddleware`** prüft **den Datensatz** je Personen-Identität, jede muss bestehen; fehlt einer Identität
     die Nutzer-ID, ergibt das wie heute 403. Sie liest weder `jti` noch `iat` und lehnt darum Personen ohne `jti` (etwa per
     Cookie) **nicht** ab. Ihr Zweig, der bei einer Ausnahme durchlässt (`ResourceAuthorizationMiddleware.cs:74–78`,
     „Continue processing“), wird **fail-closed** (Ablehnung); das liegt im Scope.
   - Ist **keine** Person angemeldet, lassen beide wie bisher durch; die Ablehnung kommt von der Anmelde- und Rechteprüfung.
   Das schließt „in keinem Aufbau“ ein, auch zwei Personen-Schemes in einer Policy.

5. **`PersonPartOf` filtert immer.** `ServiceCallerIdentity.PersonPartOf` gibt künftig **immer** nur angemeldete Identitäten
   ohne die Dienst-Identität zurück (bisher kam ohne Dienst-Identität der Principal unverändert zurück). Das **ersetzt diesen
   Teil von ADR-0003** (die Zusicherung „comes back as it went in“, `ServiceCallerHandler.cs:56–58`); die Remarks werden
   angepasst. Folge für Konsumenten: Eine eigene `IPrincipalFactory` bekommt einen gefilterten Principal (MIGRATION.md).

6. **Dienst-Tokens ändern sich nicht.** Die Identität des Dienst-Schemes ist angemeldet. Ein `permission`-Claim darin zählte
   vorher und zählt nachher. Noelias Aussteller schreibt nur `client_id` und `jti` hinein (`ServiceTokenIssuer.cs:98–105`),
   verleiht also von sich aus kein Recht. Das ist keine Änderung, wird festgehalten, damit niemand sie als Nebenwirkung liest.

7. **Der Fix ist gewollt ein Bruch, kein Patch.** Konsumenten mit `IClaimsTransformation`, die eine **zusätzliche** Identität
   ohne `AuthenticationType` anhängen, verlieren ihre Rechte (200 → 403). Das ist die geschlossene Richtung, und der Owner hat
   sie ausdrücklich gewählt.

8. **Was dieses Gate nicht anfasst** (außerhalb des Scopes, Issue „Nacharbeit G-0018“, soweit nicht bereits Issue):
   `Extensions/ClaimsPrincipalExtensions.cs` (öffentliche Helfer, in `src/` und `demo/` nicht aufgerufen, eigene Entscheidung wegen
   öffentlicher Signaturen), `Noelia.Http/.../DistributedRateLimitingMiddleware.cs:114,119` (Schlüsselwahl für das Limit: fehlt der
   Person `sub`, kann der Schlüssel aus einer unangemeldeten Identität stammen, das Limit trifft einen fremden Nutzer oder lässt sich
   umgehen), `Observability/TelemetryMiddleware.cs:137`, `Caching/Http/ResponseCachingHeaderMiddleware.cs:267` (ETag-Schlüssel),
   `Noelia.Dashboard/NoeliaDashboardMiddleware.cs:222`, `Noelia.Cli/InitCommand.cs:337` (erzeugt `VisibleTo(... IsInRole("operator"))`),
   `LoggingBehavior.cs:244` (nur Log).

9. **Migrationsweg (Text für `MIGRATION.md`).** Eine `IClaimsTransformation` läuft bei **jedem** `AuthenticateAsync`, für **jedes**
   Scheme, auch für das Dienst-Scheme `NoeliaService`. Wer Claims ergänzt, wählt deshalb die angemeldete **Personen**-Identität
   und leitet die Rechte aus deren `sub` ab:

   ```csharp
   var person = principal.Identities.FirstOrDefault(i =>
       i.IsAuthenticated && i.AuthenticationType != ServiceTokenDefaults.AuthenticationScheme);
   // Schemes mit MapInboundClaims tragen NameIdentifier statt sub
   var sub = person?.FindFirst("sub")?.Value ?? person?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
   if (person is not null && sub is not null)
   {
       foreach (var right in await rights.LookupAsync(sub))            // Rechte aus dem sub nachschlagen
       {
           if (!person.HasClaim("permission", right))                  // die Transformation läuft mehrfach
           {
               person.AddClaim(new Claim("permission", right));
           }
       }
   }
   ```

   Nicht: an `principal.Identity` anhängen (kann die unangemeldete erste Identität sein, die Claims zählen dann nicht), nicht an die
   Dienst-Identität (ein Dienst-Token bekäme Personenrechte, Entscheidung 6), und nicht der angehängten Identität selbst einen
   `AuthenticationType` geben, damit sie „zählt“: Das gäbe eine Identität als angemeldet aus, die kein Scheme geprüft hat.
   `ServiceTokenDefaults.AuthenticationScheme` ist öffentlich.

## Verworfen

- **Nur `Identities.Any(i => i.IsAuthenticated)` in `PermissionMiddleware`.** Lässt Fall A offen, lässt den Handler und die
  weiteren Wege unberührt und legt Widerruf und Handler frei.
- **Nur dokumentieren.** Gegen den Owner-Entscheid („geschlossen, nicht nur dokumentiert“).
- **Als Patch 7.1.2 ausliefern.** Gegen den Owner-Entscheid; der Fix bricht Konsumenten, also Minor 7.2.0.
- **Eine Option „unangemeldete Identitäten zulassen“.** Würde die Lücke als Schalter erhalten.
- **Die Katalog-Policies auf die Rolle ausdehnen.** Würde die offene Roadmap-Frage (`README.md:2218–2224`) nebenbei entscheiden.

## Prüfung (Plan für Q-0070)

Rot zuerst, dann grün, je Zeile mit Mutationsprobe (Körper des Fixes entfernen, Test muss rot werden):

- **Matrix** {`permission`, Rolle über Katalog, `*`, `kategorie:*`, `Permission:` mit `Resource`} × {unangemeldete Identität vorn,
  hinten} mit angemeldeter Person ohne Recht: **403** in `PermissionMiddleware` **und** `PermissionAuthorizationHandler`.
  Nur unangemeldete Identitäten: **401** (Handler: nicht erfüllt). Positiv: Recht an der angemeldeten Identität, unangemeldete
  daneben stört nicht (200).
- **Handler-Pfad der Standardkette mit `PermissionEnforcement`, Fall B, je Policy:** Katalog-Policy, `RequireAdminRole`, `AdminOnly`,
  `RequireActiveAccount`, `RequireVerifiedEmail`, `ResourceOwner` (Policy mit `OwnershipRequirement`), `ResourceRead`, und der
  eigenständige `ResourceOwnerRequirement` (Handler `ResourceOwnerHandler`, keine benannte Policy). Das sind die Zeilen, die rot werden,
  wenn Entscheidung 2 ohne Entscheidung 3 landet. Dazu: nur unangemeldete Identität bei Katalog- und Rollen-Policies.
- **Widerruf:** widerrufene Person mit unangemeldeter Identität vorn/hinten, durch die ganze Kette: 401. Zwei Personen-Schemes in
  beiden Reihenfolgen, eines widerrufen: 401. Auch mit `Without(PermissionEnforcement)`.
- **Prüfung je Datensatz und Besitzerprüfung** (Middleware und Handler) mit unangemeldeter Identität mit fremder ID vorn und hinten.
- **Kontostand:** unangemeldete Identität mit `account_status=Active` vorn, angemeldete `Suspended`: abgelehnt.
- **Mandant und Kapazität:** unangemeldetes `tenant` hinter der Person: `ICurrentPrincipal.Current` bleibt Person, `ActingForCompany`
  nicht erfüllt, `ActingAsSelf` erfüllt, in beiden Reihenfolgen.
- **Audit:** Nutzer-ID im Protokoll stammt aus der angemeldeten Identität.
- **`IClaimsTransformation`:** zusätzliche Identität ohne `AuthenticationType` zählt nicht (403); dieselben Claims an der angemeldeten
  Personen-Identität zählen (200); mit Dienst-Principal hängt die Beispiel-Transformation **nichts** an die Dienst-Identität.
- **Keine Person (Fassung 4), je Personen-Handler direkt geprüft** (`ActiveAccountHandler`, `EmailVerifiedHandler`,
  `ResourceAuthorizationHandler`, `OwnershipAuthorizationHandler`, `ResourceOwnerHandler`), und zwar **ohne** `RequireAuthenticatedUser`
  (eine Konsumenten-Policy kann ohne es auf den Handler zeigen; der eigenständige `ResourceOwnerRequirement` hat keine benannte
  Policy; die Anforderungstypen sind öffentlich). Drei Fälle je Handler, jeder `Fail()`/nicht erfüllt:
  (a) Principal ganz ohne Identität;
  (b) nur eine unangemeldete Identität **mit** `NameIdentifier`, `account_status=Active`, `email_verified=true` und `Admin`;
  (c) Dienst-Identität plus diese unangemeldete Identität.
  Mutationsproben: ohne die Prüfung „mindestens eine Person“ werden (b) und (c) rot; gibt `PersonPartOf` den Principal wie bisher
  unverändert zurück (Entscheidung 5), wird (b) rot; ein Handler, der über die leere Menge „alle bestehen“ prüft, wird bei (a) rot
  und würde ohne diese Fälle anonymen und reinen Dienst-Principals Besitz zusprechen. Zusätzlich die benannten Policies
  (`RequireActiveAccount`, `RequireVerifiedEmail`, `ResourceOwner`, `ResourceRead`) mit (a) und nur Dienst-Identität: nicht erfüllt.
  Rechte- und Rollen-Handler: kein angemeldetes Konto nicht erfüllt, Dienst-Identität mit `permission` erfüllt (Entscheidung 6).
- **Zwei angemeldete Personen-Identitäten (Fassung 3):** Sitzung der zweiten widerrufen, `sid`/`session_id` gemischt: 401. `sub`
  von A und `tenant` von B: `Current` ist Person A ohne Mandant, `ActingForCompany` nicht erfüllt. Kontostand: eine Identität ohne
  Aussage und eine mit `Active`: abgelehnt. Prüfung je Datensatz: jede muss bestehen, ohne `jti` (Cookie) kein 401.
  Ausnahme im Provider der Datensatzprüfung: abgelehnt, nicht durchgelassen.
- **Bedeutung der Katalog- und Rollen-Policies bleibt:** nur Rolle, `*` und `orders:*` bleiben an der Katalog-Policy abgelehnt;
  eine Rollen-Policy mit eigenem `RoleClaimType` funktioniert.
- **Dienst-Identität plus unangemeldete Identität an einer Tür:** Dienst-Claims zählen unverändert, die unangemeldete nicht.
- Dienst-Token: `permission` in der Dienst-Identität zählt unverändert (Entscheidung 6).

## Folgen

- `MIGRATION.md` bekommt einen Abschnitt „7.2.0“ mit dem Bruch, dem Weg aus Entscheidung 9, dem Hinweis auf `IPrincipalFactory`
  (Entscheidung 5), dem Hinweis auf den neuen 401 aus Entscheidung 4 (eine zweite angemeldete Personen-Identität ohne
  `jti`/`sub`/`iat` wird bei aktivem Widerruf abgelehnt; trifft Policies mit mehreren Schemes wie Bearer plus Cookie oder
  API-Key und Konsumenten, die ihrer angehängten Identität einen `AuthenticationType` gegeben haben; scheitert geschlossen),
  dem Hinweis, dass die Datensatzprüfung bei einer Ausnahme im Provider jetzt ablehnt statt durchzulassen (Konsumenten mit einem
  eigenen, werfenden Provider sehen 403/401 statt 200),
  dem Hinweis, dass die Personen-Handler ohne angemeldete Personen-Identität jetzt mit `Fail()` ablehnen (mit einem eigenen
  Provider der Datensatzprüfung strenger als heute),
  und dem Hinweis, dass eigener Code (`[Authorize(Roles=…)]`, `RequireRole`, `RequireClaim`, `User.IsInRole`)
  weiter alle Identitäten zählt. Hinweise für Q-0070 aus der Warden-Prüfung: Die Tests prüfen `HasFailed`, nicht nur
  `!HasSucceeded`; „kein angemeldetes Konto“ heißt „keine angemeldete Identität“; ein Test hält fest, dass eine eigene
  `IPrincipalFactory` nur angemeldete Identitäten ohne Dienst-Identität bekommt (Entscheidung 5). Das README nennt die Regel dort, wo Rechte und Rollen erklärt werden.
- Unter `docs/befunde/` entsteht ein Befund: was die Prüfungen taten, was sie versprachen, Noelia-only-Reproduktion (auch
  Mandant), Status.
- Die Remarks von `ServiceCallerIdentity.PersonPartOf` werden angepasst (Entscheidung 5).
- Die Versionsnummer bleibt in diesem Gate bei 7.1.1; den Sprung auf 7.2.0 und die Veröffentlichung macht der Owner im Release.
- Issue „Nacharbeit G-0018“ für die Stellen aus Entscheidung 8.
