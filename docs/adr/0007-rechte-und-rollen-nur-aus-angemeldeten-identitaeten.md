# ADR-0007 — Rechte und Rollen zählen nur aus angemeldeten Identitäten

**Status:** Entwurf (Fassung 1, wartet auf Warden-Freigabe) · **Datum:** 2026-11-20 · **Gate:** G-0018 (Rank A, Rechte) · **Quest:** Q-0069 (Design), Q-0070 (Umsetzung) · **Issue:** #59 · **Ziel:** Minor 7.2.0 mit Migrationshinweis, **keine** neue öffentliche API. Dieses Gate veröffentlicht nichts.

## Owner-Entscheid vom 20.11.2026 (wörtlich)

> „#59 bekommt ein eigenes Gate und wird Teil eines Minor-Release (7.2.0), weil der Fix Konsumenten brechen kann. Die Lücke wird geschlossen, nicht nur dokumentiert: Rechte und Rollen zählen nur aus angemeldeten Identitäten, in PermissionMiddleware und PermissionAuthorizationHandler gleich. Der Bruch für Konsumenten mit IClaimsTransformation steht in MIGRATION.md, mit dem Weg, wie sie ihre Claims richtig anhängen."

Dieses ADR setzt den Entscheid um. Es lässt nichts davon offen und wählt keine Alternative, die ihm widerspricht.

## In einfacher Sprache

Eine Anfrage trägt in .NET einen „Principal“. Der kann mehrere Identitäten enthalten, zum Beispiel
die Person, die sich angemeldet hat, und eine zweite, die ein Baustein später angehängt hat. Jede
Identität weiß von sich, ob sie angemeldet ist, also ob ein Anmelde-Scheme sie geprüft hat.

Noelias Rechteprüfung fragt aber nur die **erste** Identität, ob sie angemeldet ist, und liest dann
Rechte und Rollen aus **allen**. Dadurch gibt es zwei Fehler:

1. Hängt jemand an eine angemeldete Person eine zweite Identität an, die **nicht** angemeldet ist, aber
   ein Recht trägt, dann **zählt dieses Recht**. Die Person kommt durch, obwohl ihr dieses Recht nie
   ausgestellt wurde. Das ist offen, nicht „geschlossen gescheitert“.
2. Steht die nicht angemeldete Identität vorn, wird die Anfrage abgelehnt, obwohl eine angemeldete Person
   dabei ist. Das ist nur lästig.

Die Regel nach dem Fix: **Was zählt, kommt nur von angemeldeten Identitäten.** Eine Identität, die kein
Anmelde-Scheme geprüft hat, wird für Rechte, Rollen und die Nutzer-ID behandelt, als wäre sie nicht da.
Das gilt in der Middleware und im Handler gleich, weil beide denselben Baustein benutzen.

Was sich für Nutzer ändert:

- Wer Claims über `IClaimsTransformation` mit `principal.AddIdentity(new ClaimsIdentity(...))` und **ohne**
  `AuthenticationType` anhängt, bekommt künftig 403 statt 200. Die Claims zählen nicht mehr. Das scheitert
  geschlossen, bricht aber diese Konsumenten. Deshalb ein Minor mit Migrationsweg, kein Patch.
- Wer die Claims an die angemeldete Identität hängt, merkt nichts.
- Eine Anfrage mit nur nicht angemeldeten Identitäten bekommt 401, mit einer angemeldeten ohne das Recht 403.
- Eine **widerrufene** Person kommt in keinem Aufbau mehr durch (siehe Entscheidung 3).

## Frage

Welche Identitäten eines Principals dürfen Rechte, Rollen und die Nutzer-ID liefern, und wie stellen wir
sicher, dass Middleware, Handler, Widerruf und Prüfung je Datensatz dasselbe beantworten?

## Befund, zur Laufzeit belegt

Laufzeitproben des Wardens gegen die gebaute `Noelia.Infrastructure.dll` (Kommentar an #59). Endpoint mit
`[RequirePermission("orders:delete")]`, Principal mit zwei Identitäten:

| Fall | Identitäten | `PermissionMiddleware` | `PermissionAuthorizationHandler` |
|---|---|---|---|
| A | angemeldet ohne Recht + **unangemeldet mit `permission`** dahinter | **200** | **Succeeded** |
| B | unangemeldet mit `permission` vorn + angemeldet ohne Recht | 401 | nicht erfüllt |
| C | eine Identität, angemeldet, ohne Recht | 403 | nicht erfüllt |

Ursache, im Code belegt:

- `PermissionMiddleware.cs:64` und `PermissionAuthorizationHandler.cs:46` fragen `User.Identity.IsAuthenticated`. Das
  ist die **erste** Identität.
- `PermissionMiddleware.GetUserPermissions` (Z. 112–123) und `PermissionAuthorizationHandler.GetUserPermissions`
  (Z. 76–92) lesen `FindAll("permission")` und `FindAll(ClaimTypes.Role)` über **alle** Identitäten.
- Die Nutzer-ID kommt aus `FindFirst(ClaimTypes.NameIdentifier)`, ebenfalls über alle.

Dieselbe Fehlerklasse steckt an weiteren Stellen, die Personen betreffen:

| Stelle | Fehler |
|---|---|
| `Security/TokenRevocationMiddleware.cs:46–48` und `:90–92` | entscheidet an der ersten Identität, solange keine Dienst-Identität im Principal liegt (`PersonPartOf` lässt den Principal dann unverändert; mit Dienst-Identität filtert es schon auf angemeldete), und liest `jti`/`sub`/`iat` per `FindFirst` über alle |
| `Security/Authorization/ResourceAuthorizationMiddleware.cs:40–48` und `:65` | dasselbe Muster: Entscheidung an der ersten Identität (ohne Dienst-Identität), Nutzer-ID per `FindFirst` |
| `Security/ResourceOwnerHandler.cs:40` | `IsInRole(Admin)` über alle Identitäten, Nutzer-ID per `FindFirst` über alle |
| `Security/Identity/PrincipalFactory.cs:13` | prüft die erste Identität, liest `sub` über alle |

Das Gefährliche an der dritten Zeile: **Ein Fix allein in der Middleware öffnet einen Weg.** Heute verdeckt die 401 aus
`PermissionMiddleware.cs:64` den Fehler im Widerruf. Würde man dort nur `Identities.Any(i => i.IsAuthenticated)`
einsetzen, käme eine **widerrufene Person durch** (Probe: nächster Schritt läuft, 200), sobald eine Policy das
Personen-Scheme und ein zweites, fehlerhaftes Scheme nennt. Deshalb ist der Fix ein gemeinsamer Baustein, nicht eine
Zeile.

## Entscheidungen

1. **Ein gemeinsamer interner Baustein.** In `Noelia.Infrastructure.Security` entsteht ein interner Helfer
   (Arbeitsname `AuthenticatedIdentities`) mit zwei Aufgaben:
   - `IsAuthenticated(principal)`: wahr, wenn **irgendeine** Identität angemeldet ist.
   - `Of(principal)`: ein `ClaimsPrincipal`, der **nur** die angemeldeten Identitäten enthält, in ihrer Reihenfolge.
   Er ist `internal`; es entsteht keine neue öffentliche API. Er liegt neben `ServiceCallerIdentity`, das dieselbe Art von
   Auswahl für das Dienst-Scheme trifft.

2. **Rechte, Rollen, Wildcards und Nutzer-ID nur aus `Of(principal)`.** `PermissionMiddleware` und
   `PermissionAuthorizationHandler` fragen zuerst `IsAuthenticated` (keine angemeldete Identität: Middleware 401,
   Handler nicht erfüllt) und lesen danach ausschließlich aus dem gefilterten Principal: `permission`, Rollen über den
   Katalog, `*`, `kategorie:*` und die Nutzer-ID für Log und Alarm. Reihenfolge der Identitäten spielt keine Rolle mehr.
   Damit ergeben Fall A und Fall B beide **403** (eine angemeldete Person ohne Recht ist da), und nur „gar keine angemeldete
   Identität“ ergibt 401. Ein Recht, das nur eine nicht angemeldete Identität trägt, zählt nicht.

3. **Widerruf, Prüfung je Datensatz, Besitzerprüfung und Principal lesen dasselbe.** `ServiceCallerIdentity.PersonPartOf`
   filtert künftig **immer** auf angemeldete Identitäten (bisher gab es ohne Dienst-Identität den Principal unverändert
   zurück) und nimmt wie bisher die Dienst-Identität heraus. `TokenRevocationMiddleware`,
   `ResourceAuthorizationMiddleware`, `ResourceOwnerHandler` und `PrincipalFactory` lesen Entscheidung und Claims aus
   diesem Ergebnis. Folge: Steht eine nicht angemeldete Identität vorn, wird die dahinter angemeldete Person trotzdem
   geprüft und, wenn widerrufen, mit 401 abgelehnt. Ist **keine** Person angemeldet, lassen Widerruf und Prüfung je
   Datensatz wie bisher durch; die Ablehnung kommt von der Anmelde- und Rechteprüfung. Das ist nötig, damit Entscheidung 2
   den Widerruf nicht freilegt.

4. **Dienst-Tokens ändern sich nicht.** Die Identität des Dienst-Schemes ist angemeldet. Ein `permission`-Claim darin zählte
   vorher und zählt nachher. Noelias Aussteller schreibt nur `client_id` und `jti` hinein (ADR-0002, ADR-0003), also
   verleiht ein Dienst-Token von sich aus kein Recht. Das ist keine Änderung, wird hier aber festgehalten, damit niemand sie
   aus Versehen als Nebenwirkung dieses Fixes liest.

5. **Der Fix ist gewollt ein Bruch, kein Patch.** Konsumenten mit `IClaimsTransformation`, die eine **zusätzliche** Identität
   ohne `AuthenticationType` anhängen, verlieren ihre Rechte (200 → 403). Das ist die geschlossene Richtung, und der Owner hat
   sie ausdrücklich gewählt. Der Weg für sie steht in `MIGRATION.md` (Entscheidung 7).

6. **Was dieses Gate nicht anfasst.** Das Gate ändert nur `src/Noelia.Infrastructure/{Middleware,Authorization,Security,Builder}`,
   Tests, `README.md`, `MIGRATION.md` und `docs/befunde/`. Dieselbe Fehlerklasse steht noch an Stellen außerhalb dieses
   Scopes. Sie ändern weder Rechte noch Rollen einer Anfrage und werden **nicht** hier gefixt, sondern als Issue geführt:
   `Extensions/ClaimsPrincipalExtensions.cs` (öffentliche Helfer `IsAdmin()` und andere, `IsInRole` über alle Identitäten, in der
   Bibliothek selbst nicht aufgerufen), `Noelia.Http/.../DistributedRateLimitingMiddleware.cs:114` (Schlüsselwahl für das
   Limit) sowie die reinen Log-Stellen `LoggingBehavior.cs:244` und `MiddlewarePipelineModule.cs:87`. Ob die öffentlichen
   Helfer dazugehören, ist eine eigene Entscheidung, weil sie öffentliche Signaturen berühren.

7. **Migrationsweg (Text für `MIGRATION.md`).** Wer in `IClaimsTransformation` Claims ergänzt, hängt sie an die
   **angemeldete** Identität des Principals, nicht an eine zusätzliche:

   ```csharp
   if (principal.Identity is ClaimsIdentity authenticated
       && !authenticated.HasClaim("permission", "orders:read"))   // die Transformation kann mehrfach laufen
   {
       authenticated.AddClaim(new Claim("permission", "orders:read"));
   }
   ```

   Nicht: der angehängten Identität selbst einen `AuthenticationType` geben, damit sie „zählt“. Das hieße, eine Identität als
   angemeldet auszugeben, die kein Scheme geprüft hat, also genau den Zustand, den dieser Fix ausschließt. Wer die
   Rechte aus einer Datenbank lädt, setzt sie auf die angemeldete Identität oder stellt sie im Token aus.

## Verworfen

- **Nur `Identities.Any(i => i.IsAuthenticated)` in `PermissionMiddleware`.** Verworfen im Design-Review der Vorrunde: lässt
  Fall A offen (Rechte aus allen Identitäten), lässt den Handler unberührt und legt den Widerruf frei (Entscheidung 3).
- **Nur dokumentieren, dass nicht angemeldete Identitäten zählen.** Gegen den Owner-Entscheid („Die Lücke wird geschlossen,
  nicht nur dokumentiert“).
- **Als Patch 7.1.2 ausliefern.** Gegen den Owner-Entscheid; der Fix bricht Konsumenten, also Minor 7.2.0.
- **Eine Option „unangemeldete Identitäten zulassen“.** Würde die Lücke als Schalter erhalten. Wer Claims braucht, hängt sie
  an die angemeldete Identität (Entscheidung 7).

## Prüfung (Plan für Q-0070)

Rot zuerst, dann grün, je Zeile mit Mutationsprobe (Körper des Fixes entfernen, Test muss rot werden):

- **Matrix** {`permission`, Rolle über Katalog, `*`, `kategorie:*`} × {unangemeldete Identität vorn, hinten} mit angemeldeter
  Person ohne Recht: **403**, in `PermissionMiddleware` **und** `PermissionAuthorizationHandler`. Nur nicht angemeldete
  Identitäten: **401** (Handler: nicht erfüllt).
- Positiv: angemeldete Identität trägt das Recht, unangemeldete Identität daneben stört nicht: 200.
- **Widerrufene Person** mit nicht angemeldeter Identität vorn, durch die ganze Kette: 401, kein Durchkommen.
- **Prüfung je Datensatz** und **Besitzerprüfung** mit nicht angemeldeter Identität vorn und hinten.
- **`IClaimsTransformation`:** angehängte Identität ohne `AuthenticationType` zählt nicht (403); dieselben Claims an der
  angemeldeten Identität zählen (200). Beides als Test, damit der Migrationsweg belegt ist.
- **Wächter über die ganze Kette:** auch mit `Without(PermissionEnforcement)`, also auf dem Weg über den Handler allein.
- Dienst-Token: `permission` in der Dienst-Identität zählt unverändert (Entscheidung 4).

## Folgen

- `MIGRATION.md` bekommt einen Abschnitt „7.2.0“ mit dem Bruch und dem Weg aus Entscheidung 7. Das README nennt die Regel an der
  Stelle, die Rechte und Rollen erklärt.
- Unter `docs/befunde/` entsteht ein Befund: was die Prüfung tat, was sie versprach, Noelia-only-Reproduktion, Status.
- Die Versionsnummer bleibt in diesem Gate bei 7.1.1; den Sprung auf 7.2.0 und die Veröffentlichung macht der Owner im Release.
- Issue für die Stellen aus Entscheidung 6.
