# Embedded API

Nutze `HydraData.Engine`, wenn die Pumpe in einem anderen .NET-Prozess laufen
soll. Dieselbe Engine validiert Scripts vor der Ausfuehrung und liefert einen
`RunReport`.

## Grundablauf

```csharp
using HydraData.Engine;

var folders = new PumpFolderPolicy(
    ReadAllowlist: ["/data/input"],
    WriteAllowlist: ["/data/output"]);

var options = new PumpOptions(
    WorkspaceBase: "/var/hydradata/runs",
    Folders: folders);

IPumpEngine engine = new PumpEngine(options);

var scripts = new DiscoveryService().Discover(["/app/scripts"]);
var registry = ConnectionRegistry.Load("/app/connections.xml");
var connections = new ConnectionDirectory(registry);

var externCtx = ExternContext.FromValues(new Dictionary<string, object?>
{
    ["runDate"] = DateTimeOffset.UtcNow,
    ["dryRun"] = false
});

var validation = engine.Validate(scripts, externCtx, connections);
if (!validation.IsValid)
{
    foreach (var diagnostic in validation.Diagnostics)
    {
        Console.Error.WriteLine(
            $"{diagnostic.Code}: {diagnostic.Message}");
    }

    return 1;
}

var progress = new Progress<PumpProgress>(p =>
    Console.WriteLine($"{p.Phase}: {p.ScriptName}"));

var report = await engine.ExecuteAsync(
    scripts,
    externCtx,
    connections,
    progress,
    CancellationToken.None);

return report.ExitCode;
```

## Erst validieren

Rufe beim Einbetten `Validate` vor `ExecuteAsync` auf. Die Validierung
kompiliert und prueft Scripts, ohne sie auszufuehren. Eine fehlgeschlagene
Validierung entspricht Exit-Code `1`.

## Progress und Cancellation

Uebergib `IProgress<PumpProgress>`, um Lifecycle-Events an deine Anwendung zu
melden. Uebergib einen `CancellationToken` fuer Shutdown. Caller-Cancellation
wird als `OperationCanceledException` geworfen; fuer diesen Pfad gibt es keinen
`RunReport`.

## RunReport

`RunReport` enthaelt:

- `RunId`
- `ExitCode`
- Preflight-Diagnostics
- Ergebnisse pro Step

Exit-Code `0` bedeutet Erfolg, `1` bedeutet Validierungs- oder Setup-Fehler,
und `2` bedeutet Runtime-Step-Fehler oder Timeout.

## Verwandte Seiten

- [Schnellstart](quickstart.de.md)
- [Konfiguration](configuration.de.md)
- [Step-Skripte](scripts.de.md)
- [Betrieb](operations.de.md)
