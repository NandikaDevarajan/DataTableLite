# ColumnStore.Data.SqlServer

**SQL Server writes for [`ColumnStore.Data`](https://www.nuget.org/packages/ColumnStore.Data):
table-valued parameters and `SqlBulkCopy`, without boxing a cell.**

Targets **.NET Framework 4.8**, **.NET 8** and **.NET 10**.

---

## Table-valued parameters

A TVP means producing a stream of `SqlDataRecord`s. From a `System.Data.DataTable` every cell you
read is *already boxed* and stays boxed on the way into the record. From a typed column the value
goes from an array straight into `SetInt32`.

| Producing 200,000 `SqlDataRecord`s | Time | Allocated | Bytes/row |
|---|---:|---:|---:|
| From `System.Data.DataTable` | 35,012 µs | 40.4 MB | 212.0 |
| From ColumnStore.Data | 19,851 µs | 7.6 MB | 40.0 |
| | **1.8× faster** | **5× less garbage** | |

### The one-liner

```csharp
using ColumnStore.Data.SqlServer;

DataTableSqlWriter writer = new DataTableSqlWriter();
command.Parameters.Add(writer.AsStructuredParameter("@rows", "dbo.OrderList", table));
command.ExecuteNonQuery();
```

That returns a `SqlParameter` with `SqlDbType.Structured`, the table type name set, and a value that
**streams** the table's visible rows as the command executes.

### Declaring the metadata yourself

The overload above infers `SqlMetaData` from the CLR types, and the guesses are deliberately **wide**
— `nvarchar(max)`, `decimal(38,6)`, `varbinary(max)`, `datetime2` — because precision, scale and
length cannot be derived from a CLR type. `decimal(18,2)` and `decimal(38,6)` are both `Decimal`.

If your table type is width-sensitive (`decimal(19,4)`, `char(3)`, `date`), declare it:

```csharp
SqlMetaData[] metadata = new SqlMetaData[]
{
    new SqlMetaData("Id", SqlDbType.Int),
    new SqlMetaData("Name", SqlDbType.NVarChar, 100),
    new SqlMetaData("Price", SqlDbType.Decimal, 19, 4)
};
command.Parameters.Add(writer.AsStructuredParameter("@rows", "dbo.OrderList", table, metadata));
```

### The three shapes SQL Server accepts for `Structured`

| Value | Supported here | Notes |
|---|---|---|
| `IEnumerable<SqlDataRecord>` | **`AsStructuredParameter` / `AsSqlDataRecords`** | The streaming route, and the fastest. **Recommended.** |
| `DbDataReader` | **`AsDataReader`** | Works, but SQL Server infers the TVP shape from the reader's schema rather than from metadata you declared |
| `System.Data.DataTable` | n/a | This library's `DataTable` is a different type — that is the point |

> **`AsSqlDataRecords` reuses one `SqlDataRecord` instance**, repopulating it per row. That is the
> documented ADO.NET streaming pattern and what keeps a million-row TVP allocation-free — but the
> sequence must be consumed **once, streaming**. Buffering it gives you N references to the same
> record. Build a fresh parameter per execution.

## Bulk copy

```csharp
using (DbDataReader reader = writer.AsDataReader(table))
{
    bulkCopy.WriteToServer(reader);
}
```

Deleted rows are skipped automatically by both write paths.

## Reading

Reading needs **nothing from this package**. `DataTable.Load(IDataReader)` lives in the core
`ColumnStore.Data` package and takes any ADO.NET reader, `SqlDataReader` included:

```csharp
using (SqlDataReader reader = command.ExecuteReader())
{
    table.Load(reader);
}
```

## How it works

Both directions build **one pre-bound delegate per field at schema time** and then invoke delegates
per row: no `Type` inspection, no cast, no box on the non-null path.

## Dependencies

`ColumnStore.Data` and `Microsoft.Data.SqlClient`.
