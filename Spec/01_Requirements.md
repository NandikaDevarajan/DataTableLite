\# ColumnStore.Data — Requirements



\*\*Namespace:\*\* `ColumnStore.Data` (alias: `ColumnStore.Data`)

\*\*Language/Runtime:\*\* C# / .NET



This document defines \*what\* ColumnStore.Data must do and \*why\*, independent of how it's implemented. It should stay stable across implementation iterations — when the design changes, this file should rarely need to.



\---



\## 1. Problem Statement



`System.Data.DataTable` stores data row-first: each row is a heap-allocated object, and cell values are frequently boxed. For large in-memory tables, this costs significant memory (one object per row, plus boxing overhead per value-typed cell) and CPU (boxing/unboxing, type checks per access).



ColumnStore.Data exists to provide the same programming model — schema of typed columns, row-based CRUD, familiar collection shapes — while storing data column-first in typed arrays, eliminating per-row objects and per-cell boxing, so it can be adopted as a near drop-in replacement.



\---



\## 2. Goals



1\. \*\*Column-oriented storage.\*\* Data lives in typed arrays per column, not as `object\[]` per row.

2\. \*\*Zero boxing/unboxing on the hot path.\*\* Reading or writing a cell through the typed API must never box a value type or require a runtime type check per call. Reflection, type dispatch, or delegate compilation must happen once per column or per schema-bind — never per row.

3\. \*\*Drop-in compatibility.\*\* A developer using `System.Data.DataTable` should be able to switch to `ColumnStore.Data.DataTable` via a namespace change and get the memory/perf benefits without rewriting business logic, for the portion of the API surface both types share.

4\. \*\*Fast strongly-typed row access.\*\* Beyond the compatible `object`-based indexer API, expose generic `Get<T>`/`Set<T>` at the row level, and generic typed-column access at the collection level, for callers who want to avoid per-call type resolution entirely.

5\. \*\*Efficient SQL Server I/O in both directions:\*\*

&#x20;  - \*\*Read:\*\* loading from `IDataReader`/`SqlDataReader` must use the reader's typed getters (`GetInt32`, `GetDateTime`, etc.) directly into typed column storage, not the boxing `GetValue` path, except for column types with no dedicated getter.

&#x20;  - \*\*Write:\*\* sending a `ColumnStore.Data.DataTable` to SQL Server as a table-valued parameter (TVP) or via bulk copy must populate `SqlDataRecord`/`SqlBulkCopy` inputs directly from typed column storage, with the same boxing-avoidance discipline.

6\. \*\*Logical deletion.\*\* Deleting a row must not physically shift or copy the storage of every subsequent row. Deleted rows are marked, and a translation function computes the "real" (physical) storage index from a "logical" (visible, post-deletion) row position.



\## 3. Non-Goals (v1)



\- Thread safety / concurrent writers. Single-writer, same baseline guarantee as `System.Data.DataTable`.

\- Column removal after data has been written (schema is append-only for v1).

\- Full `DataTable` feature parity: constraints, relations, `DataSet`, XML serialization, change-tracking events (`RowChanged`, `RowState`, `AcceptChanges`).

\- Multi-table relations.



\---



\## 4. Functional Requirements



\### 4.1 Schema \& Columns

\- FR-1: Callers can add columns by name and `Type` (object-based, for parity with `DataTable.Columns.Add`), and by generic type parameter (`Columns.Add<T>(name, allowNull)`), receiving back a strongly-typed column handle.

\- FR-2: Each column tracks whether it allows null values; setting a value on a non-nullable column to null must fail clearly.

\- FR-3: Boolean columns must use bit-packed storage (1 bit/row), not one array slot per row.

\- FR-4: Any nullable column, regardless of value type, must track nullability via a bit per row rather than a wrapper object or in-band sentinel value.

\- FR-5: Callers must be able to retrieve a strongly-typed column handle after the fact (by name or index) for repeated, resolution-free access across many rows.



\### 4.2 Rows

\- FR-6: Row creation must support both: (a) an atomic "create and populate" call taking positional values, and (b) a two-step "reserve, then set fields, then commit" flow, matching common `DataTable` usage patterns (`Rows.Add(...)` and `NewRow()`/`Rows.Add(row)`).

\- FR-7: The two-step flow must not allow silent data corruption — the system must detect and reject having more than one reserved-but-uncommitted row at a time, and must provide a way to abandon a reserved row without committing it.

\- FR-8: Rows must expose both an `object`-based indexer (by column index or name) for compatibility, and generic `Get<T>`/`Set<T>` methods for typed access.

\- FR-9: Reading a null cell through the typed, non-defaulting API must fail clearly (not silently return `default(T)`); a separate "get or default" accessor must be available for callers who want `default(T)` instead of an exception.

\- FR-10: Iterating all rows in a table must be supported via a standard iteration construct (`foreach`), respecting deletions (see §4.4) transparently.



\### 4.3 Table

\- FR-11: The table exposes a row count, a column collection, and a way to clear all row data while preserving the column schema.

\- FR-12: The table's core functionality must not require a SQL-Server-specific package dependency — SQL Server I/O is an add-on concern, not a core one.



\### 4.4 Deletion

\- FR-13: Deleting a row must be an O(1)-ish marking operation, not an O(n) shift of subsequent rows' data.

\- FR-14: After deletion, row count, indexed access, and iteration must all reflect the reduced, renumbered set of visible rows, without exposing the deleted row or requiring callers to know about physical storage layout.

\- FR-15: The system must provide (at least internally) a way to translate between a visible ("logical") row position and its underlying ("physical") storage slot, and vice versa.



\### 4.5 SQL Server Read

\- FR-16: The system must be able to populate a table's schema (if not already defined) and rows from an ADO.NET `IDataReader`, inferring column names/types from the reader, and optionally inferring nullability from reader schema metadata when available.

\- FR-17: Loading must use the reader's dedicated typed getter for each column's type wherever one exists, falling back to the generic/boxing getter only for types without a dedicated getter.

\- FR-18: Loading must be repeatable — calling load again against a table that already has a schema must append rows using the existing schema rather than re-creating columns.



\### 4.6 SQL Server Write

\- FR-19: The system must be able to expose a table's (logical, non-deleted) rows as a stream of `SqlDataRecord`s suitable for use as a table-valued parameter.

\- FR-20: The system must be able to expose a table's (logical) rows as a generic `IDataReader`, suitable for `SqlBulkCopy.WriteToServer`.

\- FR-21: Both write paths must avoid boxing on the per-cell, non-null path wherever the target API allows a typed setter.

\- FR-22: Both write paths must correctly represent null cells as `DBNull`/the target API's null convention.



\---



\## 5. Non-Functional Requirements



\### 5.1 Memory

\- NFR-1: The dominant memory saving versus `System.Data.DataTable` must come from eliminating the per-row heap object — a "row" must be representable without a persistent per-row allocation.

\- NFR-2: Column storage must grow in bounded increments (not a single ever-doubling contiguous array), so that:

&#x20; - No single allocation for a column's storage should routinely land on the Large Object Heap (LOH) threshold (\~85,000 bytes) for typical row counts.

&#x20; - Growth cost for adding rows is amortized, not a periodic large copy.

\- NFR-3: Target per-allocation-chunk size: roughly 16–64 KB, to balance allocation count against staying comfortably under the LOH threshold.

\- NFR-4: For a representative large table (e.g. \~1,000,000 rows, \~10 columns of common types), memory usage should be measurably and significantly lower than the equivalent `System.Data.DataTable` (target: same order of magnitude as \~75–85 MB/million rows saved, to be validated empirically, not treated as a hard contractual number).



\### 5.2 Performance

\- NFR-5: Per-row, per-cell read/write through the typed APIs must not perform heap allocation, boxing, or `Type`-based branching in the common (non-null, correctly-typed) case.

\- NFR-6: Any operation whose cost scales with reflection or type resolution (creating a column, binding a schema to a reader) must be paid once per column/schema-bind, never once per row.

\- NFR-7: SQL Server read and write paths must not introduce boxing beyond what the target reader/writer API itself requires for a given column's data type.



\### 5.3 Compatibility

\- NFR-8: The public API shape (type names, method names, common usage patterns) should track `System.Data.DataTable` closely enough that switching a `using`/namespace reference and making minor adjustments is sufficient for common usage patterns (schema creation, row add/iterate, `Clear`).

\- NFR-9: Deviations from `System.Data.DataTable` behavior (e.g. the `NewRow()` commit/discard contract, absence of `RowState`/`AcceptChanges`, append-only schema) must be explicitly documented, not silently different.



\### 5.4 Reliability

\- NFR-10: Misuse of the two-step row-creation flow (§4.2, FR-7) must fail loudly with a clear exception, never silently corrupt data.

\- NFR-11: Out-of-range or negative row/column index access must fail with a clear, consistent exception type across all storage implementations.

\- NFR-12: Type-mismatched access (e.g. requesting a column as the wrong generic type) must fail with a clear, actionable error message identifying the column and the expected vs. actual type.



\---



\## 6. Acceptance Criteria (summary — see `03\_Design.md` §Test Plan for full detail)



\- A benchmark table of \~1,000,000 rows / \~10 columns demonstrably uses substantially less memory than the `System.Data.DataTable` equivalent.

\- An allocation-delta test over typed row/column read and write shows zero boxing allocations on the non-null, correctly-typed hot path.

\- The two-step row creation flow (`NewRow()`/`Add`/`Discard`) cannot produce a state where a row's data is readable through the public API without being counted, or counted without having valid data.

\- Deleting rows in any pattern (start, middle, end, consecutive, spanning internal storage boundaries) leaves indexing and iteration correct and does not corrupt other rows' data.

\- Loading from a mocked `IDataReader` covering all supported typed getters round-trips values and nulls correctly, with zero boxing on the dedicated-getter path.

\- Writing to both TVP and bulk-copy adapters round-trips values and nulls correctly, with zero boxing on the dedicated-setter path.

