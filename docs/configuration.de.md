# Konfiguration

HydraData.Host liest die Konfiguration aus dem aktuellen Arbeitsverzeichnis.
Ein normaler Host-Lauf braucht `appsettings.json`, `connections.xml` und einen
oder mehrere Script-Ordner.

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

| Key | Zweck |
| --- | --- |
| `WorkspaceBase` | Basisordner fuer Run-Verzeichnisse, Logs und Artefakte. |
| `AllowUnsafeDirectAccess` | Erlaubt Scripts mit `@unsafe: true`; fuer normale Laeufe `false` lassen. |
| `ReadAllowlist` | Ordner, aus denen Scripts ueber HydraData-IO lesen duerfen. |
| `WriteAllowlist` | Ordner, in die Scripts ueber HydraData-IO schreiben duerfen. |
| `StepTimeoutSeconds` | Timeout pro Step. Lange Schleifen sollten `Cancellation` beachten. |
| `RunDirRetentionDays` | Anzahl Tage, die Run-Verzeichnisse behalten werden. |
| `LegacyGlobalState` | Migrationsschalter fuer altes State-Verhalten. Fuer neue Projekte `false` lassen. |
| `LegacyGroupBySlug` | Migrationsschalter fuer altes Slug-Gruppierungsverhalten. Fuer neue Projekte `false` lassen. |
| `ScriptFolders` | Ordner, in denen `*.cs`-Step-Scripts gesucht werden. |
| `ConnectionsFile` | Pfad zur `connections.xml`. |

## connections.xml

Vor der Ausfuehrung loest HydraData die Default-Verbindung als erste in
`connections.xml` deklarierte Verbindung auf. Nutze `name="default"` als klare
Konvention, sofern deine Scripts nicht explizit eine benannte Verbindung waehlen.

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

## Allowlist-Regeln

Scripts sollten die HydraData-IO-Helfer statt direktem Dateizugriff verwenden.
Lesezugriffe muessen innerhalb der `ReadAllowlist` bleiben; Schreibzugriffe
innerhalb der `WriteAllowlist`. Fuer geplante Laeufe sind enge Ordner sinnvoll,
zum Beispiel ein Input-Drop und ein Output-Ordner.

## Verwandte Seiten

- [Schnellstart](quickstart.de.md)
- [Step-Skripte](scripts.de.md)
- [Betrieb](operations.de.md)
