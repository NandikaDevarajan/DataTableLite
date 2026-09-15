# ColumnStore.Data — Architecture

This document describes the high-level shape of the system: layers, module boundaries, and dependency rules. It intentionally stays shallow — per-component API details, algorithms, and the test plan live in `03_Design.md`. See `01_Requirements.md` for the why.

---

## 1. Guiding Architectural Principle

**"Schema-time cost, runtime zero-cost."** Every layer below is organized around one rule: anything whose cost depends on `Type` — reflection, `Activator.CreateInstance`, multi-way `is`/`as` dispatch, delegate compilation — happens exactly once, at the moment a column or schema is established. Everything that happens per row or per cell afterward must be a direct, resolved, non-branching operation (an array index, a bit mask, a pre-bound delegate call).

This principle is the reason the system is layered the way it is: each layer exists specifically to be the place where a particular piece of schema-time cost gets paid once and cached as a reusable, cheap runtime handle.

---

## 2. Layer Diagram

```
┌───────────────────────────────────────────────────────────────┐
│  Row Layer                                                     │
│  DataTable · DataRowCollection · DataRow                        │
│  (row-shaped façade; API-compatible with System.Data.DataTable) │
└───────────────────────────────────────────────────────────────┘
                              │ depends on
                              ▼
┌───────────────────────────────────────────────────────────────┐
│  Column Layer                                                   │
│  DataColumnCollection · IDataColumn / IDataColumn<T>             │
│  DataColumn<T> · DataColumnFactory                               │
│  (typed column access; schema-time type resolution)             │
└───────────────────────────────────────────────────────────────┘
                              │ depends on
                              ▼
┌───────────────────────────────────────────────────────────────┐
│  Storage Layer                                                  │
│  IDataColumnStorage<T>                                          │
│    ├─ TypedColumnStorage<T>   (chunked T[] growth)               │
│    └─ BitmapColumnStorage     (ulong words — bool + null bits)   │
│  ChunkSizing (chunk-size calculation helper)                     │
│  RowDeletionTracker (tombstone bitmap + index translation)       │
└───────────────────────────────────────────────────────────────┘

┌───────────────────────────────────────────────────────────────┐
│  I/O Adapter Layer (depends on Row + Column layers; optional)   │
│  DataTableLoader      (IDataReader → DataTable)                  │
│  DataTableSqlWriter   (DataTable → SqlDataRecord / IDataReader)  │
└───────────────────────────────────────────────────────────────┘
```

---

## 3. Layer Responsibilities

### 3.1 Storage Layer
Owns raw, untyped-to-the-outside-world data representation: chunked typed arrays, bit-packed booleans/nulls, and the deletion tombstone bitmap with its physical/logical index math. Nothing in this layer knows about column names, table schemas, or row semantics — it only knows "index in, value out" and "index in, value stored."

This is where NFR-1 through NFR-4 (memory shape, chunking, LOH avoidance) are satisfied.

### 3.2 Column Layer
Owns the mapping from a column's declared `Type` to a concrete storage implementation, and owns null-tracking per column. This is where the *only* per-column reflection/dispatch cost is allowed to live (`DataColumnFactory`, generic-type-parameter resolution in `DataColumnCollection.Add<T>`/`GetColumn<T>`). Once a `DataColumn<T>` handle exists, every operation on it is a direct, non-reflective call.

This is where NFR-5, NFR-6, and NFR-12 (zero-cost typed access, one-time schema cost, clear type-mismatch errors) are satisfied.

### 3.3 Row Layer
Owns the row-shaped, `DataTable`-compatible façade: `DataTable`, `DataRowCollection`, `DataRow`. This layer is responsible for translating "row" concepts (add, get, iterate, delete) into column-layer operations at a specific row index, and for the bookkeeping that makes the two-step row-creation flow safe (§4.2/FR-7 in requirements) and that makes logical deletion transparent to callers (§4.4/FR-13–15).

This is where NFR-8, NFR-9 (compatibility, documented deviations) and NFR-10, NFR-11 (safe failure modes) are satisfied.

### 3.4 I/O Adapter Layer
Bridges the Row/Column layers to ADO.NET (`IDataReader` in, `SqlDataRecord`/`IDataReader` out). This layer is explicitly **optional** and must not be a dependency of the core Row/Column/Storage layers — the core library has no SQL-Server-specific package reference. Adapters depend inward on the core layers; the core layers never depend outward on adapters.

This is where FR-16 through FR-22 and NFR-7 are satisfied.

---

## 4. Dependency Rules

1. **Storage layer has zero outward dependencies.** It doesn't know about `IDataColumn`, `DataTable`, or ADO.NET. It's testable entirely in isolation with raw indices and values.
2. **Column layer depends only on Storage.** `DataColumn<T>` composes one `IDataColumnStorage<T>` (and, if nullable, one `BitmapColumnStorage`). It doesn't know about rows or tables.
3. **Row layer depends only on Column** (and, for deletion, on the storage layer's `RowDeletionTracker` directly, since deletion is a row-lifecycle concern that happens to be implemented with a storage-layer primitive). It doesn't know about ADO.NET.
4. **I/O Adapter layer depends on Row + Column layers**, plus whatever ADO.NET/SQL Server types it bridges to. Nothing in Storage, Column, or Row layers may reference `System.Data.IDataReader`, `Microsoft.Data.SqlClient`, or any adapter-layer type.
5. **No layer depends "downward" through a skipped layer for anything but read access to already-resolved handles.** E.g., the Row layer may hold a `DataColumn<T>` reference and call `.Get`/`.Set` on it directly (this is the intended fast path, not a violation) — but it must not reach past the Column layer into `IDataColumnStorage<T>` directly, since that would bypass null-tracking.

This dependency direction is what keeps the core library free of a SQL Server package reference (NFR / FR-12) and keeps the storage layer independently testable and reusable if, e.g., a different I/O adapter (bulk CSV export, a different database) is added later.

---

## 5. Cross-Cutting Concerns

- **Error handling shape.** All layers use exceptions (not error codes/`bool` returns) for misuse (bad index, wrong type, protocol violation on row commit). Exception messages should be specific enough to act on (which column, which index, which expected type) — this is a cross-cutting requirement (NFR-10–12), not owned by any one layer.
- **No layer introduces locking.** Single-writer assumption holds throughout (see Non-Goals in requirements); this keeps every layer's hot path free of synchronization overhead.
- **Extensibility seam for future storage types.** `IDataColumnStorage<T>` is the seam if, e.g., a future sparse-column storage or a memory-mapped storage is wanted — it plugs into the Column layer without the Row layer or I/O adapters needing to change.

---

## 6. Suggested Build Order

Build bottom-up, since each layer's tests depend on the layer below being correct and stable:

1. **Storage layer**: `IDataColumnStorage<T>`, `TypedColumnStorage<T>`, `ChunkSizing`, `BitmapColumnStorage`, `RowDeletionTracker` (build and test the deletion index-translation algorithm in isolation here — it's the most algorithmically subtle piece and has no dependency on anything above it).
2. **Column layer**: `IDataColumn`/`IDataColumn<T>`, `DataColumn<T>`, `DataColumnFactory`, `DataColumnCollection`.
3. **Row layer**: `DataRow` (struct), `DataRowCollection` (including the pending-row commit/discard contract), `DataTable`. Write the row-hazard tests first (see `03_Design.md` Test Plan §Row Layer) — they define the contract.
4. **I/O Adapter layer**: `DataTableLoader`, then `DataTableSqlWriter`.
5. **Compatibility/benchmark suite** last, once the API surface across all layers is stable.
