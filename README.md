# webhook-relay-demo

Ein kleiner Webhook-Relay in .NET 8 für den Fall, dass ein Workflow (z. B. n8n) eingehende
Webhooks nicht direkt annehmen sollte: Der Relay prüft die Signatur, verwirft Duplikate,
leitet weiter mit Retries und hebt endgültig fehlgeschlagene Events zur späteren
Wiederholung auf.

```
Shop / Payment / CRM  ──POST──▶  Relay  ──POST──▶  n8n-Webhook (oder jede andere URL)
                                  │
                                  ├─ HMAC-SHA256-Signatur falsch  → 401, nichts weitergeleitet
                                  ├─ Event-ID schon gesehen       → 200 "duplicate", nichts weitergeleitet
                                  ├─ Ziel antwortet 5xx/408/429    → Retry mit exponentiellem Backoff
                                  ├─ Ziel antwortet sonstiges 4xx  → kein Retry (wird nicht besser)
                                  └─ alle Versuche fehlgeschlagen  → Dead-Letter, später per Replay
```

## Endpunkte

| Methode | Pfad | Zweck |
|---|---|---|
| POST | `/webhooks` | Event annehmen. Header `X-Signature: sha256=<hex>`, Body JSON mit Feld `id` |
| GET | `/dead-letters` | fehlgeschlagene Events (Header `X-Admin-Key`) |
| POST | `/dead-letters/{id}/replay` | Event erneut zustellen (Header `X-Admin-Key`) |
| GET | `/health` | Healthcheck |

Beim Weiterleiten wird der Header `Idempotency-Key: <id>` mitgeschickt, damit auch das Ziel
doppelte Zustellungen erkennen kann.

## Konfiguration

`Relay:Secret`, `Relay:AdminKey`, `Relay:TargetUrl`, `Relay:MaxAttempts` (Standard 4),
`Relay:BaseDelayMs` (Standard 500). Lokale Werte stehen in
`src/Relay/appsettings.Development.json`; in Produktion per Umgebungsvariable, z. B.
`Relay__Secret=...`.

## Starten und ausprobieren

```bash
dotnet test
dotnet run --project src/Relay
```

```bash
BODY='{"id":"evt_100","total":42}'
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "dev-secret-change-me" | sed 's/^.* //')
curl -X POST localhost:5288/webhooks -H "Content-Type: application/json" -H "X-Signature: sha256=$SIG" -d "$BODY"
# {"id":"evt_100","status":"delivered","attempts":1}    (zweiter Aufruf: "duplicate")

# Ziel stoppen, ein neues Event (andere id) schicken → "dead-lettered", dann:
curl localhost:5288/dead-letters -H "X-Admin-Key: dev-admin-key-change-me"
curl -X POST localhost:5288/dead-letters/<id>/replay -H "X-Admin-Key: dev-admin-key-change-me"
```

## Tests

`tests/Relay.Tests` (xUnit + `WebApplicationFactory`, Ziel-URL durch einen Stub ersetzt):
Zustellung mit Idempotency-Key, falsche/fehlende Signatur, fehlende `id`, Duplikat,
Retry nach 503/429 bis Erfolg, kein Retry bei 400, Dead-Letter nach erschöpften Retries
mit Replay, Schutz der Admin-Endpunkte.

## Bewusste Vereinfachungen

- Zustand (gesehene IDs, Dead-Letters) liegt im Speicher und ist nach einem Neustart weg.
  Für den Betrieb: Tabelle mit eindeutiger Event-ID in PostgreSQL o. ä.
- Die Weiterleitung passiert synchron im Request. Bei langen Backoffs: Queue + Hintergrunddienst,
  Annahme sofort mit 202 bestätigen.
