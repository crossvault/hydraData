# Configuration

HydraData.Host reads configuration from the current working directory. A normal
host run needs `appsettings.json`, `connections.xml`, and one or more script
folders.

## appsettings.json

```json
{
  "Pump": {
    "WorkspaceBase": "./_runs",
    "AllowUnsafeDirectAccess": false,
    "ReadAllowlist": [ "./input" ],
    "WriteAllowlist": [ "./output" ],
    "StepTimeoutSeconds": 120,
    "RunDirRetentionDays": 14,
    "LegacyGlobalState": false,
    "LegacyGroupBySlug": false,
    "ScriptFolders": [ "./scripts" ],
    "ConnectionsFile": "./connections.xml"
  }
}
```

| Key | Purpose |
| --- | --- |
| `WorkspaceBase` | Base folder for per-run directories, logs, and artifacts. |
| `AllowUnsafeDirectAccess` | Enables scripts marked `@unsafe: true`; keep `false` for normal runs. |
| `ReadAllowlist` | Folders scripts may read through HydraData IO helpers. |
| `WriteAllowlist` | Folders scripts may write through HydraData IO helpers. |
| `StepTimeoutSeconds` | Timeout per step. Long loops should observe `Cancellation`. |
| `RunDirRetentionDays` | Number of days to keep run directories. |
| `LegacyGlobalState` | Migration switch for older state behavior. Keep `false` for new projects. |
| `LegacyGroupBySlug` | Migration switch for older slug grouping behavior. Keep `false` for new projects. |
| `ScriptFolders` | Folders scanned for `*.cs` step scripts. |
| `ConnectionsFile` | Path to `connections.xml`. |

## connections.xml

Before execution, HydraData resolves the default connection as the first
connection declared in `connections.xml`. Use `name="default"` as a clear
convention unless your scripts explicitly choose a named connection.

### SQL Server

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

### PostgreSQL

```xml
<ConnectionStrings>
  <ConnectionString targetSystem="PGSQL" name="default">
    <Parameters>
      <Parameter key="Host" value="localhost" type="String" />
      <Parameter key="Port" value="5432" type="Int32" />
      <Parameter key="Database" value="stage" type="String" />
      <Parameter key="Username" value="postgres" type="String" />
      <Parameter key="Password" value="postgres" type="String" />
    </Parameters>
  </ConnectionString>
</ConnectionStrings>
```

## Allowlist Rules

Scripts should use HydraData IO helpers instead of unrestricted file access.
Reads must stay inside `ReadAllowlist`; writes must stay inside
`WriteAllowlist`. Use narrow folders for scheduled runs, for example one input
drop folder and one output folder.

## Related Pages

- [Quickstart](quickstart.en.md)
- [Step scripts](scripts.en.md)
- [Operations](operations.en.md)
