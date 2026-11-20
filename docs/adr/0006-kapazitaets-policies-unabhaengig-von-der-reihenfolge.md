# ADR-0006 — Kapazitäts-Policies lesen die Person unabhängig von der Reihenfolge

**Status:** Angenommen für `develop` (Fassung 2, Warden-Freigabe nach zwei Delta-Runden) · **Datum:** 2026-11-20 · **Gate:** G-0017 (Rank A, Rechte) · **Quest:** Q-0062 · **Issue:** #58 · **Ziel:** Patch, **keine** API-Änderung (nur eine neue Überladung)

## In einfacher Sprache

Zwei Policies fragen, in welcher Rolle jemand handelt: `ActingForCompany` (für eine Firma)
und `ActingAsSelf` (für sich selbst). Das README zeigt sie hinter der Kette `.UseAuth().UsePrincipal()`.

Das Problem: `UseAuth()` enthält Anmeldung **und** Rechteprüfung. Die Policies werden also geprüft,
bevor der Schritt `UsePrincipal()` gelaufen ist. Sie lesen aber, wen dieser Schritt gefunden hat. Zu diesem
Zeitpunkt ist dort nichts, und sie lehnen **jede** Person ab (403), auch eine gültige. Gemessen in G-0016: dieselbe
Person bekam an `RequireAuthorization()` 200 und an `RequireAuthorization(ActingAsSelf)` 403. WorkerTransfer umgeht es schon.
Die Standardkette enthält gar kein `UsePrincipal()`; dort scheitern die Policies immer.

Der Fix: Die Policies sehen sich selbst an, wer angemeldet ist, und übersetzen das genau so, wie es
`UsePrincipal()` tut. Dann ist die Reihenfolge egal.

Was sich für Nutzer ändert:
- Wer nach dem README aufgebaut hat, bekommt für gültige Personen 200 statt 403. Wer es umgangen hat, merkt nichts.
- An einer Dienst-Tür mit einer Person neben dem Dienst und `ActingAsSelf` kommt die Person künftig durch (200 statt 404).
  Das ist gewollt (ADR-0003, Entscheidung 6); die Person wird wie überall geprüft, widerrufen heißt 401.
- Ein ersetztes oder gefälschtes `ICurrentPrincipal` (etwa in Integrationstests von Konsumenten) erfüllt die beiden Policies nicht
  mehr; die Claims gehören in den User. Das scheitert geschlossen.
- Ein Dienst-Token ist nach wie vor keine Person und bekommt hier keine Rolle (ADR-0003).
- Ein widerrufenes Token bekommt an diesen Policies jetzt 401 statt 403: Die Policy erlaubt, die Widerrufsprüfung
  lehnt danach ab. Der Endpoint läuft nie.

## Frage

Woher lesen `ActingForCompanyHandler` und `ActingAsSelfHandler` die handelnde Person, wenn
`ICurrentPrincipal.Current` beim Prüfen noch nicht gesetzt sein kann?

## Entscheidung

1. Beide Handler übersetzen **immer** `context.User` mit
   `IPrincipalFactory.Create(ServiceCallerIdentity.PersonPartOf(context.User))`, exakt die Übersetzung aus
   `PrincipalMiddleware`, damit eine Dienst-Identität nie als Person durchgeht (ADR-0003). Sie bewerten damit den
   Benutzer, den ASP.NET der Policy übergibt (auch bei einem imperativen `AuthorizeAsync(otherUser, policy)`), und nie einen
   Principal aus einem anderen Schritt. `ICurrentPrincipal` wird in den Handlern nicht mehr gelesen.
2. Nur ein erfolgreich übersetzter Principal mit passender `Capacity` lässt die Anforderung gelingen. Anonym oder ungültig
   (Personen-Token ohne `sub`, unbrauchbarer Tenant) führt nicht zu `Succeed`; die Handler rufen nie `Fail`. Hinter
   `UseAuth()` heißt das 403, nicht die 401 der `PrincipalMiddleware`; die Aussage im README gilt nur für Endpoints, die die
   Policy durchgelassen hat.
3. Die Handler schreiben nichts nach `HttpContext.Items`: Das bleibt Aufgabe von `UsePrincipal()`.
4. **Keine API-Änderung.** Der Konstruktor `(ICurrentPrincipal)` bleibt mit seiner Signatur und reicht an `new PrincipalFactory()`
   weiter (in der XML-Doku vermerkt; das Argument wird nicht mehr gelesen). Neu ist die Überladung
   `(ICurrentPrincipal, IPrincipalFactory)`; MS.DI nimmt die längere auflösbare Überladung, weil `IPrincipalFactory` registriert
   ist. Das `ICurrentPrincipal` der Überladung wird nicht gelesen; es steht nur da, weil zwei Ein-Parameter-Konstruktoren
   `(ICurrentPrincipal)` und `(IPrincipalFactory)` für MS.DI mehrdeutig wären (XML-Doku sagt es). Kein `[Obsolete]` in einem Patch.
   Der Konstruktor `(ICurrentPrincipal)` bleibt binär- und quellkompatibel, **liest sein Argument aber nicht mehr**. Das ist eine
   Verhaltensänderung: Ein ersetztes oder gefälschtes `ICurrentPrincipal` erfüllt `ActingAsSelf` und `ActingForCompany` nicht
   mehr; Tests tragen `sub` und Tenant als Claims im User. Es scheitert geschlossen und steht in `MIGRATION.md`. Die beiden
   Erfolgs-Tests in `PrincipalPipelineTests` rufen weiter den Ein-Parameter-Konstruktor auf (das belegt die Kompatibilität),
   legen die Claims aber in `context.User` ab; die drei Ablehnungs-Tests bleiben unverändert.
5. Die Doku nennt eine Formulierung: `UsePrincipal()` setzt den Principal für nachfolgende Schritte und Endpoints;
   die Kapazitäts-Policies brauchen ihn nicht mehr. Die Kommentare in `PrincipalMiddleware`, `IdentityExtensions` und
   `PrincipalModule` und der README-Abschnitt werden angeglichen.

## Grenzen

- Die Widerrufsprüfung läuft in keiner dokumentierten Kette vor der Policy. Das neue `Succeed` für ein widerrufenes Token ist
  deshalb nur durch die nachfolgende Widerrufsprüfung (401) gedeckt, nicht durch die Policy.
- Schon heute kann das optionale Modul `HttpResponseCaching` vor einem nachgehängten Widerruf mit 304 antworten
  (`ResponseCachingHeaderMiddleware`, Schlüssel je `sub`). Das gilt für jedes `RequireAuthorization()` und künftig auch für die
  Kapazitäts-Endpoints. Es ist ein eigenes Issue, nicht Teil dieser Quest.
- Der Mandantenfilter (`TenantFilterExtensions`) liest `ICurrentPrincipal`; in einem eigenen Authorization-Handler fällt er auf
  `TenantId.None` zurück. Das scheitert geschlossen und ist ein Satz im README.

## Verworfen

- **Die Policies aus `UseAuth()` herauslösen oder `UsePrincipal()` davor setzen:** Der Builder fasst die Schritte absichtlich zusammen.
- **Nur die Doku ändern:** Mit dem Builder gibt es keine Stelle dazwischen.
- **„Erst `Current`, sonst Factory“:** `Current` kann aus einem anderen Principal stammen als dem, den die Policy bewertet.
- **Konstruktor ändern oder `[Obsolete]`:** binärer Bruch in einem Patch; ein optionaler Parameter ist es auch.

## Prüfung

Über die README-Kette `.UseAuth().UsePrincipal()` mit echtem HTTP (Harness aus `ServiceCallerFullChainTests`), getrennt wie in ADR-0003:

**Rot zuerst** (vor dem Fix falsch):
- gültige Person an `ActingAsSelf` und `ActingForCompany` je nach Capacity 200;
- `.UseAuth().UseTokenRevocation().UsePrincipal()`: widerrufene Person an beiden Policies 401, der Endpoint läuft nicht;
- Tür mit Person neben dem Dienst: Person mit `sub` und `AsSelf` 200; widerrufene Person 401.

**Wächter** (schon vorher grün, dürfen nie rot werden):
- die andere Capacity 403; kein Token 401; Personen-Token ohne `sub` und mit unbrauchbarem Tenant 403;
- Dienst-Token **mit `sub`** an der Dienst-Tür mit Policy 404; dasselbe Token an einem Endpoint mit `NoeliaService` plus
  `ActingAsSelf` 403; ein normales Dienst-Token an einem normalen Kapazitäts-Endpoint 401 (Kontrolle);
- Person ohne `sub` an der Tür 404;
- **Mutationsprobe:** Ein Handler ohne `PersonPartOf` macht die ersten beiden Dienst-Wächter rot;
- die drei Ablehnungs-Tests in `PrincipalPipelineTests` bleiben unverändert; die zwei Erfolgs-Tests tragen die Claims im User.
