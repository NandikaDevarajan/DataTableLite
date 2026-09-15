# ColumnStore.Data — Design

This document contains detailed component design: public API shapes, internal algorithms, and the full test plan. It assumes `01_Requirements.md` (what/why) and `02_Architecture.md` (layers/dependencies) as context, and implements the layers defined there.

---

## 1. Storage Layer

### 1.1 `IDataColumnStorage<T>`
```csharp
public interface IDataColumnStorage<T>
{
    T Get(int rowIndex);
    void Set(int rowIndex, T value);
    void Clear();
}
```

### 1.2 `TypedColumnStorage<T>` — chunked array growth
- Backing structure: `List<T[]>`, each `T[]` a fixed-size **chunk**.
- **Growth:** append whole chunks lazily; writing past the current chunk count appends empty chunks until the target chunk exists. No reallocation/doubling of a single contiguous array.
- **Why chunked:** bounds any single allocation's size (staying under the LOH threshold, NFR-2/3), avoids O(n) copy-on-grow, and only allocates chunks actually touched for sparse/out-of-order writes.
- **Chunk sizing:** row-count per chunk targets 16–64 KB (NFR-3), rounded to a **power of two** so index math uses shift/mask:
  ```csharp
  int chunkIndex   = rowIndex >> chunkShift;
  int indexInChunk = rowIndex & chunkMask;
  ```
  Derived from `Unsafe.SizeOf<T>()` for value types; for reference types, size against reference width (8 bytes on 64-bit), not the referenced object's payload.
- **Bounds checking:** both `Get`/`Set` reject negative indices explicitly with `IndexOutOfRangeException` (NFR-11) rather than relying on the underlying array's own exception shape.
- **`Clear()`** drops all chunks (`storageChunks.Clear()`).

### 1.3 `BitmapColumnStorage` — bit-packed boolean/null storage
Two uses through one mechanism: (1) `DataColumn<bool>` storage, (2) the null-tracking side-channel for any nullable `DataColumn<T>`.
- Backing: `List<ulong>`, 64 rows/word.
- `Get(rowIndex)`: `(words[wordIndex] & (1UL << bitIndex)) != 0`; reading past allocated words returns `false` rather than throwing — an untouched row is "not null"/`false` by default.
- `Set(rowIndex, value)`: lazily grows `words` to cover `wordIndex`, then sets/clears the bit.
- Negative index guard, same as §1.2.
- **Does not know about row deletion** — deletion bookkeeping is `RowDeletionTracker` (§1.5), a separate type, so this primitive stays simple and reusable for booleans/nulls with no deletion-related complexity leaking in.

### 1.4 `ChunkSizing`
```csharp
public static class ChunkSizing
{
    public static int Recommended<T>(); // power-of-two row count, 16–64KB target, < 85,000 bytes
}
```
Used by default in `TypedColumnStorage<T>`; an optional override may be threaded through `DataColumn<T>`'s constructor path for unusual access patterns.

### 1.5 `RowDeletionTracker` — logical deletion
```csharp
internal sealed class RowDeletionTracker
{
    public void MarkDeleted(int physicalRowIndex);
    public bool IsDeleted(int physicalRowIndex);
    public int LogicalCount { get; }              // physicalCount - deletedCount, maintained incrementally

    public int ToPhysicalIndex(int logicalIndex);
    public int ToLogicalIndex(int physicalIndex);
}
```
Internal structure:
- Tombstone bitmap: `List<ulong>`, **1 = deleted**, same word-packing idea as §1.3 but a distinct type/instance — do not conflate "is this bool column's value true" with "is this row deleted."
- Parallel `List<byte> deletedCountPerWord`: running popcount of deleted bits per word, updated incrementally on `MarkDeleted` (increment by 1; never recompute by rescanning).

**`ToPhysicalIndex(logicalIndex)` algorithm:**
1. Walk tombstone words in order, accumulating `aliveSoFar += (64 - deletedCountPerWord[wordIndex])`, until the word where the target alive bit lives is found (`aliveSoFar` would first exceed `logicalIndex`).
2. `remaining = logicalIndex - aliveSoFarBeforeThisWord` (0-based position of the target alive bit within this word, counting only non-deleted bits).
3. Scan bits within the word from position 0 upward, skipping deleted bits, counting non-deleted bits until `remaining` reaches zero; that bit position is the answer. (Linear scan first for correctness; an optimized version can use `BitOperations.PopCount` on masked prefixes to binary-search the position — optimize only after correctness tests pass.)
4. Return `wordIndex * 64 + bitPositionWithinWord`.

**`ToLogicalIndex(physicalIndex)`:** inverse — walk full words up to `physicalIndex`'s word accumulating alive counts, then add the count of non-deleted bits before `physicalIndex` within its own word.

**Row-creation interaction:** new rows always take the next unused **physical** slot; physical slots are never reused after deletion, keeping "physical index is permanent" simple.

---

## 2. Column Layer

### 2.1 `IDataColumn` / `IDataColumn<T>`
```csharp
public interface IDataColumn
{
    string Name { get; }
    Type DataType { get; }
    bool AllowNull { get; }

    object GetValue(int rowIndex);
    void SetValue(int rowIndex, object value);
    bool IsNull(int rowIndex);
    void Clear();
}

public interface IDataColumn<T> : IDataColumn
{
    T Get(int rowIndex);
    void Set(int rowIndex, T value);
}
```
`IsNull(int)` is on the **non-generic** interface so generic consumers (SQL loader, TVP writer, row-level `IsNull`) can check nullability by ordinal without knowing each column's `T` (this is what makes FR-20's `IDataReader` adapter possible without per-column type pattern-matching).

### 2.2 `DataColumn<T>`
```csharp
public sealed class DataColumn<T> : IDataColumn<T>
{
    public string Name { get; }
    public Type DataType { get; }         // typeof(T)
    public bool AllowNull { get; }

    public T Get(int rowIndex);           // throws if the cell is null — see IsNull / GetOrDefault
    public void Set(int rowIndex, T value);
    public void SetNull(int rowIndex);    // throws if AllowNull == false
    public bool IsNull(int rowIndex);

    public object GetValue(int rowIndex); // returns null for a null cell instead of throwing
    public void SetValue(int rowIndex, object value); // null -> SetNull; wrong type -> clear, column-named error
    public void Clear();
}
```
- Wraps one `IDataColumnStorage<T>` plus (if `AllowNull`) one `BitmapColumnStorage` for null tracking.
- **Construction is factory-only** (`DataColumnFactory.Create` or `DataColumnCollection.Add<T>`) — no public direct constructor. This guarantees `typeof(T) == typeof(bool)` always resolves to `BitmapColumnStorage`-backed storage (FR-3), with no path that can accidentally construct a byte-per-row boolean column.
- `Get` throws on a null cell (fail-fast, FR-9); `GetOrDefault<T>`/`Set<T>(int, T?)` extension methods over `DataColumn<T>` (`where T : struct`) provide `Nullable<T>` interop without `DataColumn<T>` itself handling `Nullable<T>` internally.
- `SetValue` with a mismatched type throws a clear, column-named error (NFR-12), not a bare `InvalidCastException`.

### 2.3 `DataColumnFactory`
```csharp
public static class DataColumnFactory
{
    public static IDataColumn Create(string name, Type dataType, bool allowNull);
}
```
Reflection/`Activator.CreateInstance` cost paid exactly once per column, at column-creation time. Special-cases `dataType == typeof(bool)` to construct the `BitmapColumnStorage`-backed path explicitly rather than going through generic `DataColumn<>.MakeGenericType(...)` for booleans.

### 2.4 `DataColumnCollection`
```csharp
public sealed class DataColumnCollection : IEnumerable<IDataColumn>
{
    public void Add(string columnName, Type type, bool allowNull = true);
    public DataColumn<T> Add<T>(string columnName, bool allowNull = true);

    public int Count { get; }
    public IDataColumn this[int index] { get; }
    public IDataColumn this[string name] { get; }
    public int IndexOf(string name);
    public bool Contains(string name);

    public DataColumn<T> GetColumn<T>(string name);
    public DataColumn<T> GetColumn<T>(int index);

    public void Clear();  // clears every column's data; schema (column list) unaffected
}
```
`Add<T>`/`GetColumn<T>` support the primary performance pattern (FR-5):
```csharp
DataColumn<int> qty = table.Columns.GetColumn<int>("Quantity");
for (int i = 0; i < table.Rows.Count; i++)
{
    int v = qty.Get(i);       // one cast, resolved outside the loop
}
```
Document this as the recommended pattern for hot loops, ahead of row-level `Get<T>`/`Set<T>` (§3.2), which is more convenient but pays a column lookup per call.

---

## 3. Row Layer

### 3.1 `DataRow` — synthesized view, not a stored object
```csharp
public readonly struct DataRow
{
    internal DataRow(DataTable table, int rowIndex);

    public int RowIndex { get; }

    public object this[int columnIndex] { get; set; }
    public object this[string columnName] { get; set; }

    public T Get<T>(int columnIndex);
    public T Get<T>(string columnName);
    public T GetOrDefault<T>(int columnIndex);   // default(T) instead of throwing on null
    public T GetOrDefault<T>(string columnName);

    public void Set<T>(int columnIndex, T value);
    public void Set<T>(string columnName, T value);

    public bool IsNull(int columnIndex);
    public bool IsNull(string columnName);

    public object[] ItemArray { get; set; }
}
```
**Key decision (satisfies NFR-1):** `DataRow` is a `readonly struct` carrying only `(DataTable table, int rowIndex)`. It is never heap-allocated by the library, never stored anywhere by `DataRowCollection`, and is synthesized fresh every time one is handed out (`Rows[i]`, `Rows.AddNewRow()`, enumeration). Writes go straight into column storage — there is no `object[]` buffer inside `DataRow`, so there's no "detached, not-yet-part-of-the-table" state representable inside `DataRow` itself; that state, when needed, is tracked one level up in `DataRowCollection` (§3.3), the only place that can answer "does this row index count yet."

### 3.2 Typed row access vs. typed column access
Row-level `Get<T>`/`Set<T>` satisfy FR-8 for compatibility/convenience, but each call does one column lookup plus one interface cast — cheap, not free, repeated per call. Callers iterating many rows against a known column should prefer `Columns.GetColumn<T>` (§2.4) once, outside the loop. Document this trade-off wherever row-level typed access appears.

### 3.3 `DataRowCollection` — row lifecycle
```csharp
public sealed class DataRowCollection : IEnumerable<DataRow>
{
    public int Count { get; }              // logical count — excludes deleted rows

    public DataRow NewRow();               // begin a row; see commit/discard contract below
    public void Discard(DataRow row);      // abandon a pending row created by NewRow()
    public void Add(DataRow row);          // commit a row created by NewRow()

    public DataRow AddNewRow();            // atomically reserve + commit, ready for field writes
    public DataRow Add(params object[] values); // atomically reserve + populate + commit — preferred path

    public DataRow this[int logicalIndex] { get; }
    public void Delete(DataRow row);       // logical delete
    public bool IsDeleted(int physicalRowIndex);

    public void Clear();
    public Enumerator GetEnumerator();     // manual struct enumerator — no per-foreach heap allocation
    public struct Enumerator : IEnumerator<DataRow> { /* mirrors List<T>.Enumerator shape */ }
}
```

**Commit/discard contract (satisfies FR-7, NFR-10):** because `DataRow.Set<T>` writes immediately into column storage, a row reserved via `NewRow()` already occupies a real physical slot the moment any field is set, even though it isn't logically part of the table yet. The collection tracks this explicitly:
- Holds a single `int? pendingIndex`.
- `NewRow()`: if `pendingIndex.HasValue`, throw `InvalidOperationException` — only one uncommitted row at a time. Otherwise reserve the next physical slot, record it as `pendingIndex`, return a `DataRow` view over it.
- `Add(DataRow row)`: validates `row.RowIndex == pendingIndex`; increments `Count`; clears `pendingIndex`. Throws if there's no pending row or the given row doesn't match (guards against a stale `DataRow` from a previous `NewRow()` cycle).
- `Discard(DataRow row)`: same validation as `Add`, but does not increment `Count`. Values already written at that slot are abandoned — occupy storage but are never visible (physical slots below the logical boundary, minus deleted ones, are the only visible ones). No corruption risk, only small wasted space.
- `AddNewRow()` and `Add(params object[] values)` never touch `pendingIndex` — atomic reserve+commit (and populate, for the `params` overload) in one call. **`Rows.Add(params object[] values)` is the default, hazard-free path**; `NewRow()`/`Add(DataRow)` is the advanced path for incremental field population.
- `Clear()` resets `Count` to 0 and clears `pendingIndex`.

`DataRowCollection` never stores `DataRow` instances — only scalar bookkeeping (`Count`, `pendingIndex`, the `RowDeletionTracker`). Every `DataRow` handed out is constructed fresh.

**Deletion integration (§1.5):**
- `Count` returns `deletionTracker.LogicalCount`.
- `this[int logicalIndex]` calls `ToPhysicalIndex`, constructs `new DataRow(table, physicalIndex)`.
- `Enumerator` walks physical indices, skipping tombstoned ones (cheaper than calling `ToPhysicalIndex` per logical position), yielding freshly-constructed `DataRow`s.
- `Delete(DataRow row)` calls `deletionTracker.MarkDeleted(row.RowIndex)` — no column storage touched.
- Deleting an already-deleted row: pick one behavior (no-op or throw), document it, cover with a test.

### 3.4 `DataTable`
```csharp
public sealed class DataTable
{
    public DataTable();
    public DataTable(string name);

    public string TableName { get; set; }
    public DataColumnCollection Columns { get; }
    public DataRowCollection Rows { get; }
    public int Count { get; }               // convenience alias for Rows.Count

    public DataRow NewRow();
    public DataRow AddNewRow();
    public void Clear();                    // clears row data, keeps column schema (FR-11)
}
```
No SQL-Server-specific dependency belongs here (FR-12) — see Architecture §4, rule 4.

---

## 4. I/O Adapter Layer

### 4.1 `DataTableLoader` (SQL Server read)
```csharp
public sealed class DataTableLoader
{
    public void Load(System.Data.IDataReader reader, bool inferNullability, DataTable table);
}
```
**Schema-time (once per `Load` call):**
1. If the target table has no columns, create one per reader field from `reader.GetName(i)`/`GetFieldType(i)`, and (if `inferNullability`) infer nullability from `reader.GetSchemaTable()`'s `AllowDBNull` metadata, defaulting to nullable if unavailable/unsupported (wrap in try/catch — not all providers implement `GetSchemaTable()`).
2. For each reader field, resolve its target table column (`table.Columns.IndexOf(name)`), and compile one delegate per field capturing: the reader ordinal, the target typed column (cast once here), and which typed `IDataReader` getter to call.
3. Dispatch on `column.DataType` to pick the getter: `GetInt32`, `GetInt64`, `GetInt16`, `GetByte`, `GetBoolean`, `GetDecimal`, `GetDouble`, `GetFloat`, `GetDateTime`, `GetGuid`, `GetString`. Types without a dedicated getter (`byte[]`, `char`, custom provider types) fall back to `IsDBNull`/`GetValue` + `column.SetValue(object)` — still resolved once at schema time, just unable to avoid boxing for that field's runtime type.

**Per-row (hot loop):** `while (reader.Read())`, invoke each pre-built delegate. Non-null, dedicated-getter path: read the typed value straight from the reader, `typedColumn.Set(rowIndex, value)` — no cast, no box. Null path: `column.SetValue(rowIndex, null)` → routes to `SetNull`.

**Requirements satisfied:** FR-16–18, NFR-6–7. Must accept `System.Data.IDataReader` (not a SQL-Server-specific type) so the core has no SQL Server package dependency; `SqlDataReader` satisfies this automatically. Must support repeated calls against the same table without re-creating columns. An async variant (`LoadAsync(DbDataReader, ...)`) is a reasonable future addition using the same pattern, not required for v1.

### 4.2 `DataTableSqlWriter` (SQL Server write)
```csharp
public sealed class DataTableSqlWriter
{
    // For SqlParameter.Value when SqlDbType.Structured (TVP):
    public IEnumerable<SqlDataRecord> AsSqlDataRecords(DataTable table, SqlMetaData[] metadata);

    // For SqlBulkCopy.WriteToServer(IDataReader):
    public IDataReader AsDataReader(DataTable table);
}
```
**`AsSqlDataRecords`:** schema-time — for each caller-supplied `SqlMetaData` entry (precision/scale/length can't be inferred from `DataColumn<T>.DataType` alone), resolve the matching column and compile a typed delegate calling the correct `SqlDataRecord.SetInt32`/`SetString`/`SetDateTime`/etc. on the non-null path (via `IDataColumn<T>.Get`), and `SetDBNull` when `IsNull` is true. Runtime: reuse a single `SqlDataRecord` instance, re-populated per logical row (deleted rows skipped automatically — iteration walks logical indices).

**`AsDataReader`:** minimal `IDataReader`/`IDataRecord` adapter over `DataTable`. `FieldCount`/`GetName`/`GetOrdinal`/`GetFieldType` delegate to `table.Columns`. Each typed getter resolves its backing `DataColumn<T>` **once**, at adapter construction, then calls `Get(currentPhysicalRowIndex)` per row. `Read()` advances over logical row positions the same way `DataRowCollection`'s enumerator does. `IsDBNull(ordinal)` delegates to `IDataColumn.IsNull(rowIndex)` — the reason `IsNull` is on the non-generic interface (§2.1).

**Requirements satisfied:** FR-19–22, NFR-7.

---

## 5. Compatibility Notes vs. `System.Data.DataTable`

| Behavior | `System.Data.DataTable` | `ColumnStore.Data` | Note |
|---|---|---|---|
| Row object | Heap-allocated `DataRow` per row | `readonly struct`, synthesized on demand | Source of the primary memory saving (NFR-1) |
| `NewRow()` + edit + `Add()` | Fully detached until `Add` | Field writes land in storage immediately; `Add`/`Discard` required before a second `NewRow()` | Advanced path; `Rows.Add(params object[])` preferred |
| Column removal | Supported | Out of scope v1 (append-only schema) | Revisit if needed |
| Deletion | Physical removal or `RowState`/`AcceptChanges` | Logical deletion via tombstone bitmap + index translation | No change-tracking model in v1 |
| Thread safety | Not thread-safe | Not thread-safe | Same baseline |
| SQL Server load | `SqlDataAdapter.Fill` or manual loop | `DataTableLoader.Load(IDataReader, ...)` | No `SqlDataAdapter`-style dependency in core |
| TVP / bulk write | Direct pass as TVP value (framework-supported), or manual mapping | `DataTableSqlWriter.AsSqlDataRecords`/`AsDataReader` | `ColumnStore.Data.DataTable` can't be passed directly as a TVP; the writer bridges this |

---

## 6. Test Plan

### 6.1 Storage layer
- `TypedColumnStorage<T>`: round-trip get/set within a chunk; sparse write far beyond current size grows lazily, unwritten intermediate rows read `default(T)`; read past all allocated chunks throws; negative index throws on `Get`/`Set`; `Clear()` empties storage (subsequent `Get(0)` throws again).
- `BitmapColumnStorage`: round-trip across single-word boundary (bit 63/64) and across a word-list growth boundary; untouched high row index returns `false` without throwing; negative index throws.
- `ChunkSizing.Recommended<T>()`: for `byte`, `int`, `long`, `decimal`, `Guid`, `DateTime`, `string`, returned chunk row-count is a power of two, implied byte size in [16KB, 64KB) and under 85,000 bytes.
- `RowDeletionTracker`: `ToPhysicalIndex`/`ToLogicalIndex` round-trip for no deletions (identity), single deletion at start/middle/end, consecutive deletions spanning a word boundary.

### 6.2 Column layer
- `DataColumn<T>.Set`/`Get` round-trip; `SetNull`+`Get` throws; `SetNull`+`IsNull` true; `GetValue` returns `null` for a null cell.
- `AllowNull = false`: `SetNull` throws.
- `SetValue` with mismatched runtime type throws a clear, column-named error.
- Every public bool-column construction path (`DataColumnFactory.Create`, `Add<bool>`) results in `BitmapColumnStorage`-backed storage (verify via internal test hook/reflection).
- `DataColumnCollection.Add<T>`/`GetColumn<T>` return the same instance as `this[name]` cast; `GetColumn<T>` with mismatched `T` throws clearly.
- `Clear()` empties every column's data; `Columns.Count` and identities unchanged.

### 6.3 Row layer (highest priority — trickiest correctness area)
- `AddNewRow()` → `Set<T>` several columns → values readable via `Rows[i]`.
- `Rows.Add(params object[])` populates positionally, commits atomically, `Count` +1.
- `NewRow()` twice without intervening `Add`/`Discard` throws `InvalidOperationException`.
- `NewRow()` → writes → `Add(row)`: `Count` +1; fields visible at correct logical index.
- `NewRow()` → writes → `Discard(row)`: `Count` unchanged; discarded slot never visible via any enumeration/indexer path; the *next* `AddNewRow()` takes the next physical slot, not the discarded one.
- `Add(row)`/`Discard(row)` with no pending row, or a mismatched `RowIndex`, throws `InvalidOperationException`.
- Manual `Enumerator`: zero heap allocation for the enumeration mechanism itself (allocation-delta assertion); visits exactly `Count` rows in order.
- `Clear()` resets `Count` to 0, clears pending row state.

### 6.4 Deletion (integration, via `DataRowCollection`)
- `Delete` on a row: `Count` -1, row absent from enumeration, `Rows[i]` re-indexes correctly around the gap.
- Deleting the same row twice: behavior matches documentation (no-op or throw).
- New rows after deletions always land on new physical slots, never reusing a deleted one.

### 6.5 SQL Server read (`DataTableLoader`)
- Mocked `IDataReader` covering every dedicated-getter type plus one fallback type (e.g. `byte[]`); values/nulls round-trip correctly.
- `inferNullability = true` with mixed-nullability schema metadata → matching `AllowNull` per column; `inferNullability = false` or a `GetSchemaTable()`-throwing reader → every column defaults nullable, load still succeeds.
- Loading twice into the same table appends rows without re-creating columns.
- Allocation-delta test: N rows through dedicated-getter columns allocate no boxed values per cell.

### 6.6 SQL Server write (`DataTableSqlWriter`)
- `AsSqlDataRecords`: one record per logical row (deleted rows skipped), correct values, `DBNull.Value` for nulls.
- `AsDataReader`: correct schema metadata and typed getter values/nulls when driven directly or via a mocked `SqlBulkCopy` target.
- Allocation-delta test on the non-null hot path for both entry points.

### 6.7 Compatibility / benchmark
- Shared test-logic file exercising common patterns (create columns, `NewRow`/`Add`, `Rows.Add(params object[])`, `foreach`, `Clear`, typed column access) run against both `System.Data.DataTable` and `ColumnStore.Data.DataTable` via a swappable alias, asserting equivalent results for overlapping API surface.
- Memory benchmark (tracked, not pass/fail): 1,000,000-row, ~10-column table, allocated bytes/working set vs. `System.Data.DataTable`. Re-run whenever the row/storage layers change.

---

## 7. Open Questions

1. Deletion bitmap semantics are fixed here as **1 = deleted**; confirm no downstream code assumes the opposite before implementation begins.
2. `DataColumnCollection.Remove` (column removal) is explicitly out of scope for v1 (Requirements §3) — confirm this is acceptable, since removing a column from a columnar store with existing row data raises questions about cached `DataColumn<T>` references callers may be holding per the hot-loop pattern (§2.4).
3. SQL Server TVP metadata (`SqlMetaData[]`) is caller-supplied in this design (§4.2) rather than inferred, since precision/scale/length can't be reliably derived from `DataColumn<T>.DataType` alone. Confirm this is acceptable, or decide whether a "best guess" inference helper is worth adding as a clearly-marked convenience.
