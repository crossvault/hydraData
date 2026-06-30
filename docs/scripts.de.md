# Step-Skripte

HydraData-Steps sind C#-Scripts aus den konfigurierten Script-Ordnern. Jeder
Step wird vor der Ausfuehrung kompiliert, damit Setup-Fehler vor Seiteneffekten
auffallen.

## Dateinamen und Reihenfolge

Nutze dieses Muster:

```text
GG_SS[_TT][_[slug]]_description.cs
```

Beispiele:

- `01_10_extract_customers.cs`
- `01_20_transform_customers.cs`
- `02_10_load_customers.cs`
- `02_10_01_load_customers_part.cs`
- `03_10_[kunden]_read_masterdata.cs`

`GG` ist die Gruppe, `SS` der Step und optional `TT` ein Sub-Step. Sortiert wird segmentweise numerisch. Ein bracketed Slug wie `[kunden]` ist optionale Metadaten im Dateinamen; er gehoert nicht zur numerischen Reihenfolge.

## Metadaten

Metadaten stehen am Anfang des Scripts:

```csharp
// @name: Extract customers
// @description: Reads active customers from staging
// @haltOnError: true
// @unsafe: false
```

`@haltOnError` ist standardmaessig `true`. `@unsafe: true` funktioniert nur,
wenn der Host unsafe direct access erlaubt.

## Script-Oberflaeche

Scripts koennen diese Helfer direkt aufrufen:

- Datenbank: `Query`, `Scalar`, `Execute`, `BulkInsert`
- Dateien: `ReadExcel`, `StreamExcel`, `ReadCsv`, `ReadCsvFast`, `WriteExcel`, `WriteCsv`, `WriteCsvFast`
- DuckDB: `Analyze`, `Duck`
- Ergebnis: `Ok`, `Warn`, `Fail`, `Expect`, `Note`
- State: `State`, `Shared`, `Ctx`
- Ausgabe: `Print`, `Log`, `Table`
- Helfer: `iif`, `icase`, `coalesce`, `coalesceBlank`, `isBlank`, `fmt`,
  `parseOr`, `between`, `isIn`, `nullIf`, `trimToNull`, `cleanText`,
  `beforeDelimiter`, `afterDelimiter`, `betweenDelimiters` und `M.*`-Aliase

`State` gilt fuer die aktuelle Gruppe. `Shared` ist im gesamten Run sichtbar.
`Ctx` enthaelt read-only Werte aus der einbettenden Anwendung.

## Cancellation

Lange Schleifen muessen den Cancellation-Token beachten:

```csharp
foreach (var row in rows)
{
    Cancellation.ThrowIfCancellationRequested();
    // row verarbeiten
}
```

## Beispiel

```csharp
// @name: Customer count check
// @description: Verifies that staging contains active customers
// @haltOnError: true
// @unsafe: false

var count = Scalar<int>(
    "select count(*) from staging_customers where is_active = 1");

Note(fmt("Active customers: {0}", count));

if (count == 0)
{
    return Warn("No active customers found.");
}

State.Set("activeCustomerCount", count);
return Ok("Customer check completed.");
```

## Verwandte Seiten

- [Schnellstart](quickstart.de.md)
- [Konfiguration](configuration.de.md)
- [Embedded API](embedded-api.de.md)
- [Betrieb](operations.de.md)
