# Operations

HydraData.Host is the console runner for scheduled jobs, CI, and manual runs.
It reads configuration from the current working directory.

## Batch Run

During development:

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj
```

After publishing or building the host, run the executable from the folder that
contains `appsettings.json`, `connections.xml`, and your script folders.
The examples below use `dotnet run` from the repository checkout; a published
executable or shell alias can use a shorter name such as `hydradata`.

## Resume

Resume validates all scripts and then executes from the requested order:

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj -- resume 02_10
dotnet run --project src/HydraData.Host/HydraData.Host.csproj -- resume 02_10_01
```

Use this after fixing a failed later step when earlier work should not be
repeated.

## Session Mode

Session mode keeps state in one process while you run and re-run individual
steps:

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj -- session
hydradata> 01_10
hydradata> 01_20
hydradata> :quit
```

This is useful while editing scripts interactively.

## Exit Codes

| Code | Meaning |
| --- | --- |
| `0` | Run completed without errors. |
| `1` | Configuration, connection, or validation failure before step execution. |
| `2` | Runtime step failure, timeout, or cancelled batch run. |

Caller cancellation in embedded usage throws `OperationCanceledException`
instead of returning a `RunReport`.

## Run Artifacts

Each run writes artifacts below `WorkspaceBase`. Keep this folder outside the
repository for production jobs. `RunDirRetentionDays` controls cleanup of old
run folders.

## Troubleshooting

- Missing `appsettings.json`: start the host from the configured working folder.
- Missing default connection: ensure `connections.xml` contains at least one connection; the first declared connection is the default. `name="default"` is a recommended convention.
- IO denied: put the path under `ReadAllowlist` or `WriteAllowlist`.
- Step timeout: increase `StepTimeoutSeconds` or add cancellation checks in long loops.
- Validation failure: fix compile errors before expecting any step to run.

## Related Pages

- [Quickstart](quickstart.en.md)
- [Configuration](configuration.en.md)
- [Step scripts](scripts.en.md)
- [Embedded API](embedded-api.en.md)
