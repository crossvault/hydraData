# Schnellstart

Diese Anleitung fuehrt einen minimalen HydraData-Step aus, ohne eine Datenbank
zu verwenden.

## Voraussetzungen

- .NET 10 SDK
- Ein Checkout des HydraData-Repositories

## Build

```bash
dotnet restore hydradata.slnx
dotnet build hydradata.slnx
dotnet test hydradata.slnx --filter 'FullyQualifiedName!~IntegrationTests'
```

## Minimalen Run-Ordner anlegen

Aus dem Repository-Root:

```bash
mkdir scripts input output
```

Erstelle `scripts/01_10_hello.cs`:

```csharp
// @name: Hello HydraData
// @description: Minimal smoke test
// @haltOnError: true
// @unsafe: false

Note("HydraData executed the hello step.");
Print("Hello from HydraData.");

return Ok("Hello step completed.");
```

Erstelle `appsettings.json`:

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

Erstelle `connections.xml`:

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

Der Hello-Step nutzt die Verbindung nicht, der Host prueft aber, dass mindestens
eine Verbindung vorhanden ist. Wenn Scripts keine Verbindung auswaehlen, nutzt
HydraData den ersten deklarierten Eintrag; `default` als Name ist eine
empfohlene Konvention.

## Ausfuehren

```bash
dotnet run --project src/HydraData.Host/HydraData.Host.csproj
```

Erwartetes Ergebnis:

- der Prozess endet mit Exit-Code `0`
- die Konsole enthaelt `Hello from HydraData.`
- Run-Artefakte liegen unter `_runs/`

## Danach

- Reale Datenbankverbindungen: [Konfiguration](configuration.de.md)
- Steps schreiben: [Step-Skripte](scripts.de.md)
- HydraData einbetten: [Embedded API](embedded-api.de.md)
- Geplante Laeufe betreiben: [Betrieb](operations.de.md)
