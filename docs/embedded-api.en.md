# Embedded API

Use `HydraData.Engine` when the pump should run inside another .NET process.
The same engine validates scripts before execution and returns a `RunReport`.

## Basic Flow

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

## Validation First

Call `Validate` before `ExecuteAsync` when embedding. Validation compiles and
audits scripts without running them. A failed validation maps to exit code `1`.

## Progress and Cancellation

Pass `IProgress<PumpProgress>` to stream lifecycle events to your application.
Pass a `CancellationToken` for shutdown. Caller cancellation is thrown as
`OperationCanceledException`; no `RunReport` is returned for that path.

## RunReport

`RunReport` contains:

- `RunId`
- `ExitCode`
- preflight diagnostics
- per-step results

Exit code `0` means success, `1` means validation or setup failure, and `2`
means a runtime step failure or timeout.

## Related Pages

- [Quickstart](quickstart.en.md)
- [Configuration](configuration.en.md)
- [Step scripts](scripts.en.md)
- [Operations](operations.en.md)
