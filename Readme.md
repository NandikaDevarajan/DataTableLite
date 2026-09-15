# ColumnStore.Data

**A column-oriented, strongly typed, allocation-lean replacement for `System.Data.DataTable`.**
Change the `using`, keep your code, use ~70% less memory.

[![.NET](https://img.shields.io/badge/.NET-Framework%204.8%20%7C%208.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-latest-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![Tests](https://img.shields.io/badge/tests-287%20%C3%97%203%20frameworks-brightgreen)](tests/ColumnStore.Data.Tests)
[![Dependencies](https://img.shields.io/badge/core%20dependencies-none-blue)](src/ColumnStore.Data)

```csharp
- using System.Data;
+ using ColumnStore.Data;
```

That is the port.

---

## Why

`DataTable` was introduced 25 years ago and has never been reimagined. It stores data **row-first**:
one heap-allocated `DataRow` per row, each holding an `Object[]` of cell values — so **every
value-typed cell is boxed**.

On x64, boxing an `int` costs 24 bytes, plus an 8-byte array slot to point at it. One 4-byte integer
occupies **32 bytes**:

| 1,000,000 `Int32` cells | Memory | Time | Bytes per cell |
|---|---:|---:|---:|
| Boxed `Object[]` | 30.5 MB | 11,539 µs | 32.0 |
| Typed `Int32[]` | 3.8 MB | 462 µs | 4.0 |
| **Difference** | **8× more memory** | **25× more time** | |

Now multiply by *cells*, not rows. A 1,000,000-row table with 10 value-typed columns performs **ten
million** boxing operations to fill, and ten million unboxing operations to read.

ColumnStore.Data keeps the same programming model — a schema of typed columns, row-based CRUD,
familiar collection shapes — but turns the table ninety degrees and stores it **column-first in typed
arrays**. There is no per-row object, no `Object[]` per row, and no boxing on the typed path.

---

## Results

A mixed 10-column table (`Int32`, `String`, `Boolean`, `Decimal`, `DateTime`, `Int64`, `Guid`,
`Int16`, `Double`, `Boolean`), populated identically both ways:

| Rows | `System.Data` | ColumnStore.Data | Memory saved | Build time | Faster |
|---:|---:|---:|---:|---:|---:|
| 1,000 | 0.2 MB | 0.1 MB | 69.7% | 0.3 ms → 0.0 ms | 9.6× |
| 10,000 | 2.8 MB | 0.7 MB | 74.4% | 4.9 ms → 0.3 ms | 15.1× |
| 100,000 | 25.2 MB | 7.0 MB | 72.4% | 53.6 ms → 3.4 ms | 15.6× |
| 1,000,000 | **233.5 MB** | **69.2 MB** | **70.4%** | 1,001.7 ms → 46.4 ms | **21.6×** |

**234 MB down to 69 MB.** The saving holds at around 70% from a thousand rows to a million — it is
structural, not a trick that only pays at scale.

Per column type, one million rows each — bytes retained per row:

| Type | `System.Data` | ColumnStore.Data |
|---|---:|---:|
| `Boolean` | 145.8 | **0.4** |
| `Byte` | 145.8 | 1.5 |
| `Int16` | 146.9 | 2.5 |
| `Int32` | 149.0 | 4.5 |
| `Int64` / `Double` / `DateTime` | 153.2 | 8.5 |
| `Decimal` | 161.6 | 16.5 |
| `Guid` | 185.0 | 16.5 |

This is a **single-column** table, so the `System.Data` side is dominated by the fixed per-row cost —
the row object and its `Object[]` — which is exactly the overhead that disappears. That is why every
type looks similar on the left and tracks its true width on the right.

`Boolean` at 0.4 bytes is one bit plus chunk bookkeeping — booleans are **bit-packed**, 64 rows per
`UInt64` word.

And on the operations around the data:

| Operation | Result |
|---|---|
| Load from an `IDataReader`, like-for-like | **2.9× faster**, 51% less memory |
| Send as a table-valued parameter | **1.8× faster**, 5× less garbage (212 → 40 bytes/row) |
| Delete rows | **18.6× faster** (logical tombstone, nothing shifted) |
| Fill from `DataTableReader` | **5.3× → 10.3×** faster, rising with row count (100 → 1,000,000) |

> Retained memory is `GC.GetTotalMemory(true)` after two forced gen-2 collections, not working set.
> The `System.Data` arm is built with `BeginLoadData` and positional `Object[]` — its fastest
> documented bulk path, not a deliberately slow `NewRow` loop. Both sides draw strings from the same
> pool, so the payload is identical and the difference that remains is structural. Numbers vary by
> machine; the ratios travel.

Reproduce everything:

```bash
dotnet run -c Release --project benchmarks/ColumnStore.Data.Benchmarks -- article
```

---

## Install

```bash
dotnet add package ColumnStore.Data
```

SQL Server writes (table-valued parameters, `SqlBulkCopy`) are a separate package, so an application
that only wants an in-memory table never takes a database driver:

```bash
dotnet add package ColumnStore.Data.SqlServer
```

| | `ColumnStore.Data` | `ColumnStore.Data.SqlServer` |
|---|---|---|
| .NET Framework 4.8 | ✅ | ✅ |
| .NET 8 | ✅ | ✅ |
| .NET 10 | ✅ | ✅ |
| Dependencies | **none** on .NET 8/10; one small Microsoft package on .NET Framework | `ColumnStore.Data`, `Microsoft.Data.SqlClient` |

Building the packages from source is `dotnet pack -c Release -o artifacts`.

## Quick start

```csharp
using ColumnStore.Data;

DataTable table = new DataTable("Orders");
DataColumn<Int32> quantity = table.Columns.Add<Int32>("Quantity", allowDBNull: false);
DataColumn<String> product = table.Columns.Add<String>("Product");
DataColumn<Decimal> price  = table.Columns.Add<Decimal>("Price");

table.Rows.Add(3, "widget", 19.99m);
table.Rows.Add(7, "gadget", 4.50m);

// Familiar shapes all work.
foreach (DataRow row in table.Rows)
{
    Console.WriteLine(row["Product"]);
}
table.Rows[0]["Quantity"] = 5;

// The fast path: resolve the column once, outside the loop.
Int64 total = 0;
for (Int32 rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
{
    total += quantity.Get(rowIndex);
}
```

### The fast path, in order of preference

1. **`DataColumn<T>` handle resolved once, then `Get`/`Set` per row.** No cast, no box, no type
   dispatch — an array index and a bit mask. This is the pattern the library is built around.
2. **`DataRow.Get<T>` / `Set<T>`.** One column lookup plus one interface cast per call. Convenient
   and allocation-free, but not free.
3. **`DataRow`'s `Object` indexer.** Boxes every value type it touches. It exists for source
   compatibility, not for hot loops.

You get most of the benefit from step 1 alone — and you get the memory saving no matter which you use.

### Filling from a reader

`Load` matches `System.Data.DataTable.Load`, including creating the schema when the table has none
and appending against the existing schema when it does. It takes **any** `IDataReader`, needs nothing
from the SQL Server package, and puts every cell through the reader's dedicated typed getter straight
into typed storage — so nothing boxes on the way in.

```csharp
using (IDataReader reader = command.ExecuteReader())
{
    table.Load(reader);          // or table.Load(reader, LoadOption.Upsert)
}

Int32 appended = table.LoadRowCount(reader, inferNullability: true);
```

`LoadOption` is accepted and has no effect, which is exact rather than a shortcut: every option
describes how to reconcile an incoming row with an existing row of the same primary key, and this
library has no primary keys — `System.Data.DataTable` appends for all three options too on a table
without one. `DataTableLoadTests` asserts that equivalence against the framework rather than
claiming it.

---

## SQL Server I/O

The core assembly has **no** SQL Server package reference. The adapters live in
`ColumnStore.Data.SqlServer`, which depends inwards on the core, so a caller who never touches SQL
Server never takes the dependency.

Reading needs nothing from this package — `table.Load(reader)` is in the core one. This package is
for **writes**.

```csharp
using ColumnStore.Data.SqlServer;

DataTableSqlWriter writer = new DataTableSqlWriter();

// A table-valued parameter, in one line. SqlDbType.Structured, TypeName set, value streaming.
command.Parameters.Add(writer.AsStructuredParameter("@rows", "dbo.OrderList", table));

// Declare the metadata yourself whenever the TVP type is width-sensitive.
SqlMetaData[] metadata = new SqlMetaData[]
{
    new SqlMetaData("Id", SqlDbType.Int),
    new SqlMetaData("Price", SqlDbType.Decimal, 19, 4)
};
command.Parameters.Add(writer.AsStructuredParameter("@rows", "dbo.OrderList", table, metadata));

// Bulk copy.
using (DbDataReader reader = writer.AsDataReader(table))
{
    bulkCopy.WriteToServer(reader);
}
```

### The three shapes SQL Server accepts for a `Structured` parameter

| Value | Here | Notes |
|---|---|---|
| `IEnumerable<SqlDataRecord>` | `AsStructuredParameter` / `AsSqlDataRecords` | The streaming route, and the fastest. **Recommended** |
| `DbDataReader` | `AsDataReader` | Works — SQL Server infers the TVP shape from the reader's schema rather than from metadata you declared |
| `System.Data.DataTable` | n/a | This library's `DataTable` is a different type, which is the point |

Both directions build one pre-bound delegate per field at schema time, then invoke delegates per row:
no `Type` inspection, no cast, no box on the non-null path. Deleted rows are skipped automatically.

Two things to know before using them:

- `AsSqlDataRecords` **reuses one `SqlDataRecord`**, repopulating it per row. That is the documented
  ADO.NET streaming pattern and what keeps a million-row TVP allocation-free — but the sequence must
  be consumed once, streaming. Buffering it gives you N references to the same record.
- TVP metadata is caller-supplied, because precision, scale and length cannot be derived from a CLR
  type — `decimal(18,2)` and `decimal(38,6)` are both `Decimal`. `InferSqlMetaData` is an explicitly
  best-guess convenience favouring maximum widths; read its docs before trusting it with money or
  fixed-length keys.

---

## How it works

```
Row Layer         DataTable · DataRowCollection · DataRow
                  row-shaped façade, API-compatible with System.Data.DataTable
                      │
Column Layer      DataColumnCollection · DataColumn · DataColumn<T>
                  typed column access; the only place type resolution is paid
                      │
Storage Layer     TypedColumnStorage<T>   chunked T[] growth
                  BitmapColumnStorage     UInt64 words — bool + null bits
                  RowDeletionTracker · AliveRowPrefixIndex · ChunkSizing

I/O Adapters      DataTableLoader · DataTableSqlWriter · DataTableDataReader
(separate assembly, depends inwards only)
```

The governing rule is **"schema-time cost, runtime zero-cost."** Anything whose cost depends on
`Type` — reflection, `Activator.CreateInstance`, multi-way dispatch, delegate creation — happens
exactly once, when a column is created or a schema is bound. Everything per row or per cell after
that is a resolved, non-branching operation.

Four decisions carry most of the benefit:

- **`DataRow` is a `readonly struct`** of `(table, physicalRowIndex)` — never heap-allocated, never
  stored, synthesized fresh whenever one is handed out. This is the bulk of the memory saving.
- **Column values live in a `List<T[]>` of fixed-size chunks**, a power-of-two row count so index
  maths is a shift and a mask. Nothing lands on the Large Object Heap, and growth never copies — a
  chunk is appended, never reallocated.
- **Booleans and null flags are bit-packed**, 64 rows per `UInt64`. A nullable `Int32` column costs
  four bytes and one bit per row — no `Nullable<T>` widening, no boxed `Object`, no sentinel stolen
  from the column's range. A set bit means *is null*, so writing a value normally touches the bitmap
  not at all, and a nullable column that never receives a null never allocates a single bitmap word.
- **Deletion is logical.** A deleted row is tombstoned, never shifted; a Fenwick tree over per-word
  alive counts translates a visible row position to its storage slot in O(log n), and short-circuits
  to the identity while nothing has been deleted.

---

## Compatibility with `System.Data.DataTable`

Every assignment shape System.Data code uses compiles and works, including the one a struct row would
normally reject — `Rows[i]` returns `ref DataRow`:

```csharp
table.Rows[0]["Name"] = "changed";              // works
table.Rows[0].ItemArray = values;               // works
foreach (DataRow row in table.Rows) { … }       // works
DataRow row = table.Rows[0]; row["Name"] = v;   // works
```

### What differs

All deliberate, all covered by tests.

| Behaviour | `System.Data.DataTable` | ColumnStore.Data |
|---|---|---|
| Row object | Heap-allocated `DataRow` per row | `readonly struct`, synthesized on demand |
| Reading a null cell | `DBNull.Value` | `DBNull.Value` — identical |
| `Columns["missing"]` | Returns `null` | Returns `null` — identical |
| `Columns.Clear()` | Drops the **columns** | Drops the **columns** — `DataTable.Clear()` empties rows |
| Column removal, `SetOrdinal`, rename | Supported | Supported; ordinals renumber, so cache the **handle**, never the ordinal |
| `NewRow()` + edit + `Add()` | Detached until `Add`, ordered by **add** order | Detached until `Add`, any number at once — but ordered by **creation** order. Identical whenever nothing else joins the table in between; any `Add` that *would* reorder throws, and `Rows.AddAtEnd(row)` reproduces System.Data's order |
| Deleting a row twice | Throws | No-op (idempotent) |
| Deletion | Physical removal | Logical tombstone plus index translation; slots are never reused, so `Rows.InsertAt` cannot work |
| `DataSet` | Full relational container | Container only: naming, add/remove, lookup, `Clone`, `Copy`, `Clear` |
| `Nullable<T>` column type | n/a | Use `Add<T>(name, allowDBNull: true)`, or pass `typeof(Int32?)` to the `Type` overload and it is unwrapped |
| Indexing a cell (`Rows[0]["x"]`) | Free | Costs a lazily-built row-view store, 16 bytes/row — **only** for tables that actually index a row |

### What refuses

Anything needing machinery this library deliberately does not have throws `NotImplementedException`
with a message naming the alternative — never a silent no-op, which would hand back wrong answers
instead of a stack trace.

Constraints, primary keys, `Unique`, `AutoIncrement` · `Select(filter)`, `Compute`, `DefaultView` ·
`GetChanges`, `RejectChanges`, `RowError` · `ReadXml` / `WriteXml` · `Relations` and `Merge` ·
table events (firing would cost per row; never firing would be silent).

`AcceptChanges` and `BeginLoadData` are no-ops, and exactly right: writes are already committed, and
there are no indexes to suspend.

**`DropInCompatibilityTests` is the full inventory** — run your own code against that list before
porting.

---

## Repository layout

```
src/ColumnStore.Data/                     core library + DataTable.Load (net48; net8.0; net10.0)
src/ColumnStore.Data.SqlServer/           SQL Server write adapters (Microsoft.Data.SqlClient)
tests/ColumnStore.Data.Tests/             287 tests, run on all three frameworks (xUnit)
benchmarks/ColumnStore.Data.Benchmarks/   memory, throughput and deletion harness
Spec/                                     requirements, architecture, design, coding standards
docs/                                     long-form article (Markdown, HTML, PDF)
```

## Building and testing

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

```bash
dotnet run -c Release --project benchmarks/ColumnStore.Data.Benchmarks -- article
```

The suite covers each layer bottom-up and includes:

- **Zero-allocation assertions** — exact zero, not "small" — over typed reads and writes, null writes
  and checks, row enumeration with and without deletions, and indexed access with deletions.
- **Randomised deletion patterns** cross-checked against a naive reference model, at row counts large
  enough to force the deletion index to grow and rebuild.
- **Row-lifecycle hazard tests** defining the detached-row contract: a row's data can never be
  readable without being counted, nor counted without being valid.
- **A shared compatibility suite** run against both `System.Data.DataTable` and
  `ColumnStore.Data.DataTable` through one harness, asserting equivalent behaviour.
- **SQL Server round-trips** for every typed getter and setter, nulls included, against an in-memory
  reader and in-memory `SqlDataRecord`s — no SQL Server instance required.

---

## Documentation

- **[Reimagining the DataTable](docs/Article.md)** — the long-form write-up, with every benchmark and
  the reasoning behind each design decision. Also available as
  [PDF](docs/Reimagining-DataTable.pdf).
- **[`Spec/`](Spec)** — requirements, architecture, detailed design, coding standards, and
  [`05_DesignDecisions.md`](Spec/05_DesignDecisions.md): every deviation from the spec, measured and
  argued.
- **`readme.txt`** in each source folder — narrative documentation of that layer. Every source file
  opens with a header stating its purpose, assumptions and design reasoning.

## Roadmap

Not in v1: thread safety (single-writer, the same baseline as `System.Data.DataTable`), change
tracking, constraints and keys, relations, an expression engine, XML serialization, and physical
compaction of tombstoned slots. Each throws `NotImplementedException` naming an alternative rather
than failing quietly.

Column removal and `DataSet` were out of scope for v1 and are now implemented.
