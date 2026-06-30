# Step Scripts

HydraData steps are C# scripts discovered from configured script folders. Each
step is compiled before execution so setup errors fail before side effects.

## File Names and Order

Use this pattern:

```text
GG_SS[_TT][_[slug]]_description.cs
```

Examples:

- `01_10_extract_customers.cs`
- `01_20_transform_customers.cs`
- `02_10_load_customers.cs`
- `02_10_01_load_customers_part.cs`
- `03_10_[customers]_read_masterdata.cs`

`GG` is the group, `SS` is the step, and optional `TT` is a sub-step. Numbers are sorted segment by segment. A bracketed slug such as `[customers]` is optional metadata in the filename; it is not part of the numeric order.

## Metadata

Place metadata at the top of the script:

```csharp
// @name: Extract customers
// @description: Reads active customers from staging
// @haltOnError: true
// @unsafe: false
```

`@haltOnError` defaults to `true`. `@unsafe: true` only works when the host is
configured to allow unsafe direct access.

## Script Surface

Scripts can call these helpers directly:

- Database: `Query`, `Scalar`, `Execute`, `BulkInsert`
- Files: `ReadExcel`, `StreamExcel`, `ReadCsv`, `ReadCsvFast`, `WriteExcel`, `WriteCsv`, `WriteCsvFast`
- DuckDB: `Analyze`, `Duck`
- Verdicts: `Ok`, `Warn`, `Fail`, `Expect`, `Note`
- State: `State`, `Shared`, `Ctx`
- Output: `Print`, `Log`, `Table`
- Helpers: `iif`, `icase`, `coalesce`, `coalesceBlank`, `isBlank`, `fmt`,
  `parseOr`, `between`, `isIn`, `nullIf`, `trimToNull`, `cleanText`,
  `beforeDelimiter`, `afterDelimiter`, `betweenDelimiters`, and `M.*` aliases

`State` is scoped to the current group. `Shared` is visible across the run.
`Ctx` contains read-only values supplied by the embedding application.

## Cancellation

Long loops must check the provided cancellation token:

```csharp
foreach (var row in rows)
{
    Cancellation.ThrowIfCancellationRequested();
    // process row
}
```

## Example

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

## Related Pages

- [Quickstart](quickstart.en.md)
- [Configuration](configuration.en.md)
- [Embedded API](embedded-api.en.md)
- [Operations](operations.en.md)
