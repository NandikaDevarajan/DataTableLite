///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: A row-shaped VIEW over column storage - the API compatibility surface that lets DataTable-shaped code keep
//   reading and writing "rows" while the data actually lives column-first.
// Assumptions: RowIndex is a PHYSICAL storage slot, not a logical row position. DataRowCollection translates a logical
//   position into a physical one when it hands a row out, and the physical slot is permanent for the life of the
//   table, so a DataRow stays valid even as other rows are deleted. A default(DataRow) is unbound and every member
//   rejects it rather than dereferencing a null table.
// Design Considerations: THIS TYPE IS THE MEMORY SAVING (NFR-1). It is a readonly struct of exactly one reference and
//   one Int32 - 12 or 16 bytes on the stack, never heap-allocated by the library, never stored by
//   DataRowCollection, and synthesized fresh every time one is handed out. System.Data.DataTable's equivalent is a
//   heap-allocated DataRow holding an Object[] of boxed cells; on a million-row table that difference is the bulk of
//   the hundreds of megabytes this library exists to avoid.
//   Because writes go straight into column storage, there is no detached "not yet part of the table" state
//   representable inside a DataRow at all. That state, when it exists, is tracked one level up by DataRowCollection -
//   the only place that can answer "does this slot count as a row yet". See 03_Design.md section 3.1.
//   Get<T>/Set<T> pay one column lookup plus one interface cast per call. That is cheap, allocation free, and not
//   free: a loop over many rows of a known column should resolve DataColumn<T> once via Columns.GetColumn<T> and use
//   its Get/Set instead (03_Design.md section 3.2). The Object indexer is cheaper still to write and boxes every
//   value type it touches - it exists for source compatibility, not for hot paths.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;

using ColumnStore.Data.Storage;

namespace ColumnStore.Data
{
    /// <summary>
    /// A lightweight, non-allocating view of one row of a <see cref="DataTable"/>.
    /// </summary>
    public readonly struct DataRow : IEquatable<DataRow>
    {
        #region Private Members
        // The table this row belongs to. Null only for default(DataRow), which every member rejects.
        private readonly DataTable owningTable;

        // The physical storage slot this row occupies. Permanent for the life of the table.
        private readonly Int32 physicalRowIndex;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Binds a row view to a physical storage slot. Internal because only the row layer knows which slots are
        /// legitimate; callers obtain rows from <see cref="DataRowCollection"/>.
        /// </summary>
        /// <param name="table">The owning table.</param>
        /// <param name="rowIndex">The physical storage slot.</param>
        internal DataRow(DataTable table, Int32 rowIndex)
        {
            Contract.Assert(table != null, "A DataRow must be bound to a table.");
            Contract.Assert(rowIndex >= 0, "A DataRow's physical row index must be non-negative.");
            this.owningTable = table;
            this.physicalRowIndex = rowIndex;
        }
        #endregion

        #region Public Properties
        /// <summary>The table this row belongs to.</summary>
        public DataTable Table
        {
            get { return this.owningTable; }
        }

        /// <summary>
        /// The physical storage slot this row occupies. Stable for the life of the table: it does not change when
        /// other rows are deleted, and it is not the row's position among visible rows. Use
        /// <see cref="DataRowCollection.IndexOf"/> for the latter.
        /// </summary>
        public Int32 RowIndex
        {
            get { return this.physicalRowIndex; }
        }

        /// <summary>
        /// All of the row's cell values as an <see cref="Object"/> array, in column order, with
        /// <see cref="DBNull.Value"/> for null cells - exactly as <see cref="System.Data.DataRow.ItemArray"/> reports
        /// them.
        /// Reading allocates an array and boxes every value type; assigning populates the row positionally. Provided
        /// for <see cref="System.Data.DataRow"/> compatibility - not a hot path.
        /// </summary>
        /// <exception cref="ArgumentNullException">The assigned array is null.</exception>
        /// <exception cref="ArgumentException">The assigned array has more elements than the table has columns.</exception>
        public Object[] ItemArray
        {
            get
            {
                DataColumnCollection columns = GetColumns();
                Object[] values = new Object[columns.Count];
                for (Int32 ordinal = 0; ordinal < columns.Count; ordinal++)
                {
                    DataColumn column = columns[ordinal];
                    values[ordinal] = AsCellValue(column.GetValue(this.physicalRowIndex));
                }
                return values;
            }
            set
            {
                if (value == null) { throw new ArgumentNullException(nameof(value)); }
                DataColumnCollection columns = GetColumns();
                if (value.Length > columns.Count) { throw new ArgumentException($"The table has {columns.Count} columns but {value.Length} values were supplied.", nameof(value)); }
                for (Int32 ordinal = 0; ordinal < value.Length; ordinal++)
                {
                    DataColumn column = columns[ordinal];
                    column.SetValue(this.physicalRowIndex, value[ordinal]);
                }
            }
        }

        /// <summary>
        /// The cell at the given column ordinal, as an <see cref="Object"/>. A null cell reads as
        /// <see cref="DBNull.Value"/>, matching <see cref="System.Data.DataRow"/>; writing null or
        /// <see cref="DBNull.Value"/> makes the cell null. Boxes value types.
        /// </summary>
        /// <param name="columnIndex">Zero-based column ordinal.</param>
        /// <returns>The boxed cell value, or <see cref="DBNull.Value"/>.</returns>
        /// <exception cref="IndexOutOfRangeException">The ordinal is outside the schema.</exception>
        /// <exception cref="ReadOnlyException">The column is read only.</exception>
        public Object this[Int32 columnIndex]
        {
            get
            {
                DataColumnCollection columns = GetColumns();
                DataColumn column = columns[columnIndex];
                return AsCellValue(column.GetValue(this.physicalRowIndex));
            }
            set
            {
                DataColumnCollection columns = GetColumns();
                DataColumn column = columns[columnIndex];
                RejectWriteToReadOnlyColumn(column);
                column.SetValue(this.physicalRowIndex, value);
            }
        }

        /// <summary>
        /// The cell in the named column, as an <see cref="Object"/>. Names are compared case-insensitively. A null
        /// cell reads as <see cref="DBNull.Value"/>, matching <see cref="System.Data.DataRow"/>. Boxes value types.
        /// </summary>
        /// <param name="columnName">The column name.</param>
        /// <returns>The boxed cell value, or <see cref="DBNull.Value"/>.</returns>
        /// <exception cref="ArgumentException">No column has that name.</exception>
        /// <exception cref="ReadOnlyException">The column is read only.</exception>
        public Object this[String columnName]
        {
            get
            {
                DataColumnCollection columns = GetColumns();
                DataColumn column = columns.RequireColumn(columnName);
                return AsCellValue(column.GetValue(this.physicalRowIndex));
            }
            set
            {
                DataColumnCollection columns = GetColumns();
                DataColumn column = columns.RequireColumn(columnName);
                RejectWriteToReadOnlyColumn(column);
                column.SetValue(this.physicalRowIndex, value);
            }
        }

        /// <summary>
        /// The cell in the given column. The column must belong to this row's table.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns>The boxed cell value, or <see cref="DBNull.Value"/>.</returns>
        /// <exception cref="ArgumentNullException">The column is null.</exception>
        /// <exception cref="ArgumentException">The column belongs to a different table.</exception>
        /// <exception cref="ReadOnlyException">The column is read only.</exception>
        public Object this[DataColumn column]
        {
            get
            {
                RequireOwnColumn(column);
                return AsCellValue(column.GetValue(this.physicalRowIndex));
            }
            set
            {
                RequireOwnColumn(column);
                RejectWriteToReadOnlyColumn(column);
                column.SetValue(this.physicalRowIndex, value);
            }
        }

        /// <summary>
        /// The row's state. A row created by NewRow and not yet added is Detached, a deleted row is Deleted, and
        /// every other row is Unchanged. There is no Added or Modified here, because there is no pending-change state
        /// to distinguish them from: a write lands in column storage immediately.
        /// </summary>
        public DataRowState RowState
        {
            get
            {
                DataTable table = RequireTable();
                if (table.Rows.IsDetachedRow(this.physicalRowIndex)) { return DataRowState.Detached; }
                if (table.Rows.IsDeleted(this.physicalRowIndex)) { return DataRowState.Deleted; }
                return DataRowState.Unchanged;
            }
        }

        /// <summary>
        /// The row-level error text. Always empty: this library keeps no per-row error state.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to a non-empty value.</exception>
        public String RowError
        {
            get { return String.Empty; }
            set { if (String.IsNullOrEmpty(value) == false) { throw new NotImplementedException("Per-row error state is not supported: it would cost a string reference per row, which is exactly the kind of per-row overhead this library exists to remove. Keep your validation errors in your own collection, keyed by RowIndex."); } }
        }

        /// <summary>Always false: this library keeps no per-row error state.</summary>
        public Boolean HasErrors
        {
            get { return false; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Returns the typed cell value at the given column ordinal. Throws when the cell is null.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="columnIndex">Zero-based column ordinal.</param>
        /// <returns>The cell value.</returns>
        public T Get<T>(Int32 columnIndex)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn<T> column = columns.GetColumn<T>(columnIndex);
            return column.Get(this.physicalRowIndex);
        }

        /// <summary>
        /// Returns the typed cell value from the named column. Throws when the cell is null.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="columnName">The column name.</param>
        /// <returns>The cell value.</returns>
        public T Get<T>(String columnName)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn<T> column = columns.GetColumn<T>(columnName);
            return column.Get(this.physicalRowIndex);
        }

        /// <summary>
        /// Returns the typed cell value at the given column ordinal, or <c>default(T)</c> when the cell is null.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="columnIndex">Zero-based column ordinal.</param>
        /// <returns>The cell value, or <c>default(T)</c>.</returns>
        public T GetOrDefault<T>(Int32 columnIndex)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn<T> column = columns.GetColumn<T>(columnIndex);
            return DataColumnExtensions.GetOrDefault(column, this.physicalRowIndex);
        }

        /// <summary>
        /// Returns the typed cell value from the named column, or <c>default(T)</c> when the cell is null.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="columnName">The column name.</param>
        /// <returns>The cell value, or <c>default(T)</c>.</returns>
        public T GetOrDefault<T>(String columnName)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn<T> column = columns.GetColumn<T>(columnName);
            return DataColumnExtensions.GetOrDefault(column, this.physicalRowIndex);
        }

        /// <summary>
        /// Stores a typed cell value at the given column ordinal.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="columnIndex">Zero-based column ordinal.</param>
        /// <param name="value">The value to store.</param>
        public void Set<T>(Int32 columnIndex, T value)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn<T> column = columns.GetColumn<T>(columnIndex);
            column.Set(this.physicalRowIndex, value);
        }

        /// <summary>
        /// Stores a typed cell value in the named column.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="columnName">The column name.</param>
        /// <param name="value">The value to store.</param>
        public void Set<T>(String columnName, T value)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn<T> column = columns.GetColumn<T>(columnName);
            column.Set(this.physicalRowIndex, value);
        }

        /// <summary>
        /// Sets the cell at the given column ordinal to null.
        /// </summary>
        /// <param name="columnIndex">Zero-based column ordinal.</param>
        /// <exception cref="InvalidOperationException">The column does not allow null.</exception>
        public void SetNull(Int32 columnIndex)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn column = columns[columnIndex];
            column.SetValue(this.physicalRowIndex, null);
        }

        /// <summary>
        /// Sets the cell in the named column to null.
        /// </summary>
        /// <param name="columnName">The column name.</param>
        /// <exception cref="InvalidOperationException">The column does not allow null.</exception>
        public void SetNull(String columnName)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn column = columns.RequireColumn(columnName);
            column.SetValue(this.physicalRowIndex, null);
        }

        /// <summary>
        /// True when the cell at the given column ordinal is null.
        /// </summary>
        /// <param name="columnIndex">Zero-based column ordinal.</param>
        /// <returns>True when the cell is null.</returns>
        public Boolean IsNull(Int32 columnIndex)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn column = columns[columnIndex];
            return column.IsNull(this.physicalRowIndex);
        }

        /// <summary>
        /// True when the cell in the named column is null.
        /// </summary>
        /// <param name="columnName">The column name.</param>
        /// <returns>True when the cell is null.</returns>
        public Boolean IsNull(String columnName)
        {
            DataColumnCollection columns = GetColumns();
            DataColumn column = columns.RequireColumn(columnName);
            return column.IsNull(this.physicalRowIndex);
        }

        /// <summary>
        /// Marks this row deleted. The row is tombstoned rather than physically removed, so every other row keeps its
        /// physical slot and any DataRow already held onto stays valid.
        /// </summary>
        /// <exception cref="InvalidOperationException">The row is not part of the table, or is already deleted.</exception>
        public void Delete()
        {
            DataTable table = RequireTable();
            table.Rows.Delete(this);
        }

        /// <summary>
        /// Commits pending changes to this row. A NO-OP, and correctly so: a write goes straight into column storage,
        /// so there is never anything pending to commit.
        /// </summary>
        public void AcceptChanges()
        {
        }

        /// <summary>
        /// Rolls this row back to its last committed values.
        /// </summary>
        /// <exception cref="NotImplementedException">Always.</exception>
        public void RejectChanges()
        {
            throw new NotImplementedException("Rolling back needs the row's original values, which this library does not keep - holding them would double the memory it exists to save. Read the values you may need to restore before changing them.");
        }

        /// <summary>
        /// Begins an edit that BeginEdit/EndEdit would make atomic. A no-op here: there is no proposed-value buffer,
        /// so a write is visible immediately and there is nothing to commit.
        /// </summary>
        public void BeginEdit()
        {
        }

        /// <summary>
        /// Ends an edit begun by <see cref="BeginEdit"/>. A no-op, for the same reason.
        /// </summary>
        public void EndEdit()
        {
        }

        /// <summary>
        /// Cancels an edit begun by <see cref="BeginEdit"/>.
        /// </summary>
        /// <exception cref="NotImplementedException">Always. Writes are immediate, so there is nothing to cancel, and
        /// silently doing nothing would leave the caller believing a change had been undone.</exception>
        public void CancelEdit()
        {
            throw new NotImplementedException("CancelEdit cannot work here: a write goes straight into column storage, so by the time you cancel, the value has already replaced the old one. Read the old value first if you need to restore it.");
        }

        /// <summary>
        /// True when the row has the given version. Only <see cref="DataRowVersion.Current"/> and
        /// <see cref="DataRowVersion.Default"/> ever exist here, because no other version is retained.
        /// </summary>
        /// <param name="version">The version to test for.</param>
        /// <returns>True when the version exists.</returns>
        public Boolean HasVersion(DataRowVersion version)
        {
            return version == DataRowVersion.Current || version == DataRowVersion.Default;
        }

        /// <summary>
        /// Clears the row's errors. A no-op: there are none to clear.
        /// </summary>
        public void ClearErrors()
        {
        }

        /// <summary>
        /// The error text for one column. Always empty.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns>An empty string.</returns>
        public String GetColumnError(DataColumn column)
        {
            return String.Empty;
        }

        /// <summary>
        /// Records an error against one column.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="error">The error text.</param>
        /// <exception cref="NotImplementedException">A non-empty error is supplied.</exception>
        public void SetColumnError(DataColumn column, String error)
        {
            if (String.IsNullOrEmpty(error)) { return; }
            throw new NotImplementedException("Per-cell error state is not supported: it would cost a string reference per cell. Keep validation errors in your own collection, keyed by row index and column.");
        }

        /// <summary>
        /// True when the other row view refers to the same physical slot of the same table.
        /// </summary>
        /// <param name="other">The row to compare with.</param>
        /// <returns>True when both views denote the same row.</returns>
        public Boolean Equals(DataRow other)
        {
            Boolean sameTable = ReferenceEquals(this.owningTable, other.owningTable);
            return sameTable && this.physicalRowIndex == other.physicalRowIndex;
        }

        /// <summary>
        /// True when the object is a <see cref="DataRow"/> denoting the same row.
        /// </summary>
        /// <param name="obj">The object to compare with.</param>
        /// <returns>True when both views denote the same row.</returns>
        public override Boolean Equals(Object obj)
        {
            if (obj is DataRow other) { return Equals(other); }
            return false;
        }

        /// <summary>
        /// A hash code combining the owning table's identity and the physical row index.
        /// </summary>
        /// <returns>The hash code.</returns>
        public override Int32 GetHashCode()
        {
            Int32 tableHash = 0;
            if (this.owningTable != null) { tableHash = this.owningTable.GetHashCode(); }
            return Bits.CombineHashes(tableHash, this.physicalRowIndex);
        }

        /// <summary>
        /// Describes the row by table name and physical slot, for diagnostics and test failure messages.
        /// </summary>
        /// <returns>The description.</returns>
        public override String ToString()
        {
            if (this.owningTable == null) { return "DataRow (unbound)"; }
            return $"DataRow {this.owningTable.TableName}[physical {this.physicalRowIndex}]";
        }
        #endregion

        #region Private Methods
        // Converts the column layer's "null means no value" into the DBNull.Value that System.Data.DataRow reports.
        // The column layer keeps using null internally because that is what its own typed paths already produce; the
        // translation happens here, at the one surface where compatibility is what matters.
        private static Object AsCellValue(Object storedValue)
        {
            if (storedValue == null) { return DBNull.Value; }
            return storedValue;
        }

        // Returns the owning table, rejecting an unbound default(DataRow) with an actionable message.
        private DataTable RequireTable()
        {
            if (this.owningTable == null) { throw new InvalidOperationException("This DataRow is not bound to a table. Obtain rows from DataTable.Rows rather than constructing default(DataRow)."); }
            return this.owningTable;
        }

        // Refuses a write through the compatibility surface to a column marked read only, with the exception type
        // System.Data raises so that existing catch blocks keep working.
        private static void RejectWriteToReadOnlyColumn(DataColumn column)
        {
            if (column.ReadOnly == false) { return; }
            throw new ReadOnlyException($"Column '{column.ColumnName}' is read only.");
        }

        // Rejects a column that belongs to a different table - or to none - before it is used to address a cell in
        // this row, because doing so would read whatever happened to be at this row index in that other column.
        private void RequireOwnColumn(DataColumn column)
        {
            if (column == null) { throw new ArgumentNullException(nameof(column)); }
            DataTable table = RequireTable();
            if (ReferenceEquals(column.Table, table)) { return; }
            throw new ArgumentException($"Column '{column.ColumnName}' does not belong to table {table}.", nameof(column));
        }

        // Returns the owning table's schema, rejecting an unbound default(DataRow) with an actionable message instead
        // of a NullReferenceException. Every public member funnels through here, so the guard exists exactly once.
        private DataColumnCollection GetColumns()
        {
            if (this.owningTable == null) { throw new InvalidOperationException("This DataRow is not bound to a table. Obtain rows from DataTable.Rows rather than constructing default(DataRow)."); }
            return this.owningTable.Columns;
        }
        #endregion
    }
}
