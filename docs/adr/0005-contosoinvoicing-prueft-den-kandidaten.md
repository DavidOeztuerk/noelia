# ADR-0005 — Der ContosoInvoicing-Schritt prüft den Release-Kandidaten

**Status:** Angenommen für `develop` (Fassung 2, Warden-Freigabe nach Delta-Runde) · **Datum:** 2026-11-20 · **Gate:** G-0017 (Rank A, Prüfregel des Release-Gates) · **Quest:** Q-0067 · **Issue:** #54 · **Ziel:** nur Skript und Skill, keine Bibliothek

## In einfacher Sprache

Das Release-Gate soll ContosoInvoicing gegen den Kandidaten laufen lassen, weil dort
Dinge auffallen, die die Demo nie tut. Der Schritt im Skill tut das nicht: Wörtlich
ausgeführt baut und startet er Noelia **6.4.0**, und `noelia analyze .` ruft ein global
installiertes Tool auf, das heute still 7.1.1 ist und bei einem 7.1.1-Kandidaten richtig aussähe.

Der Fix ist ein Skript statt vier Zeilen Text: `demo/eng/contoso-candidate-check.sh`.
Es arbeitet auf einer **Wegwerf-Kopie** von ContosoInvoicing, nimmt eine **eindeutige
Kandidatennummer** und einen **eigenen Paketordner für alles**, und es **belegt am Bau selbst**
(Commit-Hash, nicht nur an der Nummer), dass es den Kandidaten geprüft hat. Stimmt etwas nicht,
bricht es ab, statt grün zu melden.

Was sich für Nutzer ändert: nichts am Produkt. Das Release-Gate hat einen Schritt, der wirklich prüft.

## Entscheidung

**Eingabe.** `contoso-candidate-check.sh --contoso <pfad> --noelia <checkout> --version <v> --feed <dir>`.
Kein Default für den Pfad zu ContosoInvoicing (im Worktree zeigt ein relativer Default ins Leere); fehlt er, bricht das Skript
laut ab und nennt Pfad und Git-sha von ContosoInvoicing. Die Version wird gegen einen Regex geprüft, Pfade gegen
XML-unsichere Zeichen (sie gehen in `sed` und XML), Muster aus `demo/eng/test-package-version.sh`.

**Eindeutige Nummer (M3).** Der Kandidat wird mit `<VersionPrefix>-gate.<stamp>` in einen eigenen Feed gepackt
(Muster `demo/eng/release-gate.mjs`), nie unter der echten Nummer. Zusätzlich vergleicht das Skript den
Commit im `nuspec` des wiederhergestellten Pakets (`repository@commit`) und das `+sha` aus `noelia --version`
mit `git -C <checkout> rev-parse HEAD` und bricht bei Abweichung ab; ein dirty Tree wird ausgewiesen.
Das `+sha` wird vor dem Versionsvergleich abgetrennt.

**Eine Isolierung für alle Kommandos (M2).** `NUGET_PACKAGES=<temp>/pkgs` wird für das ganze Skript exportiert.
Restore läuft mit `--configfile <temp>/NuGet.Config` (außerhalb der Kopie, `<clear />`, Kandidatenfeed, nuget.org,
`packageSourceMapping` `Noelia.*` nur auf den Kandidatenfeed); `nuget.config` und `NuGet.Local.Config` in der Kopie werden gelöscht.
Gebaut wird mit `--no-restore -warnaserror`; gestartet wird die gebaute DLL direkt (`dotnet bin/Release/…/ContosoInvoicing.dll`),
nicht `dotnet run`. Als Gate-Bedingung prüft das Skript am Ende, dass im globalen `~/.nuget/packages` kein neues
`noelia.*/<v>` entstanden ist.

**Das Tool des Kandidaten (M1).** `dotnet dnx Noelia.Cli --version <v> --source <feed>` (nicht das nackte `dnx` im PATH),
mit demselben `NUGET_PACKAGES`. Das Skript prüft, dass `<temp>/pkgs/noelia.cli/<v>/` existiert, und vergleicht das `+sha`
mit dem Checkout. Das Verhalten ohne TTY (keine Rückfrage, die hängt) wird im Rot-Test mit Frist belegt.

**Der Nachweis.** `obj/project.assets.json` muss für jede `Noelia.*`-Bibliothek genau die Kandidatenversion nennen, sonst Exit ≠ 0.

**Start.** `Operator__Secret` ist ein Wegwerf-Wert (mindestens 128 Bit Zufall, nicht leer, nie das Secret aus der README,
nie im Klartext ausgegeben, kein `set -x`, in aufbewahrten Logs geschwärzt). `ASPNETCORE_ENVIRONMENT=Production`, freier Port
(nicht 5000: dort lauscht unter macOS AirPlay). Das Skript wartet auf `/health` mit Frist (60 s) und bricht früh ab, wenn der
Prozess endet (der Fall Exit 134), mit dem Log-Ende. Nach dem Health-OK prüft es, dass die eigene PID lebt und dass `report.json`
`service == "invoicing"` und `environment == "Production"` meldet (Port-Race, fremder Prozess).

**`noelia analyze`.** Einmal gegen die Kopie (`analyze <dir>`), einmal live (`analyze --url … --operator-secret …`).
Exit 2 (Tool-Fehler, z. B. unbekanntes Report-Schema) ist FAIL; Exit 1 (Befunde) wird ausgegeben, nicht als Abbruch behandelt.

**Aufräumen.** `trap` auf `EXIT INT TERM`: zuerst den Dienst beenden (TERM, Frist, KILL, `wait`), dann das Temp-Verzeichnis löschen, außer mit `--keep`.
Die Kopie schließt `.git`, `.env*`, `bin`, `obj`, `logs` aus.

**Der Skill** ruft nur das Skript auf und erklärt, was es belegt und wie man ein Ergebnis liest. Die README von ContosoInvoicing
nennt `:5310`, tatsächlich lauscht der Dienst auf `:5000`; das ist dort zu beheben, im Bericht vermerkt, nicht hier geändert.

## Verworfen

- **Das Pin in ContosoInvoicing selbst anheben:** Das Original bleibt ein Fremder, der Noelia von nuget.org nimmt.
- **Nur den Skill-Text verbessern:** Das war der Zustand, der den Schritt unwirksam gelassen hat.
- **Das Skript auf `rm -rf ~/.nuget/packages/noelia.*` stützen:** Es soll davon unabhängig sein (#63).

## Prüfung

Rot zuerst: ein Feed mit einem anderen Bau unter der Kandidatennummer scheitert am Commit-Vergleich; ein falsches
Pin scheitert an der Assets-Prüfung; ein Dienst ohne Secret bricht früh mit Log-Ende ab; ein leeres Secret wird abgelehnt;
nach dem Lauf entsteht kein `noelia.*/<v>` im globalen Ordner (auch nicht nach `build`, `run`, `dnx`). Die Pin-Umschreibung und die
erzeugte Config werden ohne Netz geprüft. Ein vollständiger Lauf gegen einen echten Kandidaten ist der Beleg des nächsten Releases.
Aufnehmen in `release-gate.mjs` als eigener Schritt wäre die konsequente Folge und wird als Folge-Issue geführt.
