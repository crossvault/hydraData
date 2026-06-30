# Quickstart

This guide runs a minimal HydraData step without touching a database.

## Prerequisites

- .NET 10 SDK
- A checkout of the HydraData repository

## Build

```bash
dotnet restore hydradata.slnx
dotnet build hydradata.slnx
dotnet test hydradata.slnx --filter 'FullyQualifiedName!~IntegrationTests'
```

## Create a Minimal Run Folder

From the repository root:

```bash
mkdir scripts input output
```

Create `scripts/01_10_hello.cs`:

```csharp
// @name: Hello HydraData
// @description: Minimal smoke test
// @haltOnError: true
// @unsafe: false

Note("HydraData executed the hello step.");
Print("Hello from HydraData.");

return Ok("Hello step completed.");
```

Create `appsettings.json`:

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

Create `connections.xml`:

```xml
<ConnectionStrings>
  <ConnectionString targetSystem="MSSQL" name="default">
    <Parameters>
      <Parameter key="Server" value="localhost" type="String" />
      <Parameter key="Database" value="stage" type="String" />
      <Parameter key="Trusted_Connection" value="true" type="Boolean" />
      <Parameter key="TrustServerCertificate" value="true" type="Boolean" />
    </Parameters>
  </ConnectionString>
</ConnectionStrings>
```

The hello step does not use the connection, but the host still validates that at
least one connection exists. When scripts do not select a connection, HydraData
uses the first declared entry; naming it `default` is a recommended convention.

## Run

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj
```

Expected result:

- the process exits with code `0`
- console output includes `Hello from HydraData.`
- run artifacts are written below `_runs/`

## Next Steps

- Configure real database connections in [Configuration](configuration.en.md).
- Write production steps with [Step scripts](scripts.en.md).
- Embed HydraData with [Embedded API](embedded-api.en.md).
- Operate scheduled runs with [Operations](operations.en.md).
