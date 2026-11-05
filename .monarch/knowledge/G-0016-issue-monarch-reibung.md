Zwei Reibungen aus einer Noelia-Session (Gate G-0016, Quest Q-0055, 05.11.2026). Beide wurden nicht umgangen.

## 1. sed mit Zeilenadresse gilt als Schreiben in einen geschützten Pfad

Kommando (Ausschnitt): `sed -i '' -e '62s/.*/…/' -e '65s/.*/…/' src/.../ServiceCallerHandler.cs`

Meldung: „Das Skript von 'sed' schreibt in den geschützten Pfad '62s/.*/' (protect_paths: .monarch/ledger/**).“

Das Ziel war eine Quelldatei im Scope der Quest, das Ledger war nicht beteiligt. Der Hook hält das sed-Skript für einen Pfad. Das ist vermutlich dieselbe Familie wie #55: Dort wurde `sed 's/.*/new/' f` wieder erlaubt, die Form mit Zeilenadresse offenbar nicht.

## 2. Während einer aktiven Quest gibt es keinen Weg für einen Issue-Text

Die Standardregel verlangt `gh issue create --body-file <datei>`. Während einer aktiven Quest passt keine Datei:

- Das Scratchpad liegt außerhalb des Scopes (Hinweis beim Quest-Start: „Der Scope gilt für jeden Schreibzugriff, auch Scratchpad“).
- `knowledge_write` legt die Datei unter `.monarch/knowledge/` ab, aber `gh` mit diesem Pfad wird verweigert: „Das Kommando 'gh' ist nicht als lesend bekannt und nennt '.monarch/knowledge/…'“.

Ein Befund mitten in der Quest kann deshalb erst nach `quest submit` ein Issue werden. Das geht, aber der Text muss so lange warten, und im ADR fehlt dann die Nummer.

Mögliche Lösung: `--body-file` bei `gh issue create` als lesenden Zugriff auf `.monarch/knowledge/**` werten, oder einen eigenen MCP-Weg für Issues anbieten.
