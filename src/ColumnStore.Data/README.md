# ColumnStore.Data

**A column-oriented, strongly typed, allocation-lean replacement for `System.Data.DataTable`.**
Change the `using`, keep your code, use ~70% less memory.

```csharp
- using System.Data;
+ using ColumnStore.Data;
```

Targets **.NET Framework 4.8**, **.NET 8** and **.NET 10**.

---

## Why

`System.Data.DataTable` stores data row-first: one heap-allocated `DataRow` per row, each holding an
`Object[]` of cell values — so **every value-typed cell is boxed**. On x64 one 4-byte `int` ends up
occupying 32 bytes: 24 for the box, 8 for the array slot that points at it.

ColumnStore.Data keeps the same programming model and stores the data **column-first in typed
arrays**. No per-row object, no `Object[]` per row, no boxing on the typed path.

| 1,000,000 rows × 10 columns | Memory | Build time |
|---|---:|---:|
| `System.Data.DataTable` | 233.5 MB | 1,001.7 ms |
| ColumnStore.Data | **69.2 MB** | **46.4 ms** |
| | **70.4% saved** | **21.6× faster** |

`Boolean` columns cost **0.4 bytes per row** — they are bit-packed, 64 rows to a `UInt64`.

## Quick start

```csharp
using ColumnStore.Data;

DataTable table = new DataTable("Orders");
DataColumn<Int32> quantity = table.Columns.Add<Int32>("Quantity", allowDBNull: false);
table.Columns.Add<String>("Product");
table.Columns.Add<Decimal>("Price");

table.Rows.Add(3, "widget", 19.99m);

foreach (DataRow row in table.Rows)
{
    Console.WriteLine(row["Product"]);
}
table.Rows[0]["Quantity"] = 5;

// The fast path: resolve the column once, outside the loop — no cast, no box, no type dispatch.
Int64 total = 0;
for (Int32 rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
{
    total += quantity.Get(rowIndex);
}
```

### Filling from a reader

`Load` matches `System.Data.DataTable.Load`, including creating the schema when the table has none —
but every cell goes through the reader's typed getter straight into typed storage, so nothing boxes.

```csharp
using (IDataReader reader = command.ExecuteReader())
{
    table.Load(reader);
}
```

`table.LoadRowCount(reader, inferNullability: true)` does the same and returns how many rows were
appended.

## What differs from `System.Data.DataTable`

All deliberate, all covered by tests. Anything unsupported throws `NotImplementedException` naming
the alternative — never a silent no-op.

- **Rows are a `readonly struct`** synthesized on demand, not heap objects. This is the memory saving.
  Every System.Data assignment shape still compiles: `Rows[i]` returns `ref DataRow`.
- **Deletion is logical** — a tombstone plus index translation. Slots are never reused, so
  `Rows.InsertAt` cannot work, and deleting twice is a no-op rather than an exception.
- **`NewRow()` rows appear in the order they were created**, not the order they were added. Identical
  to System.Data whenever nothing else joins the table in between; any `Add` that *would* reorder
  throws rather than reorder silently, and `Rows.AddAtEnd(row)` reproduces System.Data's order.
- **Refused:** constraints and primary keys, `Select(filter)`, `Compute`, `DefaultView`,
  `GetChanges`/`RejectChanges`, XML, relations, and table events.
- Not thread-safe — the same single-writer baseline as `System.Data.DataTable`.

## Related package

**`ColumnStore.Data.SqlServer`** adds SQL Server writes: table-valued parameters and `SqlBulkCopy`,
without boxing a cell. Reading needs nothing extra — `Load` is in this package and takes any
`IDataReader`.

## Dependencies

None on .NET 8 and .NET 10. On .NET Framework 4.8, one: `System.Runtime.CompilerServices.Unsafe`,
for the managed size of a generic type, which .NET Framework exposes nowhere else.
