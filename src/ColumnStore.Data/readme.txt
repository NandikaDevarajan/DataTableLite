ColumnStore.Data - Row / Column layer fundamentals
================================================================================

WHY THIS LIBRARY EXISTS
--------------------------------------------------------------------------------
System.Data.DataTable stores data row-first. Every row is a heap-allocated
DataRow object holding an Object[] of cell values, so every value-typed cell is
boxed. For a 1,000,000 row / 10 column table that is 1,000,000 row objects,
1,000,000 arrays and up to 10,000,000 boxes - hundreds of megabytes of overhead
plus a cast (and a type check) on every single read and write.

ColumnStore.Data keeps the same programming model - a schema of typed columns, row
based CRUD, familiar collection shapes - but stores the data column-first in
typed arrays. There is no per-row object, no Object[] per row and no boxing on
the typed access path.

THE ONE RULE: "SCHEMA-TIME COST, RUNTIME ZERO-COST"
--------------------------------------------------------------------------------
Anything whose cost depends on Type - reflection, Activator.CreateInstance,
multi-way is/as dispatch, delegate creation - happens exactly once, when a
column is created or a schema is bound. Everything that happens per row or per
cell afterwards is a resolved, non-branching operation: an array index, a bit
mask, or a pre-bound delegate call.

Every type in this folder exists to be the place where one piece of schema-time
cost gets paid once and cached as a cheap runtime handle.

WHAT LIVES HERE (Row + Column layers)
--------------------------------------------------------------------------------
  IDataColumn / IDataColumn<T>  The column contract. IsNull lives on the
                                non-generic interface on purpose, so ordinal
                                driven consumers (the SQL loader, the TVP
                                writer, the IDataReader adapter) can test
                                nullability without knowing each column's T.

  DataColumn                    The non-generic base every column derives from,
                                and the type System.Data-shaped code names:
                                "DataColumn c = table.Columns["x"];" has to
                                compile or nothing else about the port matters.
                                Holds the name, ordinal, table back-reference,
                                caption, default value and read-only flag; its
                                header groups every property into honoured,
                                refused, or structural.

  DataColumn<T>                 One typed column: one IDataColumnStorage<T> for
                                the values plus - only when the column is
                                nullable - one BitmapColumnStorage holding one
                                null bit per row. A SET bit means the cell IS
                                NULL, so writing a value normally touches the
                                bitmap not at all, and a nullable column that
                                never receives a null never allocates a single
                                bitmap word. Factory-constructed only, so bool
                                columns can never accidentally be built on
                                byte-per-row storage.

  DataColumnFactory             The single place where a runtime Type becomes a
                                DataColumn<T>. The only reflection in the
                                library, paid once per column.

  DataColumnCollection          The schema. Add<T>/GetColumn<T> hand back a
                                strongly typed handle so a hot loop resolves the
                                column once, outside the loop.

  DataRow                       A readonly struct of (table, physicalRowIndex).
                                Never heap-allocated, never stored, synthesized
                                fresh every time one is handed out. This is
                                where the bulk of the memory saving comes from.

  DataRowCollection             Row lifecycle: create detached rows, add,
                                discard, delete, iterate. Owns the detached-row
                                bookkeeping that makes NewRow()/Add() safe and
                                the deletion tracker that makes logical deletion
                                invisible to callers.

  DataTable                     The facade, including Load(IDataReader) - the
                                System.Data-shaped way to fill a table from any
                                ADO.NET reader. Deliberately free of any SQL
                                Server dependency; WRITE adapters (TVP, bulk
                                copy) live in their own assembly and depend
                                inwards on this one.

  DataTableLoader               The engine behind DataTable.Load: one pre-bound
                                delegate per field, built once per load, so the
                                per-row loop does nothing but invoke them. Lives
                                here rather than in the SQL Server assembly
                                because it needs only IDataReader - which is what
                                lets Load exist without a database driver.

  DataSet /                     A named container for several tables, and the
  DataTableCollection           collection behind it. A CONTAINER, not a
                                relational engine: naming, adding, removing,
                                lookup, Clone, Copy and Clear all work;
                                Relations, Merge and the XML surface refuse.

THE FAST PATH, IN ORDER OF PREFERENCE
--------------------------------------------------------------------------------
  1. DataColumn<T> handle resolved once, then Get/Set per row.  <- fastest

         DataColumn<Int32> quantity = table.Columns.GetColumn<Int32>("Quantity");
         for (Int32 i = 0; i < table.Rows.Count; i++)
         {
             total += quantity.Get(i);
         }

  2. DataRow.Get<T>/Set<T>. One column lookup plus one interface cast per call.
     Convenient, allocation free, but not free.

  3. DataRow's Object indexer. Boxes value types - it exists for
     System.Data.DataTable source compatibility, not for hot loops.

PORTING FROM System.Data: CHANGE THE using, AND THAT IS THE PORT
--------------------------------------------------------------------------------
Every shape System.Data code assigns a cell in compiles and works:

    table.Rows[0]["Name"] = "changed";       // ref-returning indexer
    table.Rows[0].ItemArray = values;
    table.Rows[0]["A"] = table.Rows[1]["A"]; // exact - see below
    foreach (DataRow row in table.Rows) { row["Name"] = "changed"; }
    DataRow row = table.Rows[0]; row["Name"] = "changed";
    table.Select()[0]["Name"] = "changed";

DataRow is a readonly struct - that IS the memory saving - and C# only allows
assignment through a property whose receiver is a VARIABLE, which a by-value
struct return is not. So Rows[i] returns "ref DataRow".

WHAT THE ref POINTS AT, AND WHY IT IS NOT ONE SHARED SLOT. A ref needs somewhere
real to point. Reusing a single scratch field per collection is the cheap choice
and it is WRONG in a way nothing reports: in

    rows[0]["Name"] = rows[1]["Name"];

the compiler takes the target's address first, then evaluates the right-hand
side, which overwrites that same slot with row 1 - so the write lands in ROW 1
and row 0 is left untouched, silently. Measured, not theorised. The store is
therefore one view slot per row, chunked at 1,024 rows (16 KB) so it never
reaches the large object heap and so chunks are never reallocated, which keeps
any reference handed out earlier valid however many rows are appended after it.

WHAT IT COSTS, AND ONLY IF YOU USE IT. The store is allocated lazily on the first
Rows[i] call and released by Clear. A table that never indexes a row - one driven
by foreach and typed column handles - pays NOTHING. Measured on 1,000,000 rows x
10 columns:

    indexer never used   69.2 MB      (70.4% under System.Data)
    indexer on every row 84.5 MB      (63.8% under System.Data)
    the views            16.0 bytes/row

The library's own internals never touch the store, so nothing starts paying for
views on your behalf.

BEHAVIOUR THAT NOW MATCHES System.Data EXACTLY
--------------------------------------------------------------------------------
  * A null cell reads back as DBNull.Value through the Object indexers and
    ItemArray, and writing null or DBNull.Value makes a cell null.
  * Columns["missing"] returns NULL; Columns[99] throws IndexOutOfRangeException;
    IndexOf and Contains tolerate a null name. Same for DataSet.Tables.
  * Columns.Clear() drops the COLUMNS. It is DataTable.Clear() that empties the
    rows and keeps the schema. (Easy to confuse; both are pinned by tests.)
  * Column removal, SetOrdinal and renaming all work, and ordinals renumber.
  * DataColumn.ToString() returns the column name, nothing else.

DEVIATIONS THAT REMAIN (all deliberate, all documented)
--------------------------------------------------------------------------------
  * ORDINALS MOVE when a column is removed or reordered. Cache the HANDLE
    (DataColumn<T>), never the ordinal - a stale ordinal silently addresses a
    different column, and nothing can warn you.
  * ROWS APPEAR IN THE ORDER NewRow() CREATED THEM, not the order they were
    added. Any number of rows may be detached at once, exactly as System.Data
    allows, and writes to them land in column storage immediately - which is why
    a detached row holds a storage slot from the moment it is created, and why
    its slot decides where it appears. The two orderings coincide while nothing
    else joins the table between creating a row and adding it (the usual
    create/fill/add loop, and "create several, add some in order, abandon the
    rest"). Any Add that WOULD reorder the table throws instead; call
    Rows.AddAtEnd(row) to copy the row into a new last row, which reproduces
    System.Data's ordering exactly. A detached row that is never added costs its
    storage slot and nothing else; Rows.DetachedRowCount reports how many there
    are.
  * Deletion is logical. A deleted row is tombstoned, never physically shifted,
    and its physical slot is never reused - so Rows.InsertAt cannot work.
  * Not thread safe - single writer, same baseline as System.Data.DataTable.

WHAT IS REFUSED, AND WHY IT THROWS RATHER THAN NO-OPS
--------------------------------------------------------------------------------
Anything needing machinery this library deliberately does not have throws
NotImplementedException with a message saying what to do instead:

  constraints, primary keys, Unique, AutoIncrement, MaxLength   no index or
                                                                constraint engine
  Select(filter), Compute, DefaultView, DisplayExpression       no expression
                                                                engine
  GetChanges, RejectChanges, CancelEdit, RowError               no per-row
                                                                original values
  Relations, DataSet.Merge                                      no keys to match
                                                                rows on
  ReadXml / WriteXml / GetXml                                   no serialiser
  table events (RowChanged, ColumnChanged, ...)                 refused at
                                                                SUBSCRIPTION

A member that quietly did nothing would be far worse than one that says so: the
caller would get wrong answers instead of a stack trace pointing at the line to
change. Three members are NO-OPS rather than refusals, because for this library
their answer is exact rather than approximate - AcceptChanges, BeginLoadData and
EndLoadData have nothing to do when writes are already committed and there are
no indexes or constraints to suspend.

See ../../Spec/01_Requirements.md, 02_Architecture.md and 03_Design.md for the
full requirement and design rationale behind every one of these choices.
