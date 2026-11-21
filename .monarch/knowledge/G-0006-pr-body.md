## G-0006: Sitzung, Widerruf und Schlüssel-Defaults

Gate G-0006 (Rank A). Design zuerst nach Standardregel 3: Dieser Stand enthält nur das Design (ADR-0008) zur Warden-Runde. Die Umsetzung folgt erst nach dem Approve, in diesem PR.

### Das Design in einfacher Sprache

- **Q-0016 (#35), Fehler NO-1:** Nach dem Abmelden konnte jemand mit einem gerade ersetzten Refresh-Token in den folgenden 15 Sekunden noch ein neues, gültiges Token holen. Die Sitzung lebte weiter. Künftig gilt eine Sitzung als beendet, wenn sie kein offenes Token mehr hat. Das 15-Sekunden-Fenster für zwei Tabs gibt nur noch in offenen Sitzungen ein neues Token aus und prüft das nach dem Schreiben noch einmal, damit ein Abmelden im selben Augenblick gewinnt. Die Datenbanktabelle der Anwendungen ändert sich nicht.
- **Q-0071 (neu, statt Q-0026 in 7.2.0):** Ein Dienst, der still ein geteiltes Geheimnis als Schlüssel nimmt, schreibt beim Start eine Warnung. Sonst ändert sich nichts. In 8.0 startet er so nicht mehr (G-0019, Q-0026). Owner-Entscheid vom 21.11.
- **Q-0017 (#36):** Die Demo zeigt echten Widerruf. Abmelden beendet alle Sitzungen der Person und sperrt sofort ihre Access-Tokens. Jeder Dienst der Demo prüft das. Owner-Entscheide vom 27.10. und 21.11.
- **Q-0018 (#37):** Tests für den Authority-Pfad (fremde Schlüssel und Aussteller werden abgelehnt), für den Sitzungs-Widerruf über die echte Kette und für das Abmelden ohne neues Token im Fenster.

Keine neue öffentliche API. Ziel ist Minor 7.2.0. Dieses Gate veröffentlicht nichts.

### Verschoben

- Q-0026 (#44) geht nach G-0019 (Noelia 8.0), Owner-Entscheid vom 21.11.

Design: `docs/adr/0008-sitzungsende-und-widerruf-je-person.md`
