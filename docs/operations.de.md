# Betrieb

HydraData.Host ist der Console Runner fuer geplante Jobs, CI und manuelle
Laeufe. Er liest die Konfiguration aus dem aktuellen Arbeitsverzeichnis.

## Batch-Lauf

In der Entwicklung:

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj
```

Nach Publish oder Build wird das Executable aus dem Ordner gestartet, der
`appsettings.json`, `connections.xml` und die Script-Ordner enthaelt.
Die Beispiele unten nutzen `dotnet run` aus dem Repository-Checkout; ein
publiziertes Executable oder Shell-Alias kann einen kuerzeren Namen wie
`hydradata` verwenden.

## Resume

Resume validiert alle Scripts und fuehrt dann ab der gewuenschten Order aus:

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj -- resume 02_10
dotnet run --project src/HydraData.Host/HydraData.Host.csproj -- resume 02_10_01
```

Das ist nuetzlich, wenn ein spaeter Step korrigiert wurde und fruehere Arbeit
nicht erneut laufen soll.

## Session Mode

Session Mode haelt State in einem Prozess, waehrend einzelne Steps ausgefuehrt
und erneut ausgefuehrt werden:

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj -- session
hydradata> 01_10
hydradata> 01_20
hydradata> :quit
```

Das hilft beim interaktiven Bearbeiten von Scripts.

## Exit-Codes

| Code | Bedeutung |
| --- | --- |
| `0` | Lauf ohne Fehler abgeschlossen. |
| `1` | Konfigurations-, Verbindungs- oder Validierungsfehler vor Step-Ausfuehrung. |
| `2` | Runtime-Step-Fehler, Timeout oder abgebrochener Batch-Lauf. |

Caller-Cancellation bei eingebetteter Nutzung wirft `OperationCanceledException`,
statt einen `RunReport` zu liefern.

## Run-Artefakte

Jeder Lauf schreibt Artefakte unter `WorkspaceBase`. Fuer produktive Jobs sollte
dieser Ordner ausserhalb des Repositories liegen. `RunDirRetentionDays` steuert
das Aufraeumen alter Run-Ordner.

## Troubleshooting

- `appsettings.json` fehlt: Host aus dem konfigurierten Arbeitsordner starten.
- Default-Verbindung fehlt: `connections.xml` muss mindestens eine Verbindung enthalten; die erste deklarierte Verbindung ist der Default. `name="default"` ist eine empfohlene Konvention.
- IO verweigert: Pfad unter `ReadAllowlist` oder `WriteAllowlist` legen.
- Step-Timeout: `StepTimeoutSeconds` erhoehen oder Cancellation-Checks in lange Schleifen einbauen.
- Validierungsfehler: Compile-Fehler beheben; vorher laeuft kein Step.

## Verwandte Seiten

- [Schnellstart](quickstart.de.md)
- [Konfiguration](configuration.de.md)
- [Step-Skripte](scripts.de.md)
- [Embedded API](embedded-api.de.md)
