# HydraData

HydraData is an embeddable, script-driven data pump for .NET 10. It discovers ordered C# step scripts, validates them before execution, and runs them against SQL Server or PostgreSQL with scoped state, transactions, controlled IO, and host-friendly exit codes.

HydraData is intended to be suitable for public GitHub use as an OSS project under the MIT license.

## Documentation

- [Documentation index](https://github.com/crossvault/hydradata/blob/main/docs/README.md)
- [Quickstart in English](https://github.com/crossvault/hydradata/blob/main/docs/quickstart.en.md)
- [Schnellstart auf Deutsch](https://github.com/crossvault/hydradata/blob/main/docs/quickstart.de.md)

The root README is intentionally short. Detailed setup, scripting, operations, and embedding guidance belongs under `docs/`.

## Prerequisites

- .NET 10 SDK
- Git
- SQL Server or PostgreSQL for real database steps

The hello trial below does not open a database connection, but the host still
needs a valid `connections.xml` with at least one connection entry. When scripts
do not select a connection, HydraData uses the first declared entry; naming it
`default` is a recommended convention.

## Build and Test

```bash
dotnet restore hydradata.slnx
dotnet build hydradata.slnx
dotnet test hydradata.slnx --filter 'FullyQualifiedName!~IntegrationTests'
```

## Try the Host

`HydraData.Host` reads `appsettings.json`, `connections.xml`, and script folders from the current working directory. From the repository root, create `scripts/`, `input/`, and `output/`, then add these files.

`scripts/01_10_hello.cs`

```csharp
// @name: Hello HydraData
// @description: Minimal smoke test
// @haltOnError: true
// @unsafe: false

Note("HydraData executed the hello step.");
Print("Hello from HydraData.");

return Ok("Hello step completed.");
```

`appsettings.json`

```json
{
  "Pump": {
    "WorkspaceBase": "./_runs",
    "AllowUnsafeDirectAccess": false,
    "ReadAllowlist": [ "./input" ],
    "WriteAllowlist": [ "./output" ],
    "StepTimeoutSeconds": 120,
    "RunDirRetentionDays": 14,
    "ScriptFolders": [ "./scripts" ],
    "ConnectionsFile": "./connections.xml"
  }
}
```

`connections.xml`

```xml
<ConnectionStrings>
  <ConnectionString targetSystem="MSSQL" name="default">
    <Parameters>
      <Parameter key="Server" value="localhost" type="String" />
      <Parameter key="Database" value="stage" type="String" />
    </Parameters>
  </ConnectionString>
</ConnectionStrings>
```

Run the host:

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj
```

A successful run returns exit code `0` and writes run artifacts under `_runs/`. Edit `connections.xml` before using database APIs such as `Query`, `Scalar`, `Execute`, or `BulkInsert`.

## Embedded API

Use `HydraData.Engine` when you want to embed the pump in another .NET process. The main entry point is `IPumpEngine`, implemented by `PumpEngine`.

Typical embedded flow:

```csharp
var options = new PumpOptions(workspaceBase, folders);
var engine = new PumpEngine(options);
var scripts = new DiscoveryService().Discover(["./scripts"]);
var registry = ConnectionRegistry.Load("./connections.xml");
var connections = new ConnectionDirectory(registry);
var externCtx = ExternContext.FromValues(new Dictionary<string, object?>());

var validation = engine.Validate(scripts, externCtx, connections);
if (!validation.IsValid)
    return 1;

var report = await engine.ExecuteAsync(scripts, externCtx, connections, ct: cancellationToken);
return report.ExitCode;
```

See the public docs linked above for the full API surface, script conventions, and operational guidance.

## License

HydraData is licensed under the MIT license. See [LICENSE.txt](https://github.com/crossvault/hydradata/blob/main/LICENSE.txt).
