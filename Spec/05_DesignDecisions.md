# ColumnStore.Data — Design Decisions and Deviations

This document closes the loop on the specification. It answers the open questions in
`03_Design.md` §7, records every place the implementation deliberately departs from or refines the
design (required by NFR-9), and states the empirical results that NFR-4 asks to be validated rather
than assumed.

It is written as the implementation record. `01_Requirements.md` says *what* and *why*,
`02_Architecture.md` says *where*, `03_Design.md` says *how*; this says *what was actually decided,
and why that*.

---

## 1. Resolutions of `03_Design.md` §7 open questions

### 1.1 Deletion bitmap semantics — confirmed as specified

**Question.** "Deletion bitmap semantics are fixed here as **1 = deleted**; confirm no downstream
code assumes the opposite before implementation begins."

**Resolution: confirmed, 1 = deleted, and nothing assumes otherwise.** `RowDeletionTracker` is the
only type that reads or writes the tombstone bitmap. No other type in the library sees a tombstone
bit; the row layer asks it questions (`IsDeleted`, `ToPhysicalIndex`, `TryGetNextAlivePhysicalIndex`)
and never inspects the representation. The one place the sense could have leaked — a `Boolean`
column's own bit-packed storage — is a deliberately separate type and separate instance
(`BitmapColumnStorage`), exactly as §1.3 requires, so "this bool cell is true" and "this row is
deleted" cannot be confused.

The convention is stated in `RowDeletionTracker`'s file header and asserted behaviourally by
`RowDeletionTrackerTests`.

### 1.2 Column removal — confirmed out of scope

**Question.** "`DataColumnCollection.Remove` is explicitly out of scope for v1; confirm this is
acceptable, since removing a column from a columnar store with existing row data raises questions
about cached `DataColumn<T>` references callers may be holding per the hot-loop pattern."

**Resolution: confirmed out of scope, and the reasoning in the question is the reason.** The
library's primary performance pattern hands callers a long-lived `DataColumn<T>` handle and invites
them to keep it outside a loop. A removal would leave those handles pointing at a column the table
no longer contains, with no way to detect it that does not add a validity check to the hot path — the
exact cost the pattern exists to avoid.

The schema is therefore append-only, and `DataColumnCollection` has no `Remove`. This is stated in
the collection's file header, in `src/ColumnStore.Data/readme.txt`, and in the compatibility table in
`README.md`.

If removal is wanted later, the shape that preserves the guarantee is a *tombstoned column* — mark
the column hidden, release its storage, keep its ordinal — rather than a compacting removal that
shifts ordinals. That mirrors what the row layer already does for deletion, for the same reason.

### 1.3 TVP metadata — confirmed caller-supplied, with a marked convenience added

**Question.** "SQL Server TVP metadata (`SqlMetaData[]`) is caller-supplied rather than inferred,
since precision/scale/length can't be reliably derived from `DataColumn<T>.DataType` alone. Confirm
this is acceptable, or decide whether a 'best guess' inference helper is worth adding as a
clearly-marked convenience."

**Resolution: confirmed caller-supplied, and the marked convenience was added.**
`DataTableSqlWriter.AsSqlDataRecords` requires the metadata array; nothing is inferred behind the
caller's back. `DataTableSqlWriter.InferSqlMetaData` is offered separately as an explicitly
best-guess helper, and its guesses are chosen so that a wrong guess costs bandwidth rather than data:

| Column type | Inferred SQL Server type | Why this guess |
|---|---|---|
| `String` | `nvarchar(max)` | Cannot truncate. A guessed `nvarchar(50)` silently would. |
| `Byte[]` | `varbinary(max)` | Same reasoning. |
| `Decimal` | `decimal(38,6)` | Maximum precision SQL Server allows, so no CLR-representable value overflows. Scale 6 covers currency and unit rates; anything finer needs explicit metadata. |
| `DateTime` | `datetime2` | Full range and precision; `datetime` would clip both. |
| `Int32`/`Int64`/`Int16`/`Byte`/`Boolean`/`Double`/`Single`/`Guid` | exact counterpart | No width to guess. |
| anything else | — | `NotSupportedException` naming the column, rather than a silent guess. |

The helper's own XML documentation spells this out and names the cases where it must not be trusted
(`decimal(19,4)`, `char(3)`, `date`, and anything else width-sensitive).

---

## 2. Refinements of the design

Three places where the implementation does something other than the literal text of
`03_Design.md`, and four places where it adds to it - two of those being techniques that were measured and rejected,
and one being the System.Data compatibility layer that supersedes two of the original non-goals. Each is a considered refinement, not an oversight, and each is documented in the
header of the file that makes the choice.

### 2.1 The null bitmap records "is null", with a per-column written watermark

**Design text (§1.3).** The null side channel is described in "is null" terms: a bit per row, set when the cell is
null.

**What was implemented.** Exactly that — a set bit means null — plus one extra `Int32` per column,
`highestWrittenRowIndex`, recording the highest row the column has ever been written at.

**Why the "is null" sense.** Real data is overwhelmingly non-null, so the common path should not have to touch the
bitmap at all. `Set` reads the null bit and clears it *only* when it is actually set. For a nullable column that never
receives a null, the bitmap's word list is never grown: zero words allocated, zero stores on the value path.

**Why `Set` cannot simply skip the bitmap.** Writing a value over a cell that was previously set null must clear that
cell's bit, or the cell keeps reading as null while holding a value. The fast path may skip the *store*, never the
*load* that decides whether a store is needed. `DataColumnTests.SetOverANullCellClearsTheNull` pins this.

**Why the watermark is not optional.** Under a bare "is null" bitmap an untouched cell carries no bit and therefore
reads as NOT null. That is not merely a compatibility wart:

- `Get` on an unwritten cell fell through to the value store and threw `IndexOutOfRangeException` from the storage
  layer, rather than the clear "this cell is null" error FR-9 requires.
- For a reference-typed column, `IsNull` returned false while the store returned a null reference, so
  `DataTableSqlWriter` passed that null to `SqlDataRecord.SetString` and **`Microsoft.Data.SqlClient` threw a
  `NullReferenceException` from inside `ValueUtilsSmi.SetString_LengthChecked`.**

Both were caught by the existing suite the moment the sense was inverted. The watermark fixes both at a cost of one
comparison and an occasional store to a field already in cache: any row above it was never written and is null by
definition, and any row at or below it is decided by the bitmap. Row creation still costs nothing per column, and an
unpopulated cell still reads as null exactly as `System.Data.DataTable` reports `DBNull`.

**The residual approximation, and where it is closed.** A caller who writes a HIGH row index and leaves LOWER ones
untouched puts those lower cells below the watermark with no null bit, so they read as `default(T)`. Sequential
population — every loader, every `Rows.Add`, every append-then-fill loop — is exact. Two known-skip points are closed
explicitly because the library can see them coming:

- `DataRowCollection.Add(params Object[])` marks the trailing columns it was given no value for as null. Costs nothing
  when every column is supplied, because the loop does not execute.
- `DataColumnCollection` marks the already-existing rows null when a column is added to a non-empty table. Costs
  nothing in the schema-then-rows ordering, because there are no rows yet. The row count arrives as a plain
  `Func<Int32>` rather than a table reference, so the column layer still depends on nothing above it
  (02_Architecture rule 2).

What remains is pinned by `DataColumnTests.SparseWriteLeavesLowerUnwrittenCellsReadingAsDefaultNotNull`, which is
written to be deleted rather than repaired if the approximation is ever replaced by something exact.

**What it is worth.** Measured on the three production shapes at 50,000 rows, comparing nullable columns that carry
nulls against nullable columns that happen always to be populated:

| Shape | Nullable columns | Retained, 1-in-7 null | Retained, no nulls | Saved | Load time saved |
|---|---:|---:|---:|---:|---:|
| Audit | 17 | 8,089 KB | 7,953 KB | 136 KB (1.7%) | 7.4% |
| Investigation | 78 | 30,676 KB | 30,050 KB | 626 KB (2.0%) | 1.3% |
| Attachment | 59 | 22,249 KB | 21,775 KB | 474 KB (2.1%) | 3.3% |

The memory figures match the arithmetic exactly — one bit per row per nullable column, plus the word list's growth
overhead — which is the point: the saving is real, bounded and predictable, not a guess. Where a nullable column does
carry nulls the words are allocated either way and the change is neutral. **The saving is therefore a property of the
DATA, not of the schema.**

### 2.2 Index translation uses a Fenwick tree over per-word alive counts

**Design text (§1.5).** Specifies a per-word deleted count with a linear walk over words, and
explicitly invites optimisation: *"Linear scan first for correctness; an optimized version can use
`BitOperations.PopCount` on masked prefixes to binary-search the position — optimize only after
correctness tests pass."*

**What was implemented.** `AliveRowPrefixIndex`: per-word counts stored as **alive** rather than
deleted rows, with a Fenwick (binary indexed) tree over them. Recording a deletion is O(log words);
translating an index is O(log words) via a Fenwick descent plus a single-word bit selection
(`PDEP` where available, a portable bit-clearing loop otherwise). With nothing deleted, translation
short-circuits to the identity.

**Why.** The linear walk is O(rows / 64) per translation — 15,625 iterations on a million-row table —
so any indexed read after a single deletion would become painfully slow. Alive counts are stored
rather than deleted counts because the Fenwick "find the element containing the k-th unit" descent
operates directly on the quantity being searched. The cost is eight bytes per 64 rows: 0.125 bytes
per row.

**How it is trusted.** `RowDeletionTrackerTests` cross-checks both translation directions against a
deliberately naive linear reference model over randomised deletion patterns, at row counts chosen to
force the tree to grow and rebuild (64, 200, 513, 1024, 5000 rows; up to 4,999 of 5,000 rows
deleted), and additionally verifies the enumeration primitive walks exactly the surviving rows in
order. `RowDeletionIntegrationTests` repeats the exercise from the outside, through the public API,
against a plain `List<T>` model — including patterns that interleave deletions with insertions,
which is what would break an index cached without invalidation.

### 2.3 `DataTableDataReader`'s typed getters cast per call

**Design text (§4.2).** *"Each typed getter resolves its backing `DataColumn<T>` **once**, at adapter
construction, then calls `Get(currentPhysicalRowIndex)` per row."*

**What was implemented.** The `IDataColumn[]` is resolved once at construction; each typed getter
performs one interface cast per call.

**Why.** Recovering `T` from an `IDataColumn` at the call site requires a cast — that is the only
mechanism the language offers. A genuinely per-type pre-resolved layout therefore needs one parallel
array per supported type (eleven arrays, mostly null entries) purely so a getter can be handed an
already-cast reference. The cast that replaces it is a single `castclass` against a sealed type: no
reflection, no type search, no dictionary lookup, no allocation, no boxing — which is what NFR-7
actually requires. The eleven-array variant was judged worse code for a gain unmeasurable against
`SqlBulkCopy`'s own per-row cost.

The reasoning is recorded in the file's header so the deviation is visible where the code is read.

### 2.4 The default chunk size is a fixed 128 rows, not the 16–64 KB band

**Design text (§1.2, and NFR-2/NFR-3).** "Chunk sizing: row-count per chunk targets 16–64 KB (NFR-3),
rounded to a power of two."

**What was implemented.** `ChunkSizing.DefaultRowCount` is a fixed **128 rows**, independent of
element size, and it is what `TypedColumnStorage` uses when constructed without an explicit size. The
16–64 KB rule is still implemented, still a power of two, still per element type, and still fully
tested — as `ChunkSizing.LohSafeRowCount<T>()` — but it is no longer the default.

**Why.** With the band-derived sizing, every non-`Boolean` column allocated a 32 KB first chunk
however few rows it held, so a 100-row, 10-column table retained about 290 KB — 2,983 bytes per row,
worse than `System.Data.DataTable`'s 924. That is a real penalty for anyone holding many small
tables, and it was invisible until the row-count sweep in §4.2 was run. At 128 rows the same table
retains 12 KB, or 125 bytes per row, and is 7.4× better than the framework rather than 3.2× worse.

**What it costs.** Measured, in §4.5: at 100,000 and 1,000,000 rows, 128-row chunks cost roughly 3%
more retained memory, 12% slower sequential reads and 27% slower inserts than the best-performing
size, because a million-row column then holds 7,813 chunks rather than 123 — more array headers, less
contiguity for a scan, and more objects for the collector to trace.

**Is the requirement still met in spirit?** NFR-2 asks that "no single allocation for a column's
storage should routinely land on the Large Object Heap", and NFR-3 names 16–64 KB as the means. The
end is comfortably met: a 128-row chunk is 512 bytes for `Int32` and 2 KB for `Decimal`, orders of
magnitude below the 85,000-byte threshold, and growth is still by whole appended chunks with no
copy-on-grow. What the deviation gives up is the *upper* half of NFR-3's reasoning — that a chunk be
large enough for its per-chunk bookkeeping to be noise — which is precisely the cost §4.5 measures.

**Recommendation on the evidence: 512 rows.** It is indistinguishable from 128 on a small table
(38 KB against 12 KB, both negligible) and the fastest arm measured at a million rows on every axis.
The value stands at 128 by explicit request; it is one constant, and any column can override it
through `DataColumnCollection.Add<T>(name, allowNull, chunkRowCount)`.

---

### 2.5 The loader binds one delegate per type, and resolves nullability at schema time

**Design text (4.1).** The loader binds one delegate per field at schema time, "whose body is an `IsDBNull` test, a
typed read and a typed store".

**What was implemented.** Two changes to what that delegate contains, both measured, plus one correction to an
earlier version of this section.

**One binding factory per type, not one generic factory.** The obvious factory is generic - `CreateTypedBinding<T>`
holding a `Func<IDataReader, Int32, T>` - and that is what was written first. It costs a **second indirect call per
cell**: the binding is invoked, and the getter is then invoked through it. Writing `reader.GetInt32(ordinal)` into the
lambda instead removes that hop and leaves a direct interface call. The price is eleven near-identical factories,
which is real duplication and is accepted deliberately; the measurement below is why.

**Nullability resolved once, not per cell.** A column that cannot hold a null gets a binding with no `IsDBNull` in it
at all. The Object fallback binding lost its test outright with no compensation needed: `GetValue` already reports a
null as `DBNull.Value` and `SetValue` already routes `DBNull` and `null` to `SetNull`, so the test was asking the
reader the same question twice.

**Where the null diagnostic went, and the mistake that got there.** Dropping the test means a source that yields a
null for a column declared non-nullable now fails inside the provider's typed getter, with no column named. The first
attempt recovered that with a per-cell `try`/`catch` carrying an exception filter. **That was wrong, and it made real
loads slower.** It had been measured against `DataTableReader`, whose `IsDBNull` is expensive; against a reader whose
`IsDBNull` is cheap - which is every real provider, including `SqlDataReader` on an already-fetched row - the
per-cell EH region cost more than the test it replaced, and on a mixed shape the whole change came out net negative.

The guard now wraps the **whole load**: `LoadRows` and `LoadRowsAsync` contain no exception handling at all, and
`Load`/`LoadAsync` catch around them with a filter. A failed load leaves the reader positioned on the offending row,
so the culprit is found by scanning the ordinals of the fields bound to non-nullable columns, which
`BuildFieldBindings` collected at schema time. One EH region per load, none per cell. Because it is a filter and not
a catch block, a failure on a cell that is not null propagates as itself, with its original stack.

- `DataTableLoaderTests.LoadNullIntoANonNullableColumnThrowsNamingTheColumn` pins the diagnostic.
- `DataTableLoaderTests.LoadTypeMismatchOnANonNullCellSurfacesAsTheProvidersOwnFailure` pins that the filter does not
  claim failures it did not cause.

**What it is worth.** Row loop only, 50,000 rows, best of five, arm order reversed on a second pass, measured over
`BufferedColumnReader` - a reader with a deliberately near-free `IsDBNull`, so nothing here is borrowed from an
expensive reader:

| Change | Audit (17/25 nullable) | Investigation (78/85) | Attachment (59/61) |
|---|---:|---:|---:|
| Getter written inline rather than called through `Func<>` | -3.5% to -20% | -6% to -11% | -7% to -13% |
| Nullability resolved at schema time, all columns non-nullable | -15% | -11% | -12% |
| Per-cell `try`/`catch` guard (**rejected**) | +14% | +4% | +8% |

The saving from the nullability split scales with the fraction of columns that are **non-nullable**, so it is large on
a table of mostly required columns and small on these three shapes as they actually are. It also exists only when the
table's nullability is accurate: loading with `inferNullability: false` into a table with no pre-declared schema makes
every column nullable, and every binding takes the tested path.

### 2.6 The row loop keeps `i < array.Length` and a local for the delegate

**What was considered.** Hoisting the array length into a local (`Int32 bindingCount = fieldBindings.Length;`) and
invoking the delegate straight out of the array (`fieldBindings[i](reader, rowIndex);`) rather than through a local.

**What was measured.** Nothing, within noise. Across the three shapes the hoisted-length arm came out 2-5% *faster*
when it ran second and 1-3% *slower* when the arm order was reversed - the classic signature of ordering bias rather
than of a real effect.

**Why it is not done.** `i < array.Length` in the loop condition is the pattern the JIT matches to eliminate the
bounds check on `array[i]`; hoisting the length into a local can only make that recognition harder, never easier. The
local for the delegate is a `stloc`/`ldloc` pair that copy propagation removes before register allocation, so it costs
nothing and reads better. Both forms then perform exactly the same work - and that work is dominated by the calls
inside the delegate, which is where 2.5 found the time.

### 2.7 Runtime code generation was prototyped, measured, and left out

**What was considered.** Emitting the whole load - the row loop included - as one expression tree and compiling it, so
that a row costs one indirect call into straight-line typed code instead of one indirect call per column. The
prototype lives in `benchmarks/ColumnStore.Data.Benchmarks/CompiledRowLoaderPrototype.cs` and is runnable:

```
dotnet run -c Release -- codegen
```

It is written in the only shape that could ever be cached - the column handles are resolved out of the table
parameter into locals in the prologue, outside the row loop, rather than captured as constants - because capturing
them would bind the compiled delegate to one `DataTable` instance and make it single-use.

**What was measured.** Against the *original* loader, compiling was worth 10-33% of the row loop. Against the loader
as 2.5 leaves it, almost all of that has gone:

| Shape | Bound delegates | Compiled loader | Change |
|---|---:|---:|---:|
| Audit, 50,000 rows | 6,384 us | 5,720 us | -10.4% |
| Investigation, 50,000 rows | 40,785 us | 39,729 us | -2.6% |
| Attachment, 50,000 rows | 28,843 us | 28,209 us | -2.2% |

Most of what code generation was buying was the getter delegate hop, and 2.5 removed that without generating any code.
What is left has to pay for `Expression.Compile`:

| Shape | Columns | Bind columns | Compile loader | Break-even rows, uncached |
|---|---:|---:|---:|---:|
| Audit | 24 | 1.4 us | 11,978 us | 924,000 |
| Investigation | 85 | 5.3 us | 15,565 us | 3,366,000 |
| Attachment | 66 | 3.9 us | 14,443 us | 2,199,000 |

**Why it is not in the library.** A single load would have to move roughly a million rows before compilation repaid
itself, so it can only pay behind a cache - and the cache is where the real cost sits: a bounded schema-keyed store
with an eviction policy, a non-codegen fallback for NativeAOT and anywhere else `Expression.Compile` cannot emit, a
duplicate of every loader test to keep the two paths behaviourally identical, opaque dynamic-method frames in any
stack trace, and 12-15 ms added to the first load of each distinct schema. That is a large, permanent increase in
surface area for a few percent of a loop that is itself a small fraction of a database round trip. The prototype and
its benchmark are kept so the decision can be revisited against evidence rather than re-argued.

### 2.8 The System.Data compatibility layer: what is honoured, what is refused, and why nothing is silent

**Requirement.** Code written against `System.Data.DataTable` should compile and behave the same after changing a
`using` directive. Column removal and `DataSet`, both out of scope in v1, are now in scope.

**What was implemented.**

- `DataColumn` is now a non-generic abstract base that `DataColumn<T>` derives from, because System.Data code names
  `DataColumn` constantly and a library whose only column type is `DataColumn<T>` cannot be dropped in at all.
  `IDataColumn` survives unchanged for the ordinal-driven consumers, and the typed handle is still the same object,
  so a partial port - hot loops typed, everything else left alone - addresses one set of storage.
- `IDataColumn.Name` became `ColumnName` and `AllowNull` became `AllowDBNull`, matching System.Data's spelling.
- The row `Object` indexers and `ItemArray` now report a null cell as `DBNull.Value`. The COLUMN layer still uses
  null internally, because that is what its own typed paths already produce; the translation happens once, at the
  surface where compatibility is the point.
- Lookup failures match exactly: `Columns["missing"]` returns null, `Columns[99]` throws `IndexOutOfRangeException`,
  `IndexOf`/`Contains` tolerate a null name. These are not the choices a library designed from scratch would make,
  and they are copied deliberately, because ported code tests for them.
- `DataColumnCollection.Clear()` drops the COLUMNS, as System.Data's does; `DataTable.Clear()` is what empties rows.
- Column removal, `SetOrdinal` and renaming are implemented, with ordinals renumbered across the schema.
- `DataSet` and `DataTableCollection` are implemented as a CONTAINER: naming, add/remove, lookup, `Clone`, `Copy`,
  `Clear`, `Reset`.

**Why the schema stopped being append-only, and what that costs.** The original rule existed because a caller may
hold a cached column handle, and removing a column from underneath it has no safe meaning. Removal as implemented
DETACHES the column instead of destroying it - `Table` becomes null, `Ordinal` becomes -1, the data stays - so the
cached handle keeps working. What genuinely breaks is a cached ORDINAL: after a removal it silently addresses a
different column, with no error possible. The library's own rule is therefore **cache the handle, never the
ordinal**, and every internal ordinal-driven consumer re-reads the schema at the start of each operation rather than
holding ordinals across one. `ColumnRemovalTests.CachedHandleSurvivesRemovalButCachedOrdinalDoesNot` pins both
halves of that, including the silent failure, so it is discovered here rather than in a caller's production data.

**Full assignment compatibility, and what it is built on.** `DataRow` is a readonly struct - that is where the
memory saving lives (NFR-1) - and C# only allows assignment through a property whose receiver is a *variable*, which a
by-value struct return is not. `DataRowCollection.this[Int32]` therefore returns **`ref DataRow`**, and every shape
System.Data code assigns a cell in compiles and writes:

```
table.Rows[0]["Name"] = "changed";        table.Rows[0].ItemArray = values;
table.Rows[0]["A"] = table.Rows[1]["A"];  foreach (DataRow row in table.Rows) { row["Name"] = "changed"; }
DataRow row = table.Rows[0];              table.Select()[0]["Name"] = "changed";
```

**The design decision is what the ref points at, and it was settled by a measurement.** A reference needs real
storage. Reusing one scratch field per collection is far cheaper and is WRONG in a way nothing would ever report: in
`rows[0]["Name"] = rows[1]["Name"]` the compiler takes the target's address first and then evaluates the right-hand
side, which overwrites that same slot with row 1 - the write lands in ROW 1 while row 0 is left untouched, silently.
That was reproduced before the design was chosen, and it is pinned by
`DropInCompatibilityTests.CompoundAssignmentBetweenTwoRowsWritesToTheCorrectRow`, which exists precisely to fail if
anyone "optimises" back to a shared slot.

The store is therefore one view slot per physical row, chunked at 1,024 rows (16 KB per chunk) for two reasons: a
million-row table would otherwise allocate a 16 MB array straight onto the large object heap, against NFR-3; and
chunks are never reallocated, so a reference handed out before rows are appended stays valid - pinned by
`ARowReferenceSurvivesLaterAppends`.

**The cost is real, bounded, and opt-in by usage.** The store is allocated lazily on the first `Rows[i]` call and
released by `Clear`, and the library's own internals resolve rows without it, so nothing starts paying on the
caller's behalf. Measured at 1,000,000 rows x 10 columns:

| | Retained | vs `System.Data` (233.5 MB) |
|---|---:|---:|
| Row indexer never used | 69.2 MB | 70.4% saved |
| Row indexer used on every row | 84.5 MB | 63.8% saved |
| Cost of the views | 16.0 bytes/row | |

A table driven by `foreach` and typed column handles - the way the fast path is meant to be written - pays nothing at
all. The alternative designs were rejected on the same evidence: a shared scratch slot is silently wrong, and making
`DataRow` a class costs roughly 32 bytes of gen-0 garbage per row access, turning a zero-allocation `foreach` over a
million rows into ~32 MB of churn.

**Why unsupported members throw rather than no-op.** Every member needing machinery the library deliberately lacks
raises `NotImplementedException` with a message naming an alternative. The alternative - accepting the call and doing
nothing - is strictly worse: a `Select(filter)` returning every row, a `RejectChanges` that rolled nothing back, or a
`Merge` that appended instead of matching would all hand back WRONG ANSWERS where throwing hands back a stack trace
pointing at the line to change. Table events go further and refuse at SUBSCRIPTION, because firing them would put a
delegate check and an allocation on the per-row path while never firing them would leave a caller's audit logic
silently dead; failing at wire-up is the only variant that cannot be missed.

Three members are no-ops and that is exact rather than approximate: `AcceptChanges` has nothing to commit because a
write goes straight into column storage, and `BeginLoadData`/`EndLoadData` have no indexes or constraints to suspend.
`DataSet.HasChanges()` answers false for the same reason, rather than throwing.

**Properties are refused per VALUE, not per member.** `CaseSensitive = false`, `Unique = false`, `MaxLength = -1` and
`EnforceConstraints = false` are all accepted, because assigning a default is not asking for anything; only the value
that would need the missing machinery throws. A blanket refusal would break code that merely restates a default.

**Cost.** Measured after the change, on the Attachment shape at 50,000 rows: 22,253 KB retained against 22,249 KB
before it, and 51.1% below `System.Data.DataTable` - the new per-column fields are the whole difference. The typed
`Get`/`Set` fast path is untouched: it is still non-virtual on a sealed class, because only `GetValue`/`SetValue`/
`IsNull`/`Clear` became overrides. Row creation gained one Boolean test, for the "does any column have a default
value" check that lets a table without defaults skip the per-column pass entirely.

**Tests.** `ColumnRemovalTests`, `DataSetTests` and `DropInCompatibilityTests` - 53 tests, including a deliberately
exhaustive refusal inventory whose job is to be the checklist a port is run against.

### 2.9 Detached rows: any number at once, and the one thing that stays different

**The problem.** `System.Data` code holds as many rows from `NewRow()` as it likes, fills them, and adds the ones it
decides to keep. This library allowed exactly one at a time and threw on the second — and threw again on any
`Rows.Add(values)` or `AddNewRow()` while one was outstanding. Run side by side, the gap was three refusals wide:

```
second NewRow()                         InvalidOperationException
Rows.Add(values) while one is pending   InvalidOperationException
Rows.AddNewRow() while one is pending   InvalidOperationException
```

The cause was not columnar storage. `NewRow()` reserved `NextPhysicalIndex` — the slot the row would *finally*
occupy — without consuming it, so a second call would have handed out the same slot twice. The one-at-a-time rule was
the guard around that.

**The change.** `NewRow()` now *appends* a slot that is tombstoned from the outset, and `Add` clears the tombstone in
place. Everything that makes a detached row invisible — `Count`, the indexer, iteration — is the machinery deletion
already uses, so it costs nothing new. Concretely:

- any number of detached rows, each with its own slot;
- writes still go straight into typed column storage, so there is no boxing and a typed `DataColumn<T>` handle works
  on a detached row exactly as on any other;
- the slot never moves, so a handle taken before `Add` still addresses the right row afterwards;
- a detached row that is never added keeps its tombstone forever, which is precisely the representation of a
  discarded one — `Discard` became bookkeeping rather than an append;
- the other two refusals disappeared entirely.

The only additions were `RowDeletionTracker.MarkAlive` and `AliveRowPrefixIndex.RegisterRevival`, the exact mirrors of
`MarkDeleted` and `RegisterDeletion`: a Fenwick tree is as happy to add as to subtract.

**What stays different, and why it is refused rather than approximated.** A row's position is its slot's position, and
a detached row takes its slot when it is *created*. So rows appear in **creation order**, where `System.Data` uses
**add order**. The two coincide exactly while nothing else joins the table between creating a row and adding it —
which covers the `NewRow`/fill/`Add` loop and "create several, add some in order, abandon the rest" — and diverge the
moment anything joins in between, whether a later detached row added ahead of an earlier one or a plain
`Rows.Add(values)` landing at the tail.

Reproducing add order in place would mean logical order ceasing to equal physical order, and that equality is what the
O(1)-per-row enumeration and the whole index translation are built on. The alternative — giving detached rows a boxed
staging buffer and copying at `Add` — reintroduces boxing on the detached path, stops typed handles working on a
detached row, and leaves the caller's handle pointing at a freed staging slot unless a forwarding entry is kept for
every `NewRow` call ever made. Neither trade is worth it.

So the divergence is **detected exactly** instead: if any visible row already sits at a later slot, this `Add` would
insert ahead of it, and it throws. Nothing is ever silently misordered. `Rows.AddAtEnd(row)` is the exact way out — it
copies the row into a fresh last slot, which *is* `System.Data`'s ordering, using the new non-boxing
`DataColumn.CopyCell`. The message on the refusal names it.

**A correctness fix this exposed.** Writing the side-by-side tests surfaced a documented approximation in the null
model: `highestWrittenRowIndex` said "above me is null", so a row that was appended and left partly unwritten read as
`default(T)` rather than null once a *later* row pushed the watermark past it. Sequential population was exact, but
detached rows make out-of-order writes ordinary. Advancing the watermark now marks every row it jumps over as null,
which closes the case completely and costs nothing where it does not arise — a sequential fill jumps over no rows, so
the marking loop never runs and a nullable column populated in order still allocates no bitmap words at all. The test
that pinned the old approximation said, in its own summary, that it should be deleted rather than repaired if this
ever became exact; it was.

**Tests.** `DataRowCollectionTests` for the lifecycle and the ordering guard, and two side-by-side tests in
`DropInCompatibilityTests` that run the same detached-row script against a real `System.Data.DataTable` and compare —
including both ways of reaching the divergence, each asserting that the library *refuses* where `System.Data` would
have produced a different order, and that `AddAtEnd` then reproduces `System.Data`'s table exactly.

### 2.10 Shipping: .NET Framework 4.8 alongside .NET 8 and .NET 10

**Five APIs stood between the source and .NET Framework**, found by compiling rather than by reading release notes:
`BitOperations` (PopCount, TrailingZeroCount, IsPow2, Log2), `System.Runtime.Intrinsics.X86.Bmi2`,
`RuntimeHelpers.IsReferenceOrContainsReferences<T>`, `HashCode.Combine`, and `Unsafe.SizeOf<T>`. Nothing in the
language was a problem — `ref`-returning indexers, which carry the row-assignment compatibility, are an IL feature and
compile for net48 unchanged.

**Four of the five are wrapped, not conditionally compiled at the call site.** `Storage/Bits.cs` forwards to the
intrinsics on .NET 8 and .NET 10 and implements them in software on .NET Framework — SWAR population count, lowest-set-
bit isolation, a De Bruijn log2. The call sites in `RowDeletionTracker`, `BitmapColumnStorage` and
`TypedColumnStorage` are identical on every target, and there is exactly one file where "does .NET Framework have
this?" is ever asked. The modern path is an `AggressiveInlining` forward, so the wrapper costs nothing where the
instruction exists. BMI2 is the exception and keeps its own `#if`, because there the fallback is a *different
algorithm* rather than a different implementation of the same one.

`Unsafe.SizeOf<T>` is the one that could not be written out: .NET Framework exposes the managed size of an
unconstrained generic nowhere — `sizeof(T)` is refused and `Marshal.SizeOf` reports unmanaged layout, a different
number. So the net48 build takes exactly one package, `System.Runtime.CompilerServices.Unsafe`; net8.0 and net10.0
take **none**.

**The software fallbacks are tested against independent reference implementations, not against the framework.**
`BitsTests` computes every expectation with a bit-by-bit loop written out in the test file, because comparing against
`BitOperations` would prove nothing on the one framework where it does not exist, and comparing against the operation
under test would prove nothing anywhere. Inputs are exhaustive where they can be — every single-bit word, every power
of two, every bit position — so zero, the lowest bit, the highest bit and the 32-bit boundary the software
`TrailingZeroCount` splits on are covered by construction.

**The whole suite runs on all three frameworks**, which is the only thing that makes net48 support a claim rather than
a hope: 287 tests × 3. One test needed a per-framework bound —
`AsSqlDataRecordsWithADecimalColumnAllocatesOnlyWhatSetDecimalCosts`, where the .NET Framework build of
Microsoft.Data.SqlClient allocates 80 bytes per `SetDecimal` against 40 on .NET 8. Nothing here influences either
figure, and the companion test that demands an *exact zero* per row still passes unchanged on every framework, which
is what actually pins this library's side of the boundary.

**The SQL Server assembly needed no conditional compilation at all** — but only because of which namespace it uses.
`Microsoft.Data.SqlClient` targets net462, so one package reference serves all three frameworks, and
`Microsoft.Data.SqlClient.Server.SqlDataRecord` exists on each. The legacy `Microsoft.SqlServer.Server` spelling would
*not* have worked: on .NET Framework, Microsoft.Data.SqlClient type-forwards it to a `System.Data.SqlClient` assembly
the .NET Framework reference assemblies do not carry, and the compile fails. Measured, not assumed.

**`DataTableLoader` moved into the core assembly.** Nothing in it was ever SQL Server specific — it takes an
`IDataReader` and nothing else — and leaving it in the adapter assembly would have forced every application that
merely wanted `DataTable.Load` to reference a database driver. That move is what lets the core package have no
dependencies at all on .NET 8 and .NET 10.

**Verified as packages, not just as projects.** `dotnet pack` produces both packages with all three `lib/` folders,
and a .NET Framework 4.8 console application consuming them from a local feed exercises table building, every
assignment shape, detached rows, deletion of every third row of a thousand (survivor count and checksum both exact —
which is the software bit operations being right), `DataTable.Load` including null handling, a `Structured` parameter,
and the `DbDataReader` adapter.

### 2.11 `DataTable.Load` and the table-valued parameter surface

**`Load` is the method ported code reaches for**, and it now exists with `System.Data`'s shape: `Load(IDataReader)` and
`Load(IDataReader, LoadOption)`, creating the schema when the table has none and appending against it when it does.

`LoadOption` is **accepted and has no effect**, and that is exact rather than a shortcut. All three options describe how
to reconcile an incoming row with an *existing row of the same primary key*; this library has no primary keys, and
`System.Data.DataTable` appends for every option on a table without one. `DataTableLoadTests` asserts that equivalence
by running both implementations over the same reader and comparing, rather than by asserting what the documentation
says. `LoadRowCount` is the addition: it returns how many rows were appended, which the void-returning signature cannot
and which a table holding tombstones cannot recover from `Rows.Count`.

**For table-valued parameters, SQL Server accepts three value shapes** — `IEnumerable<SqlDataRecord>`, a
`DbDataReader`, and a `System.Data.DataTable`. The third is unavailable by construction, so both of the others are now
first class:

- `AsStructuredParameter(name, tableTypeName, table[, metadata])` returns a ready `SqlParameter` — `SqlDbType.Structured`,
  `TypeName` set, value streaming. This is the recommended route and the fastest one: 1.8× quicker with 5× less garbage
  than the same TVP built from a `System.Data.DataTable`.
- `AsDataReader` now returns a **`DbDataReader`** rather than a bare `IDataReader`, which is what makes it usable as a
  `Structured` value as well as for `SqlBulkCopy` — SQL Server accepts the former and refuses the latter. `DbDataReader`
  already implements `IDataReader`, so nothing that worked before stopped working; the two members it adds are a row
  count and a delegation to ADO.NET's own `DbEnumerator`.

The trade between them is visible rather than hidden: a reader makes SQL Server infer the TVP's shape from the reader's
schema, where records carry the metadata the caller declared. That is why the parameter helper is built on records.

## 3. Behaviours the design left open, and what was chosen

| Question | Choice | Reasoning |
|---|---|---|
| Deleting an already-deleted row (§3.3: "pick one behavior, document it") | **No-op, idempotent** | Predicate-driven cleanup over possibly-stale row handles is a normal pattern, and unlike the row-commit protocol there is no state a repeated delete can corrupt. Tested in `RowDeletionTrackerTests.MarkDeletedTwiceIsIdempotent` and `RowDeletionIntegrationTests.DeleteTwiceIsANoOp`. |
| A detached or discarded row's physical slot | **Appended as an already-tombstoned slot** | Keeps physical/logical accounting in exactly one place rather than adding a second "not a row yet" concept every translation would have to know about, and makes a detached row invisible using the machinery deletion already uses. `Add` clears the tombstone **in place**, so the row never moves and a handle taken before `Add` still addresses it afterwards. The slot is never reused, so a stale handle from a discarded attempt can never write into a later row. |
| How many rows may be detached at once | **Any number** | See §2.9. |
| `AddNewRow` / `Add(params Object[])` while rows are detached | **Allowed** | Detached rows hold their own tombstoned slots, so a direct append cannot collide with one. The restriction this replaces existed only because `NewRow` used to reserve the *next* slot without consuming it. |
| Deleting a detached row | **Throws, pointing at `Discard`** | The row is not part of the table, so there is nothing to delete; the caller means `Discard`. `System.Data` permits `DataRow.Delete()` here and does nothing observable with it — refusing says which of the two operations was meant. |
| `SetValue` with a convertible value of another type | **Converted, using the invariant culture** | `System.Data.DataTable` converts, and positional population with literals (`Rows.Add(42)` into an `Int64` column) is a common migration pattern. A failed conversion produces a column-named `ArgumentException`, never a bare `InvalidCastException` (NFR-12). |
| `Add<T>` with `T = Nullable<U>` | **Refused, with the correct call in the message** | A `DataColumn<Int32?>` would double the value array's width and add a second layer of null tracking over the bitmap that already exists. The `Type`-based overload unwraps `typeof(Int32?)` instead of refusing it, since that is what a caller reading a schema naturally passes. |
| Column added after rows exist | **Allowed; existing rows read null (nullable) or `default(T)`** | A loader that discovers a field mid-stream is legitimate. Documented rather than blocked. |
| Column name comparison | **Case-insensitive (`OrdinalIgnoreCase`)** | Matches `System.Data.DataTable`'s default (NFR-8). |
| `Rows.Clear()` called directly | **Also releases column data** | A row-count reset without a data release would let a previous row's values surface in a newly added row. Both entry points (`Table.Clear`, `Rows.Clear`) route through one implementation so they cannot diverge. |

---

## 4. Empirical results (NFR-4)

NFR-4 asks for the memory saving to be *"validated empirically, not treated as a hard contractual
number"*, with a target in the same order of magnitude as 75–85 MB saved per million rows.

Measured by `benchmarks/ColumnStore.Data.Benchmarks` — 10 columns (`Int32`, `Int64`, `Decimal`,
`Double`, `DateTime`, `Boolean`, `String`, `Guid`, `Int16`, `Byte`), .NET 10, workstation GC, x64,
24 cores, storage chunk size at the current default of 128 rows. Every timing is the average of as
many repetitions as fit into a quarter-second of measured work, after a warm-up; a single Stopwatch
reading would be timer noise at the small row counts.

### 4.1 At the representative one million rows

| Measure | ColumnStore.Data | `System.Data.DataTable` | Ratio |
|---|---:|---:|---:|
| Retained memory | 70.3 MB | 541.0 MB | 7.7× less |
| Bytes retained per row | 73.7 B | 567.3 B | 7.7× less |
| Garbage allocated while building | 71.2 MB | 904.0 MB | 12.7× less |
| Load from `IDataReader`, new table | 525 ms | 5,261 ms | 10.0× faster |
| Load from `IDataReader`, existing schema | 425 ms | 6,305 ms | 14.8× faster |
| Insert, each side's fastest hand-written path | 306.9 M cells/s | 2.2 M cells/s | 139× faster |
| Sequential read, typed column handles | 307.2 M cells/s | 52.4 M cells/s | 5.9× faster |
| Sequential read, `Object` indexer both sides | 125.0 M cells/s | 66.6 M cells/s | 1.9× faster |
| Scattered single-column read | 213.8 M cells/s | 1.6 M cells/s | 138× faster |
| Remove first tenth of rows (100k-row table) | 176 us | 54,299 us | 309× faster |
| Iterate after every tenth row removed | 353.1 M rows/s | 52.1 M rows/s | 6.8× faster |

**Against the target: 471 MB saved per million rows, against a target of 75–85 MB.** The target was
conservative: it appears to account for the per-row `DataRow` object and its `Object[]`, but not for
the boxes held by that array's value-typed cells, which on this schema are the larger term.

### 4.2 Across row counts

Ratios are the factor by which ColumnStore.Data is better; values below 1× are cases where it is worse.

| Scenario | 100 rows | 1,000 | 10,000 | 100,000 | 1,000,000 |
|---|---:|---:|---:|---:|---:|
| Retained memory | 7.40× | 8.43× | 8.38× | 7.95× | 7.70× |
| Bytes/row (lite vs framework) | 125 vs 924 | 78 vs 656 | 75 vs 631 | 74 vs 589 | 74 vs 567 |
| Garbage while building | 10.1× | 13.0× | 13.7× | 13.1× | 12.7× |
| Load from reader, new table | 5.25× | 8.15× | 10.26× | 10.23× | 10.03× |
| Load from reader, existing schema | 5.93× | 11.55× | 8.58× | 12.92× | 14.83× |
| Insert, each side's fastest path | 77.0× | 80.6× | 116× | 124× | 139× |
| Sequential read, typed handles | 3.52× | 3.61× | 5.39× | 5.78× | 5.87× |
| Sequential read, `Object` indexer | **0.66×** | 1.44× | 2.16× | 2.32× | 1.88× |
| Scattered single-column read | 5.95× | 8.44× | 25.3× | 90.4× | 138× |
| Remove first tenth of rows | 143× | 298× | 269× | 269× | 309× |
| Iterate after deletions | 3.09× | 5.04× | 7.91× | 10.8× | 6.77× |

### 4.3 The one place ColumnStore.Data is still worse

**`Object`-indexer reads below roughly 1,000 rows.** `System.Data.DataTable` stores its cells
pre-boxed in an `Object[]`, so `row[0]` returns a box that already exists and the read allocates
nothing; it paid for the box at write time. ColumnStore.Data stores values unboxed, so the same call has
to box on the way out. On a table small enough to sit in cache the framework's pre-boxed array wins
(0.66× at 100 rows); from about a thousand rows the columnar layout's locality more than pays the
boxing back, reaching 1.9–2.3× at scale. The typed column handle avoids the boxing altogether and is
faster at every size, from 3.5× at 100 rows to 5.9× at a million — which is why the documentation
ranks it first.

The small-table MEMORY penalty reported in earlier revisions of this document — 2,983 bytes per row
at 100 rows, versus `System.Data`'s 924 — is gone. It was entirely an artefact of the chunk size, and
the default is now 128 rows rather than the 16–64 KB figure. See §4.5.

### 4.4 Measurement conditions

So the numbers can be reproduced and criticised:

- **The load comparison is the apples-to-apples one.** Both sides are handed the same
  `System.Data.DataTableReader` over the same rows and asked to fill a table through their own public
  API — `DataTableLoader.Load` against `System.Data.DataTable.Load`, the framework's own bulk reader
  ingestion method. Whatever the source reader costs, it costs both equally and cancels out. A manual
  `while (reader.Read())` loop with per-cell `GetValue` calls would be slower still on the framework
  side, and beating that would prove less.
- The `Insert` row, by contrast, uses each side's fastest hand-written population path: typed column
  handles for ColumnStore.Data, `BeginLoadData` with positional `Object[]` for `System.Data`. That
  flatters the columnar side, which is why the load comparison is reported alongside it rather than
  instead of it.
- Both tables draw their `String` values from the same 128-entry pool. A million distinct strings
  costs tens of megabytes of character data in either implementation and would swamp the structural
  difference the benchmark exists to show.
- Retained memory is `GC.GetTotalMemory(true)` after two forced gen-2 collections with finalisers
  drained between them — not process working set, which includes the runtime, the JIT and pages the
  GC has not returned.
- Front-removal is capped at 100,000 rows because physically shifting a larger table one row at a
  time takes minutes per repetition on the framework side. That is why the million-row row of the
  table above reports the same 100,000-row batch.
- Every read scenario checksums both implementations and compares the results, and the load scenario
  checksums both loaded tables, so a benchmark that quietly read or ingested different data fails
  loudly rather than reporting an impressive number.
- The chunk-size sweep compares near-identical variants, where a single measurement disturbed by
  tiered recompilation would invert the comparison. It therefore warms every arm before measuring any
  of them, and reports the fastest of three complete measurements per arm rather than the average.
  Without both of those the read column swings by 50% between structurally identical arms.

Nothing in the benchmark asserts or fails a build — NFR-4 is explicit that these are numbers to be
validated, not a contract. Contracts are enforced in the test suite.

### 4.5 Chunk size: measured, and what the numbers say

`ChunkSizing.DefaultRowCount` is a fixed **128 rows**, a deliberate deviation from NFR-3's 16–64 KB
band (see §2.4). The sweep, at the two extremes of the row-count range:

| Chunk rows | `Int32` chunk | 100 rows: retained / insert | 1,000,000 rows: retained / insert / read |
|---:|---:|---:|---:|
| **128** (default) | 0.5 KB | **0.012 MB** / **2.5 us** | 70.27 MB / 33,773 us / 308 Mcells/s |
| 512 | 2 KB | 0.038 MB / 3.3 us | **68.66 MB** / **27,227 us** / **365 Mcells/s** |
| 2,048 | 8 KB | 0.142 MB / 6.0 us | 68.32 MB / 26,529 us / 348 Mcells/s |
| 8,192 | 32 KB | 0.558 MB / 16.9 us | 68.64 MB / 29,073 us / 328 Mcells/s |
| 32,768 | 128 KB | 2.222 MB / 250 us | 69.17 MB / 31,689 us / 325 Mcells/s |

At 10,000 rows and below, 128 is the best or joint-best arm on every measure. At 100,000 and
1,000,000 rows it is the worst or near-worst: against the best arm it costs about **3% more retained
memory, 12% slower sequential reads and 27% slower inserts**, because a million-row column holds
7,813 chunks instead of 123 — more array headers, less contiguity for a scan, and more objects for
the GC to trace.

**On this evidence 512 rows (2 KB chunks) is the better default.** It is within 26 KB of 128's figure
on a 100-row table — 38 KB against 12 KB, both negligible — and it is the fastest arm measured at a
million rows on every axis. The current value of 128 was set by explicit request; changing it is one
constant in `ChunkSizing.cs`, and per-column overrides are available regardless via
`Columns.Add<T>(name, allowNull, chunkRowCount)`.

## 5. Zero-allocation results (NFR-5, NFR-7)

`HotPathAllocationTests` asserts **exactly zero** bytes allocated — not "small" — for each of:

- reading every cell of every row through cached `DataColumn<T>` handles;
- writing every cell of every row through cached `DataColumn<T>` handles;
- setting cells null, testing `IsNull`, and reading through `GetOrDefault`;
- `foreach` over `Rows`, with and without deletions present;
- indexed access into a table with deletions (exercising the Fenwick translation);
- row-level `DataRow.Get<T>`/`Set<T>` by name and by ordinal.

For the I/O adapters:

- **Load:** measured against a table whose storage cost is near zero (four bit-packed `Boolean`
  columns plus one `Int32`), loading allocates only the column storage the rows need — about five
  bytes per row, against the 120+ bytes per row five boxed cells would cost.
- **TVP write:** streaming allocates **exactly zero** bytes per row for `Int32`, `Int64`, `Int16`,
  `Byte`, `Boolean`, `Double`, `Single`, `DateTime`, `Guid` and `String` columns.

**One measured exception, and it is not ours.** A `Decimal` column costs approximately 40 bytes per
row inside `Microsoft.Data.SqlClient`'s own `SqlDataRecord.SetDecimal`, which no calling convention
avoids. This is precisely the "beyond what the target reader/writer API itself requires" clause of
NFR-7. It is asserted as a bound rather than ignored, so a regression on *this* side of the boundary
still fails the test, and it is recorded in `DataTableSqlWriter`'s file header.

---

## 6. Additions beyond the specified surface

Small, additive, and each justified. Nothing here changes a specified behaviour.

| Addition | Reason |
|---|---|
| `DataTableLoader.LoadAsync(DbDataReader, …)` | §4.1 calls an async variant "a reasonable future addition using the same pattern". It reuses the same pre-bound field bindings; only the row fetch is asynchronous, since a fetched row's cells are already buffered. |
| `DataTableSqlWriter.InferSqlMetaData` | The marked convenience §7.3 invites. See §1.3 above. |
| `DataRowCollection.Remove` / `RemoveAt` | Compatibility aliases for `Delete`, matching `System.Data.DataRowCollection`, which materially reduces migration friction (NFR-8). |
| `DataRowCollection.IndexOf(DataRow)` | The inverse of the logical indexer, and the only way for a caller to recover a row's visible position from a handle. Needed because `DataRow.RowIndex` is a physical slot. |
| `DataRowCollection.PhysicalCount` / `DeletedCount`, `HasPendingRow` | Diagnostics. The gap between physical and logical count is the storage a compaction would reclaim; without it, tombstone accumulation is invisible. |
| `ChunkSizing.LohSafeRowCountForElementSize(Int32)` and `DefaultRowCount` | Lets the sizing rule be exercised for every element size from 1 to 4096 bytes, rather than only for the sizes of types that happen to exist. |
| `DataColumnCollection.Add<T>(name, allowNull, chunkRowCount)` and matching storage constructors | §1.4 anticipates "an optional override threaded through `DataColumn<T>`'s constructor path for unusual access patterns". It also lets tests force chunk-boundary conditions without materialising tens of thousands of rows. |
| `BitmapColumnStorage.CountSetBits`, `TypedColumnStorage.ChunkCount`/`ChunkRowCount`/`AllocatedRowCapacity` | Diagnostics and test hooks. They are what let the structural requirements (NFR-2: growth by whole chunks, never one doubling allocation) be asserted structurally rather than inferred from memory measurements. |
| `internal DataColumn<T>.ValueStorage` / `HasValueFlags` | The test hook §6.2 asks for, so "every Boolean construction path yields bit-packed storage" is asserted directly. |
| `DataColumnExtensions.GetOrDefault(column, rowIndex, fallbackValue)`, `GetNullable` | Companions to the `GetOrDefault`/`Set(T?)` pair §2.2 specifies. |
| `DataColumnFactory` fast paths for 20 common types | The reflective path is correct but unnecessary for the types that make up practically every real schema. A typical schema is now built with no reflection at all. The FR-3 bit-packing guarantee comes from `DataColumnStorageFactory` and holds on both routes. |
| `internal static class Contract` | The coding standard's §9 requires `Contract.Assert` "or the project's equivalent assertion mechanism" for internal preconditions. `System.Diagnostics.Contracts.Contract.Assert` is a no-op unless the binary is rewritten by the long-unsupported CCRewrite tool, so this is the project's equivalent, named the same way. Assertions stay active in release builds; hot per-cell paths deliberately do not call them. |
| `ToString` overrides on `DataTable`, `DataRow`, `DataColumn<T>` | Test failure messages and debugger inspection. |
| `DataRow : IEquatable<DataRow>` | A row view is a value; two views of the same slot of the same table should compare equal. Without it, `Assert.Equal(rowA, rowB)` would compare by field layout with no defined semantics. |

---

## 7. Requirement traceability

| Requirement | Where satisfied | Where verified |
|---|---|---|
| FR-1 columns by `Type` and by `<T>` | `DataColumnCollection.Add`, `Add<T>` | `DataColumnCollectionTests` |
| FR-2 nullability tracked and enforced | `DataColumn<T>.AllowNull`, `SetNull` | `DataColumnTests` |
| FR-3 bit-packed `Boolean` storage | `DataColumnStorageFactory` (single decision point) | `DataColumnTests.BooleanColumnIsAlwaysBitPacked` |
| FR-4 nullability as one bit per row | `BitmapColumnStorage` as the null side channel | `DataColumnTests.NullableColumnTracksNullabilityWithABitmap` |
| FR-5 retrievable typed column handle | `DataColumnCollection.GetColumn<T>` | `DataColumnCollectionTests.GetColumnReturnsTheSameInstanceTheCollectionHolds` |
| FR-6 atomic and two-step row creation | `Rows.Add(params)`, `AddNewRow`, `NewRow`/`Add` | `DataRowCollectionTests` |
| FR-7 two-step flow cannot corrupt | single `pendingPhysicalIndex`; loud refusals | `DataRowCollectionTests` (five hazard tests) |
| FR-8 `Object` indexer and `Get<T>`/`Set<T>` | `DataRow` | `DataRowTests` |
| FR-9 null read fails fast; separate defaulting accessor | `DataColumn<T>.Get`; `GetOrDefault` | `DataColumnTests`, `DataRowTests` |
| FR-10 `foreach` over rows, deletions transparent | `DataRowCollection.Enumerator` | `DataRowCollectionTests`, `RowDeletionIntegrationTests` |
| FR-11 count, columns, clear-preserving-schema | `DataTable` | `DataTableTests.ClearKeepsTheSchemaAndCachedHandles` |
| FR-12 no SQL Server dependency in core | separate `ColumnStore.Data.SqlServer` assembly | core `.csproj` has no `PackageReference` |
| FR-13 deletion is not O(n) | `RowDeletionTracker.MarkDeleted` | `DeletionBenchmark` (13× faster, and O(log n) vs O(n) by construction) |
| FR-14 count, indexing, iteration reflect deletions | `DataRowCollection` | `RowDeletionIntegrationTests` |
| FR-15 logical/physical translation both ways | `ToPhysicalIndex`, `ToLogicalIndex` | `RowDeletionTrackerTests` (naive cross-check) |
| FR-16 schema and rows from `IDataReader` | `DataTableLoader` | `DataTableLoaderTests` |
| FR-17 dedicated typed getters, fallback only where needed | pre-bound field bindings | `DataTableLoaderTests.LoadEveryDedicatedGetterTypePlusAFallbackRoundTrips` |
| FR-18 repeatable load | schema created only when absent | `DataTableLoaderTests.LoadTwiceAppendsRowsWithoutRecreatingColumns` |
| FR-19 rows as `SqlDataRecord` stream | `AsSqlDataRecords` | `DataTableSqlWriterTests` |
| FR-20 rows as `IDataReader` | `DataTableDataReader` | `DataTableSqlWriterTests` |
| FR-21 no boxing on typed write path | pre-bound typed setters | `AsSqlDataRecordsHotPathAllocatesNothingPerRow` |
| FR-22 nulls as `DBNull` | `SetDBNull`, `GetValue` mapping | `AsSqlDataRecordsNullCellsBecomeDBNull` |
| NFR-1 no per-row allocation | `DataRow` as a `readonly struct` | `HotPathAllocationTests.RowEnumerationAllocatesNothing`; 71.7 B/row measured |
| NFR-2/3 bounded chunks, well under LOH | `ChunkSizing`, `TypedColumnStorage` | `ChunkSizingTests` (LOH-safe rule over every size 1–4096 bytes; default asserted a positive power of two). Default deviates from the 16–64 KB band by design - see §2.4 and §4.5 |
| NFR-4 significantly lower memory | column-first storage | 68.4 MB vs 541.0 MB measured (§4) |
| NFR-5 no allocation on the typed path | whole design | `HotPathAllocationTests` (exact zero) |
| NFR-6 type cost paid once per column/bind | `DataColumnFactory`, pre-bound bindings | structural; `DataTableLoaderTests` allocation test |
| NFR-7 no boxing beyond what the target API forces | pre-bound typed getters/setters | `DataTableLoaderTests`, `DataTableSqlWriterTests` (§5, incl. the `SetDecimal` exception) |
| NFR-8 compatible API shape | naming and signatures throughout | `TableCompatibilityTests` (one suite, both implementations) |
| NFR-9 deviations documented | this document, `README.md`, file headers | — |
| NFR-10 misuse fails loudly | commit/discard protocol | `DataRowCollectionTests` |
| NFR-11 consistent index exceptions | explicit guards in both storages | `TypedColumnStorageTests`, `BitmapColumnStorageTests`, `DataColumnTests` |
| NFR-12 clear type-mismatch errors | column-named messages | `DataColumnTests`, `DataColumnCollectionTests`, `DataRowTests` |

---

## 8. Where to go next

Ordered by value, not effort. None of these is required by v1.

1. **Adopt 512 rows as the default chunk size, or make the first chunks grow progressively.** §4.5
   measures 512 as better than the current 128 at every row count that was swept. The more ambitious
   version is progressive sizing - 64 rows, then 512, then 4,096, then a large-table figure - which
   would beat any single constant at both ends of the range. It is not free: it replaces a single
   shift-and-mask in `TypedColumnStorage.Get`/`Set` with a per-chunk offset lookup, on the hottest
   path in the library, so it must be measured against the fixed-size arms before being committed to.
2. **Physical compaction.** Tombstones accumulate; `Rows.PhysicalCount - Rows.Count` is the storage
   a compaction would reclaim. The honest version invalidates every cached `DataRow` handle, so it
   needs an explicit, documented API (`Compact()` returning an index remapping) rather than being
   done silently.
3. **`DataView`-equivalent projections.** Sorting and filtering without copying data — a natural fit
   for columnar storage, where a sort is a permutation of row indices rather than a move of rows.
4. **Per-column dictionary encoding for `String`.** Low-cardinality string columns are common in
   loaded data and would compress from eight bytes per row plus payload to two or four bytes per row
   plus one shared payload.
5. **`SqlBulkCopy`-shaped write via `DbDataReader`.** `DataTableDataReader` implements `IDataReader`;
   deriving from `DbDataReader` instead would let `SqlBulkCopy` use `GetFieldValue<T>` and remove
   boxing from the paths where it currently falls back to `GetValue`.
6. **Column-level statistics.** Min, max and null count maintained on write would make range
   predicates and null checks answerable without touching the data.
