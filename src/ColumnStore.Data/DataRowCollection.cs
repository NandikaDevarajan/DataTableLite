///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Row lifecycle: creating detached rows, adding or discarding them, logical deletion, indexed access and
//   allocation-free iteration. This is the only type that can answer "does this storage slot count as a row yet".
// Assumptions: Single-writer access; not thread safe. The collection stores NO DataRow instances - only scalar
//   bookkeeping (the detached-row slots and the deletion tracker). Every DataRow handed out is synthesized on the
//   spot.
// Design Considerations: THE DETACHED ROW CONTRACT (FR-7, NFR-10). Because DataRow.Set writes straight into column
//   storage, a row created by NewRow() needs a real physical slot from the moment any field is set, even though it
//   is not part of the table yet. It gets one: NewRow appends a slot that is TOMBSTONED FROM THE OUTSET, so the row
//   is invisible to Count, the indexer and iteration for free - using the machinery deletion already uses - and Add
//   simply clears the tombstone, making the row visible IN PLACE. Any number of detached rows can therefore exist at
//   once, exactly as System.Data allows, writes still go straight into typed column storage with no boxing, and a
//   row handle taken before Add still addresses the right row afterwards because the slot never moves.
//   THE ONE THING THIS CANNOT REPRODUCE IS ORDER. A row's position is its slot's position and a detached row took
//   its slot when it was created, so Add makes a row visible where it was CREATED; System.Data makes it visible
//   where it was ADDED. The two coincide exactly while nothing else has joined the table since the row was created -
//   the NewRow/fill/Add loop, and "create several, add some in order, abandon the rest" - and diverge the moment
//   anything joins in between, whether a later detached row added ahead of an earlier one or a plain
//   Rows.Add(values) landing at the tail. Every such Add is refused loudly rather than silently misordered, and
//   AddAtEnd is the exact way out: it copies the row into a fresh last slot, which IS System.Data's ordering.
//   Doing that in place instead would mean logical order ceasing to be physical order, which is the assumption the
//   O(1)-per-row iteration and the whole index translation rest on.
//   A detached slot is never reused, whether it was added or abandoned. Reuse would mean a stale DataRow handle from
//   an abandoned attempt could write into a later, unrelated row - trading a little wasted space for a
//   data-corruption class of bug.
//   ITERATION WALKS PHYSICAL SLOTS. Enumerating by logical position would translate an index per row (O(log n) each);
//   walking physical slots and skipping tombstones is O(1) per row and skips whole 64-row words of deleted rows in
//   one step. The enumerator is a manually written struct so foreach allocates nothing at all.
//   DELETION IS IDEMPOTENT. Deleting an already-deleted row is a no-op rather than an exception, so predicate-driven
//   cleanup over possibly-stale handles is safe; unlike the commit protocol there is no state a repeated delete can
//   corrupt. This is the documented choice for 03_Design.md section 3.3's open behaviour.
//   See 03_Design.md section 3.3.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections;
using System.Collections.Generic;

using ColumnStore.Data.Storage;

namespace ColumnStore.Data
{
    /// <summary>
    /// The rows of a <see cref="DataTable"/>. Exposes visible (non-deleted) rows only.
    /// </summary>
    public sealed class DataRowCollection : IEnumerable<DataRow>
    {
        #region Private Constants
        // Rows per chunk of the row-view store. A DataRow is 16 bytes on x64, so 1,024 of them is a 16 KB chunk -
        // comfortably under the 85,000-byte large object heap threshold (NFR-3), and large enough that the chunk
        // list stays short even for a table of millions of rows.
        private const Int32 ROW_VIEW_CHUNK_ROWS = 1024;

        // log2(ROW_VIEW_CHUNK_ROWS), so the chunk and offset come out of a shift and a mask rather than a division.
        private const Int32 ROW_VIEW_CHUNK_SHIFT = 10;

        // Mask that extracts a physical index's offset within its chunk.
        private const Int32 ROW_VIEW_CHUNK_MASK = ROW_VIEW_CHUNK_ROWS - 1;
        #endregion

        #region Private Members
        // The table whose columns these rows address. Needed to synthesize DataRow views and to populate rows by
        // ordinal.
        private readonly DataTable owningTable;

        // Physical slot accounting and tombstones. Owns the physical/logical translation.
        private readonly RowDeletionTracker deletionTracker;

        // Physical slots of the rows created by NewRow() and not yet added or discarded. NULL until NewRow is used
        // for the first time, so a table populated by Add or by the loader never allocates it. A set rather than a
        // list because the only questions asked of it are "is this slot detached" and "stop tracking this slot";
        // in the usual create-fill-add loop it holds one entry at a time and reuses the same bucket.
        private HashSet<Int32> detachedPhysicalIndexes;

        // Backing storage for the row indexer's ref return - one DataRow slot per physical row, in chunks. NULL
        // until the indexer is used for the first time, which is what keeps the cost off tables that never touch it.
        // See the indexer for why a ref return needs real per-row storage and a single shared slot will not do.
        private List<DataRow[]> rowViewChunks;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates the row collection for a table. Internal because a row collection is meaningless without the table
        /// whose columns it addresses; obtain one from <see cref="DataTable.Rows"/>.
        /// </summary>
        /// <param name="table">The owning table.</param>
        internal DataRowCollection(DataTable table)
        {
            Contract.Assert(table != null, "A DataRowCollection must belong to a table.");
            this.owningTable = table;
            this.deletionTracker = new RowDeletionTracker();
        }
        #endregion

        #region Public Properties
        /// <summary>
        /// Number of visible rows: physical slots minus deleted and discarded ones. A row created by
        /// <see cref="NewRow"/> is not counted until it is added.
        /// </summary>
        public Int32 Count
        {
            get { return this.deletionTracker.LogicalCount; }
        }

        /// <summary>Always false; this collection is not read only.</summary>
        public Boolean IsReadOnly
        {
            get { return false; }
        }

        /// <summary>Always false; this collection is not synchronised.</summary>
        public Boolean IsSynchronized
        {
            get { return false; }
        }

        /// <summary>An object usable to synchronise access to the collection.</summary>
        public Object SyncRoot
        {
            get { return this.owningTable; }
        }

        /// <summary>
        /// Number of physical storage slots consumed, including deleted and discarded rows. Diagnostic: the gap
        /// between this and <see cref="Count"/> is the storage that a compaction would reclaim.
        /// </summary>
        public Int32 PhysicalCount
        {
            get { return this.deletionTracker.PhysicalCount; }
        }

        /// <summary>Number of deleted or discarded physical slots.</summary>
        public Int32 DeletedCount
        {
            get { return this.deletionTracker.DeletedCount; }
        }

        /// <summary>True when at least one row created by <see cref="NewRow"/> is still awaiting Add or Discard.</summary>
        public Boolean HasDetachedRows
        {
            get { return DetachedRowCount > 0; }
        }

        /// <summary>
        /// How many rows created by <see cref="NewRow"/> are still detached. Diagnostic: a number that only grows is
        /// a loop creating rows it never adds, each of which permanently consumes a storage slot.
        /// </summary>
        public Int32 DetachedRowCount
        {
            get { return this.detachedPhysicalIndexes == null ? 0 : this.detachedPhysicalIndexes.Count; }
        }

        /// <summary>
        /// The visible row at the given logical position.
        /// </summary>
        /// <param name="logicalIndex">Zero-based position among visible rows.</param>
        /// <returns>A view of that row.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The position is outside the visible rows.</exception>
        public ref DataRow this[Int32 logicalIndex]
        {
            get
            {
                Int32 visibleCount = this.deletionTracker.LogicalCount;
                if (logicalIndex < 0 || logicalIndex >= visibleCount) { throw new ArgumentOutOfRangeException(nameof(logicalIndex), logicalIndex, $"Row index must be between 0 and {visibleCount - 1}."); }
                Int32 physicalIndex = this.deletionTracker.ToPhysicalIndex(logicalIndex);
                return ref ResolveRowView(physicalIndex);
            }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Creates a DETACHED row: one that belongs to this table's schema and can be read and written immediately,
        /// but is not part of the table until it is passed to <see cref="Add(DataRow)"/>. As many detached rows may
        /// exist at once as you like, and a detached row that is simply never added costs its storage slot and
        /// nothing else. <see cref="Discard"/> says so explicitly.
        /// Rows become visible in the order they were CREATED here, not the order they were added - see
        /// <see cref="Add(DataRow)"/>, which refuses rather than misorder.
        /// </summary>
        /// <returns>A view of the detached row.</returns>
        public DataRow NewRow()
        {
            Int32 reservedIndex = this.deletionTracker.AppendDetachedRow();
            if (this.detachedPhysicalIndexes == null) { this.detachedPhysicalIndexes = new HashSet<Int32>(); }
            this.detachedPhysicalIndexes.Add(reservedIndex);
            ApplyColumnDefaults(reservedIndex);
            return new DataRow(this.owningTable, reservedIndex);
        }

        /// <summary>
        /// Adds a row created by <see cref="NewRow"/> to the table, making it and every value already written into it
        /// visible. The row keeps its storage slot, so a handle to it taken before this call still addresses it
        /// afterwards.
        /// </summary>
        /// <param name="row">The row returned by <see cref="NewRow"/>.</param>
        /// <exception cref="ArgumentException">The row is unbound or belongs to another table.</exception>
        /// <exception cref="InvalidOperationException">The row is not detached - it was already added or discarded -
        /// or adding it now would place it before a row that is already in the table.</exception>
        public void Add(DataRow row)
        {
            ValidateDetachedRow(row, "Add");
            RejectAddThatWouldMisorder(row.RowIndex);
            this.deletionTracker.MarkAlive(row.RowIndex);
            this.detachedPhysicalIndexes.Remove(row.RowIndex);
        }

        /// <summary>
        /// Abandons a row created by <see cref="NewRow"/>. Any values already written at its slot become permanently
        /// invisible; the slot is consumed and never reused. Optional - a detached row that is never mentioned again
        /// behaves identically - but it releases the collection's tracking of that row and says the intent out loud.
        /// </summary>
        /// <param name="row">The row returned by <see cref="NewRow"/>.</param>
        /// <exception cref="ArgumentException">The row is unbound or belongs to another table.</exception>
        /// <exception cref="InvalidOperationException">The row is not detached - it was already added or discarded.</exception>
        public void Discard(DataRow row)
        {
            ValidateDetachedRow(row, "Discard");
            this.detachedPhysicalIndexes.Remove(row.RowIndex);
        }

        /// <summary>
        /// Adds a row created by <see cref="NewRow"/> as the table's LAST row regardless of when it was created, by
        /// copying its values into a fresh slot and discarding the original. This is the way to reproduce
        /// <see cref="System.Data.DataRowCollection"/>'s ordering - position decided by when a row was added rather
        /// than when it was created - in the cases <see cref="Add(DataRow)"/> refuses.
        /// The copy is typed and allocation free, but it IS a copy: the returned row is the one in the table, and the
        /// row passed in is left detached and abandoned, so keep the return value rather than the argument.
        /// </summary>
        /// <param name="row">The row returned by <see cref="NewRow"/>.</param>
        /// <returns>A view of the newly appended last row.</returns>
        /// <exception cref="ArgumentException">The row is unbound or belongs to another table.</exception>
        /// <exception cref="InvalidOperationException">The row is not detached - it was already added or discarded.</exception>
        public DataRow AddAtEnd(DataRow row)
        {
            ValidateDetachedRow(row, "AddAtEnd");
            Int32 sourceIndex = row.RowIndex;
            Int32 appendedIndex = this.deletionTracker.AppendAliveRow();
            DataColumnCollection columns = this.owningTable.Columns;
            for (Int32 ordinal = 0; ordinal < columns.Count; ordinal++)
            {
                columns[ordinal].CopyCell(sourceIndex, appendedIndex);
            }
            this.detachedPhysicalIndexes.Remove(sourceIndex);
            return new DataRow(this.owningTable, appendedIndex);
        }

        /// <summary>
        /// Appends a visible, empty row and returns a view over it, in one atomic step. Cells of nullable columns read
        /// as null until written; cells of non-nullable columns read as <c>default(T)</c>.
        /// </summary>
        /// <returns>A view of the new row.</returns>
        public DataRow AddNewRow()
        {
            Int32 appendedIndex = this.deletionTracker.AppendAliveRow();
            ApplyColumnDefaults(appendedIndex);
            return new DataRow(this.owningTable, appendedIndex);
        }

        /// <summary>
        /// Appends a visible row populated positionally from the given values, in one atomic step. This is the
        /// recommended, hazard-free way to add a row. Fewer values than columns leaves the remaining cells unset.
        /// </summary>
        /// <param name="values">Cell values in column order. Null or <see cref="DBNull"/> sets a cell to null.</param>
        /// <returns>A view of the new row.</returns>
        /// <exception cref="ArgumentNullException">The value array is null.</exception>
        /// <exception cref="ArgumentException">More values were supplied than the table has columns.</exception>
        public DataRow Add(params Object[] values)
        {
            if (values == null) { throw new ArgumentNullException(nameof(values)); }
            DataColumnCollection columns = this.owningTable.Columns;
            if (values.Length > columns.Count) { throw new ArgumentException($"The table has {columns.Count} columns but {values.Length} values were supplied.", nameof(values)); }
            Int32 appendedIndex = this.deletionTracker.AppendAliveRow();
            ApplyColumnDefaults(appendedIndex);
            for (Int32 ordinal = 0; ordinal < values.Length; ordinal++)
            {
                DataColumn column = columns[ordinal];
                column.SetValue(appendedIndex, values[ordinal]);
            }
            MarkUnsuppliedColumnsNull(columns, values.Length, appendedIndex);
            return new DataRow(this.owningTable, appendedIndex);
        }

        /// <summary>
        /// Logically deletes a row. Storage is not shifted, copied or reclaimed - the row's slot is tombstoned, the
        /// visible row count drops by one, and later rows renumber automatically. Deleting an already-deleted row does
        /// nothing.
        /// </summary>
        /// <param name="row">The row to delete.</param>
        /// <exception cref="ArgumentException">The row belongs to a different table.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The row's slot is not part of this table.</exception>
        /// <exception cref="InvalidOperationException">The row is detached - add or discard it instead.</exception>
        public void Delete(DataRow row)
        {
            ValidateRowBelongsHere(row);
            if (IsDetachedRow(row.RowIndex)) { throw new InvalidOperationException("This row was created by NewRow and is not part of the table yet, so there is nothing to delete. Pass it to Rows.Discard to abandon it, or to Rows.Add first."); }
            if (row.RowIndex >= this.deletionTracker.PhysicalCount) { throw new ArgumentOutOfRangeException(nameof(row), row.RowIndex, "This row's storage slot is not part of the table."); }
            this.deletionTracker.MarkDeleted(row.RowIndex);
        }

        /// <summary>
        /// Logically deletes a row. Compatibility alias for <see cref="Delete"/>, matching
        /// <see cref="System.Data.DataRowCollection.Remove"/>.
        /// </summary>
        /// <param name="row">The row to delete.</param>
        public void Remove(DataRow row)
        {
            Delete(row);
        }

        /// <summary>
        /// Logically deletes the visible row at the given logical position. Compatibility alias matching
        /// <see cref="System.Data.DataRowCollection.RemoveAt"/>.
        /// </summary>
        /// <param name="logicalIndex">Zero-based position among visible rows.</param>
        /// <exception cref="ArgumentOutOfRangeException">The position is outside the visible rows.</exception>
        public void RemoveAt(Int32 logicalIndex)
        {
            DataRow row = GetRowByValue(logicalIndex);
            Delete(row);
        }

        /// <summary>
        /// True when the given physical slot holds a row created by <see cref="NewRow"/> and not yet added, which is
        /// exactly the state System.Data calls Detached.
        /// </summary>
        /// <param name="physicalRowIndex">A physical storage slot, as reported by <see cref="DataRow.RowIndex"/>.</param>
        /// <returns>True when that slot holds a detached row.</returns>
        public Boolean IsDetachedRow(Int32 physicalRowIndex)
        {
            return this.detachedPhysicalIndexes != null && this.detachedPhysicalIndexes.Contains(physicalRowIndex);
        }

        /// <summary>
        /// True when the given physical storage slot has been deleted or discarded.
        /// </summary>
        /// <param name="physicalRowIndex">A physical storage slot, as reported by <see cref="DataRow.RowIndex"/>.</param>
        /// <returns>True when the slot is not visible.</returns>
        public Boolean IsDeleted(Int32 physicalRowIndex)
        {
            return this.deletionTracker.IsDeleted(physicalRowIndex);
        }

        /// <summary>
        /// Returns the row's position among visible rows, or -1 when the row is deleted, detached, or from another
        /// table. This is the inverse of the <see cref="this[Int32]"/> indexer.
        /// </summary>
        /// <param name="row">The row to locate.</param>
        /// <returns>The zero-based logical position, or -1.</returns>
        public Int32 IndexOf(DataRow row)
        {
            Boolean sameTable = ReferenceEquals(row.Table, this.owningTable);
            if (sameTable == false) { return -1; }
            if (row.RowIndex < 0 || row.RowIndex >= this.deletionTracker.PhysicalCount) { return -1; }
            Boolean deleted = this.deletionTracker.IsDeleted(row.RowIndex);
            if (deleted) { return -1; }
            return this.deletionTracker.ToLogicalIndex(row.RowIndex);
        }

        /// <summary>
        /// Removes every row and releases every column's data, leaving the schema intact. Detached rows go too: their
        /// storage is released with everything else, so a handle to one taken before the call no longer refers to
        /// anything this table will accept.
        /// </summary>
        public void Clear()
        {
            this.deletionTracker.Clear();
            this.detachedPhysicalIndexes = null;
            this.owningTable.Columns.ClearColumnData();
            // Drop the row-view store too. It is rebuilt lazily if the indexer is used again, so a table that is
            // filled, drained and refilled does not keep views for rows that no longer exist.
            this.rowViewChunks = null;
        }

        /// <summary>
        /// Copies the visible rows into an array.
        /// </summary>
        /// <param name="destination">The destination array.</param>
        /// <param name="destinationIndex">Where in the destination to start writing.</param>
        /// <exception cref="ArgumentNullException">The destination is null.</exception>
        /// <exception cref="ArgumentException">The destination is too small.</exception>
        public void CopyTo(DataRow[] destination, Int32 destinationIndex)
        {
            if (destination == null) { throw new ArgumentNullException(nameof(destination)); }
            if (destinationIndex < 0) { throw new ArgumentOutOfRangeException(nameof(destinationIndex), destinationIndex, "Destination index must not be negative."); }
            if (destination.Length - destinationIndex < Count) { throw new ArgumentException("The destination array is too small to hold every visible row.", nameof(destination)); }
            Int32 writeIndex = destinationIndex;
            foreach (DataRow row in this)
            {
                destination[writeIndex] = row;
                writeIndex = writeIndex + 1;
            }
        }

        /// <summary>
        /// Inserts a row at a given position.
        /// </summary>
        /// <param name="row">The row to insert.</param>
        /// <param name="position">Where to insert it.</param>
        /// <exception cref="NotImplementedException">Always.</exception>
        public void InsertAt(DataRow row, Int32 position)
        {
            throw new NotImplementedException("Rows cannot be inserted in the middle: a physical slot is permanent for the life of the table, which is what lets a DataRow stay valid while other rows around it are deleted. Append with Add and order the rows when you read them.");
        }

        /// <summary>
        /// Finds the row with the given primary key value.
        /// </summary>
        /// <param name="key">The key value.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataRow Find(Object key)
        {
            throw new NotImplementedException("Find needs a primary key index, and this library has no primary keys (see DataTable.PrimaryKey). Resolve the key column's typed handle once and scan it, or build your own dictionary.");
        }

        /// <summary>
        /// True when a row with the given primary key value exists.
        /// </summary>
        /// <param name="key">The key value.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public Boolean Contains(Object key)
        {
            throw new NotImplementedException("Contains needs a primary key index, and this library has no primary keys (see DataTable.PrimaryKey). Resolve the key column's typed handle once and scan it, or build your own dictionary.");
        }

        /// <summary>
        /// Returns an allocation-free struct enumerator over the visible rows, in logical order. Deleted rows are
        /// skipped; a row appended during enumeration is visited.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this.owningTable, this.deletionTracker);
        }
        #endregion

        #region Private Methods
        // Generic enumeration, required by IEnumerable<T>. Boxes the struct enumerator, so LINQ and other
        // IEnumerable-based consumers pay one allocation per enumeration - foreach over this collection directly does
        // not, because the compiler binds to the public struct-returning GetEnumerator above.
        IEnumerator<DataRow> IEnumerable<DataRow>.GetEnumerator()
        {
            Enumerator enumerator = GetEnumerator();
            return enumerator;
        }

        // Non-generic enumeration, required by IEnumerable. Same boxing note as above.
        IEnumerator IEnumerable.GetEnumerator()
        {
            Enumerator enumerator = GetEnumerator();
            return enumerator;
        }

        // Marks the trailing columns a positional Add was given no value for as null. Null tracking is "is null", so
        // an untouched cell reads as NOT null and would otherwise come back as default(T) - zero, false, an empty
        // reference - where System.Data.DataTable gives DBNull. This is the one place the row layer can KNOW a cell
        // was deliberately skipped, so it is the one place the gap can be closed. It costs nothing on the common path
        // where every column was supplied, because the loop does not execute, and nothing at all for a non-nullable
        // column, which has no bitmap to write to.
        private static void MarkUnsuppliedColumnsNull(DataColumnCollection columns, Int32 suppliedValueCount, Int32 physicalRowIndex)
        {
            for (Int32 ordinal = suppliedValueCount; ordinal < columns.Count; ordinal++)
            {
                DataColumn column = columns[ordinal];
                if (column.AllowDBNull == false) { continue; }
                // A column with a default already had it written by ApplyColumnDefaults; overwriting with null here
                // would undo exactly what the caller asked the default to do.
                if (column.HasDefaultValue) { continue; }
                column.SetValue(physicalRowIndex, null);
            }
        }

        // Writes every column's default value into a freshly created row. Costs one Boolean test when no column has
        // a default, which is almost every table - the per-column walk only happens when a default actually exists.
        private void ApplyColumnDefaults(Int32 physicalRowIndex)
        {
            DataColumnCollection columns = this.owningTable.Columns;
            if (columns.HasDefaultValues == false) { return; }
            columns.ApplyDefaultValues(physicalRowIndex);
        }

        // Resolves a logical position to a row view WITHOUT touching the row-view store, so the library's own
        // internals never cause a table to start paying for views the caller has not asked for.
        private DataRow GetRowByValue(Int32 logicalIndex)
        {
            Int32 visibleCount = this.deletionTracker.LogicalCount;
            if (logicalIndex < 0 || logicalIndex >= visibleCount) { throw new ArgumentOutOfRangeException(nameof(logicalIndex), logicalIndex, $"Row index must be between 0 and {visibleCount - 1}."); }
            Int32 physicalIndex = this.deletionTracker.ToPhysicalIndex(logicalIndex);
            return new DataRow(this.owningTable, physicalIndex);
        }

        // Returns a reference to the DataRow view for a physical slot, creating the backing store on first use.
        //
        // WHY THIS NEEDS PER-ROW STORAGE. The indexer returns "ref DataRow" so that
        // "table.Rows[0]["Name"] = value" compiles: C# only allows assignment through a property whose receiver is a
        // VARIABLE, and a by-value struct return is not one. A ref has to point at something real, and the obvious
        // cheap choice - one shared scratch field reused by every call - is WRONG in a way no exception would ever
        // reveal. In "rows[0]["Name"] = rows[1]["Name"]" the compiler takes the target's address first, then
        // evaluates the right-hand side, which overwrites that same scratch slot with row 1; the write then lands in
        // ROW 1 while row 0 is left untouched. Measured, not theorised. One slot per row is what makes the ref exact.
        //
        // The store is chunked rather than one array for two reasons: a million-row table would otherwise allocate a
        // 16 MB array straight onto the large object heap, and chunks are never reallocated, so a reference handed
        // out earlier stays valid no matter how many rows are appended afterwards.
        private ref DataRow ResolveRowView(Int32 physicalIndex)
        {
            if (this.rowViewChunks == null) { this.rowViewChunks = new List<DataRow[]>(); }
            Int32 chunkIndex = physicalIndex >> ROW_VIEW_CHUNK_SHIFT;
            while (this.rowViewChunks.Count <= chunkIndex)
            {
                this.rowViewChunks.Add(new DataRow[ROW_VIEW_CHUNK_ROWS]);
            }
            DataRow[] chunk = this.rowViewChunks[chunkIndex];
            Int32 offset = physicalIndex & ROW_VIEW_CHUNK_MASK;
            // Rewriting the slot every time costs one 16-byte store and removes any need to track which slots have
            // been initialised - the value is a pure function of the physical index, so rewriting it is a no-op in
            // meaning and always correct after a Clear or a reuse.
            chunk[offset] = new DataRow(this.owningTable, physicalIndex);
            return ref chunk[offset];
        }

        // Validates that the given row is one this collection still considers detached, for Add and Discard. The two
        // ways it can fail read very differently to a caller, so they are reported differently: a row that is already
        // part of the table is a double-add, and anything else is a handle from a completed or cleared NewRow cycle.
        private void ValidateDetachedRow(DataRow row, String attemptedOperation)
        {
            ValidateRowBelongsHere(row);
            if (IsDetachedRow(row.RowIndex)) { return; }
            Boolean alreadyInTable = row.RowIndex >= 0 && row.RowIndex < this.deletionTracker.PhysicalCount && this.deletionTracker.IsDeleted(row.RowIndex) == false;
            if (alreadyInTable) { throw new InvalidOperationException($"Rows.{attemptedOperation}(DataRow) was given a row that is already part of the table, at logical position {this.deletionTracker.ToLogicalIndex(row.RowIndex)}. Only a row created by NewRow and not yet added can be added or discarded."); }
            throw new InvalidOperationException($"Rows.{attemptedOperation}(DataRow) was given the row at physical slot {row.RowIndex}, which this table does not hold as a detached row. It was already added or discarded, or the table has been cleared since it was created. Use Rows.Add(params Object[]) or Rows.AddNewRow() to append a row directly.");
        }

        // Refuses an Add that would place a row somewhere other than at the end.
        //
        // A row's position is its physical slot's position, and a detached row took its slot when NewRow created it.
        // So Add makes the row visible where it was CREATED, while System.Data would make it visible where it was
        // ADDED. Those coincide exactly while nothing else has joined the table since the row was created - which
        // covers the create/fill/add loop and "create several, add some in order, abandon the rest" - and they
        // diverge the moment anything else goes in first, whether that is a later detached row being added ahead of
        // this one or a plain Rows.Add(values) landing at the tail in between.
        //
        // Reproducing Add order in place would mean logical order ceasing to equal physical order, and that equality
        // is what the O(1)-per-row enumeration and the whole logical/physical translation are built on. So the
        // divergence is DETECTED EXACTLY rather than approximated or papered over: if any visible row already sits at
        // a later slot, this Add would insert ahead of it, and that is refused, pointing at AddAtEnd, which does
        // reproduce System.Data's order by copying. Nothing is ever silently misordered, and the check costs one
        // bitmap probe on a path that is not a hot one.
        private void RejectAddThatWouldMisorder(Int32 physicalRowIndex)
        {
            Int32 laterVisibleIndex;
            Boolean wouldInsertBeforeAnExistingRow = this.deletionTracker.TryGetNextAlivePhysicalIndex(physicalRowIndex + 1, out laterVisibleIndex);
            if (wouldInsertBeforeAnExistingRow == false) { return; }
            throw new InvalidOperationException($"This row cannot be added here without changing the table's order. Rows appear in the order NewRow created them, and another row has joined the table since this one was created - it sits at logical position {this.deletionTracker.ToLogicalIndex(laterVisibleIndex)} - so adding this row now would place it ahead of that one rather than at the end. Either add rows created by NewRow before anything else joins the table, or call Rows.AddAtEnd(row), which copies the row into a new last row and gives System.Data's ordering exactly.");
        }

        // Rejects a row view that is unbound or belongs to another table, before its index is used against this
        // table's storage.
        private void ValidateRowBelongsHere(DataRow row)
        {
            if (row.Table == null) { throw new ArgumentException("This DataRow is not bound to a table. Obtain rows from DataTable.Rows rather than constructing default(DataRow).", nameof(row)); }
            Boolean sameTable = ReferenceEquals(row.Table, this.owningTable);
            if (sameTable == false) { throw new ArgumentException($"This DataRow belongs to table '{row.Table.TableName}', not '{this.owningTable.TableName}'.", nameof(row)); }
        }
        #endregion

        #region Nested Types
        /// <summary>
        /// Allocation-free enumerator over a table's visible rows. Mirrors the shape of
        /// <see cref="System.Collections.Generic.List{T}.Enumerator"/>: a mutable struct, obtained by value, so
        /// <c>foreach</c> over <see cref="DataRowCollection"/> performs no heap allocation whatsoever.
        /// </summary>
        public struct Enumerator : IEnumerator<DataRow>
        {
            #region Private Constants
            // Sentinel for "positioned before the first row, or past the last one".
            private const Int32 NO_CURRENT_ROW = -1;
            #endregion

            #region Private Members
            // The table used to synthesize each DataRow view.
            private readonly DataTable table;

            // Supplies the next visible physical slot and skips whole words of tombstones in one step.
            private readonly RowDeletionTracker tracker;

            // Physical slot of the current row, or NO_CURRENT_ROW before the first MoveNext and after the last.
            private Int32 currentPhysicalIndex;

            // Physical slot to resume searching from, or NO_CURRENT_ROW once the enumeration is exhausted. Keeping
            // this separate from currentPhysicalIndex is what makes MoveNext after exhaustion stay exhausted instead
            // of restarting at zero.
            private Int32 nextSearchIndex;
            #endregion

            #region Constructor / Destructor
            /// <summary>
            /// Creates an enumerator positioned before the first visible row.
            /// </summary>
            /// <param name="table">The table being enumerated.</param>
            /// <param name="tracker">The table's deletion tracker.</param>
            internal Enumerator(DataTable table, RowDeletionTracker tracker)
            {
                Contract.Assert(table != null, "An enumerator must be bound to a table.");
                Contract.Assert(tracker != null, "An enumerator must be bound to a deletion tracker.");
                this.table = table;
                this.tracker = tracker;
                this.currentPhysicalIndex = NO_CURRENT_ROW;
                this.nextSearchIndex = 0;
            }
            #endregion

            #region Public Properties
            /// <summary>
            /// The row at the enumerator's current position.
            /// </summary>
            /// <exception cref="InvalidOperationException">The enumerator is positioned before the first row or past
            /// the last one.</exception>
            public DataRow Current
            {
                get
                {
                    if (this.currentPhysicalIndex == NO_CURRENT_ROW) { throw new InvalidOperationException("The enumerator is not positioned on a row. Call MoveNext first."); }
                    return new DataRow(this.table, this.currentPhysicalIndex);
                }
            }
            #endregion

            #region Public Methods
            /// <summary>
            /// Advances to the next visible row, skipping deleted and discarded slots.
            /// </summary>
            /// <returns>True when a row was found.</returns>
            public Boolean MoveNext()
            {
                if (this.nextSearchIndex == NO_CURRENT_ROW) { return false; }
                Int32 foundPhysicalIndex = 0;
                Boolean found = this.tracker.TryGetNextAlivePhysicalIndex(this.nextSearchIndex, out foundPhysicalIndex);
                if (found == false)
                {
                    this.currentPhysicalIndex = NO_CURRENT_ROW;
                    this.nextSearchIndex = NO_CURRENT_ROW;
                    return false;
                }
                this.currentPhysicalIndex = foundPhysicalIndex;
                this.nextSearchIndex = foundPhysicalIndex + 1;
                return true;
            }

            /// <summary>
            /// Returns the enumerator to its initial position, before the first visible row.
            /// </summary>
            public void Reset()
            {
                this.currentPhysicalIndex = NO_CURRENT_ROW;
                this.nextSearchIndex = 0;
            }

            /// <summary>
            /// Nothing to release - present only to satisfy <see cref="IEnumerator{T}"/>.
            /// </summary>
            public void Dispose()
            {
            }
            #endregion

            #region Private Properties
            // Non-generic current value, required by IEnumerator. Boxes the row; only reached through the boxed
            // IEnumerator path, never by foreach over the concrete collection.
            Object IEnumerator.Current
            {
                get
                {
                    DataRow row = Current;
                    return row;
                }
            }
            #endregion
        }
        #endregion
    }
}
