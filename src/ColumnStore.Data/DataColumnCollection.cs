///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The table's schema: an ordered list of columns, a name-to-ordinal map, and the two generic entry points -
//   Add<T> and GetColumn<T> - that hand a caller a resolved, strongly typed column handle.
// Assumptions: Single-writer access; not thread safe. A column belongs to at most one collection at a time.
// Design Considerations: Add<T> and GetColumn<T> exist to make FR-5 usable: resolve the column once, outside the loop,
//   and every subsequent cell access is a direct array index. Row-level Get<T>/Set<T> is the convenient alternative
//   and costs one lookup plus one cast per call - fine for scattered access, wasteful in a loop over a million rows.
//   Name lookup is a Dictionary rather than a linear scan because DataRow's string indexer and the SQL loader's
//   schema binding both go through it, and a wide table would otherwise make an innocent-looking row["Name"] O(columns).
//   Names are compared case-insensitively, matching System.Data.DataTable's default (NFR-8).
//   THE SCHEMA IS NO LONGER APPEND-ONLY. Remove, RemoveAt, SetOrdinal and a ColumnName change all reorder or drop
//   columns, which means ORDINALS MOVE. That was the original reason for forbidding removal, and the cost is real: a
//   caller holding a cached DataColumn<T> keeps a valid handle onto valid data, but an ordinal captured earlier may
//   now address a different column. The library's own rule is therefore "cache the HANDLE, never the ORDINAL", and
//   every internal consumer that walks by ordinal - the loader, the writer, the reader adapter - re-reads the schema
//   at the start of each operation rather than holding ordinals across one.
//   A removed column is DETACHED, not cleared: its Table becomes null and its Ordinal -1, but it keeps its data, so
//   "remove it and read the values out afterwards" works, and dropping the reference is what frees the memory.
//   Adding a column to a table that already has rows is allowed and leaves the new column's existing rows null (for a
//   nullable column) or at default(T) (for a non-nullable one). It is not blocked because a loader that discovers a
//   new field mid-stream is a legitimate pattern, but a schema-then-rows ordering is the intended usage.
//   Making that "existing rows are null" promise true costs one explicit null write per existing row, once, when the
//   late column is added. Null tracking is "is null", so an untouched cell carries no bit; without this pass the new
//   column's existing rows would read as default(T) the moment any row of it was written.
//   See 03_Design.md section 2.4 and Spec/05_DesignDecisions.md section 2.8.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections;
using System.Collections.Generic;

namespace ColumnStore.Data
{
    /// <summary>
    /// The ordered collection of columns that defines a <see cref="DataTable"/>'s schema. Mirrors the shape of
    /// <see cref="System.Data.DataColumnCollection"/>.
    /// </summary>
    public sealed class DataColumnCollection : IEnumerable<DataColumn>
    {
        #region Private Constants
        // Prefix System.Data uses when it has to invent a column name.
        private const String GENERATED_NAME_PREFIX = "Column";
        #endregion

        #region Private Members
        // Columns in ordinal order. A column's position in this list is its ordinal.
        private readonly List<DataColumn> columns;

        // Name to ordinal, case-insensitive. Keeps name-based cell access off a linear scan.
        private readonly Dictionary<String, Int32> ordinalsByName;

        // True when at least one column carries a default value, so row creation can skip the whole default-writing
        // pass with a single test in the overwhelmingly common case where no column has one.
        private Boolean hasDefaultValues;
        #endregion

        #region Block Dependencies
        // The table this schema belongs to, or null for a free-standing collection. Read when a column is attached or
        // detached, and when a column added to a non-empty table has to mark the existing rows null.
        private readonly DataTable owningTable;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates an empty schema with no owning table.
        /// </summary>
        public DataColumnCollection()
            : this(null)
        {
        }

        /// <summary>
        /// Creates an empty schema owned by a table.
        /// </summary>
        /// <param name="owningTable">The owning table. May be null.</param>
        internal DataColumnCollection(DataTable owningTable)
        {
            this.columns = new List<DataColumn>();
            this.ordinalsByName = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);
            this.owningTable = owningTable;
            this.hasDefaultValues = false;
        }
        #endregion

        #region Public Properties
        /// <summary>Number of columns in the schema.</summary>
        public Int32 Count
        {
            get { return this.columns.Count; }
        }

        /// <summary>Always false; this collection is not read only.</summary>
        public Boolean IsReadOnly
        {
            get { return false; }
        }

        /// <summary>Always false; this collection is not fixed size.</summary>
        public Boolean IsFixedSize
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
            get { return this.columns; }
        }

        /// <summary>
        /// The column at the given ordinal.
        /// </summary>
        /// <param name="index">Zero-based column ordinal.</param>
        /// <returns>The column.</returns>
        /// <exception cref="IndexOutOfRangeException">The ordinal is outside the schema. This is the exception
        /// <see cref="System.Data.DataColumnCollection"/> raises, so code that catches it keeps working.</exception>
        public DataColumn this[Int32 index]
        {
            get
            {
                if (index < 0 || index >= this.columns.Count) { throw new IndexOutOfRangeException($"Cannot find column {index}."); }
                return this.columns[index];
            }
        }

        /// <summary>
        /// The column with the given name, compared case-insensitively.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <returns>The column, or NULL when no column has that name - which is what
        /// <see cref="System.Data.DataColumnCollection"/> returns, and what code written against it expects.</returns>
        /// <exception cref="ArgumentNullException">The name is null.</exception>
        public DataColumn this[String name]
        {
            get
            {
                if (name == null) { throw new ArgumentNullException(nameof(name)); }
                Int32 ordinal = 0;
                Boolean found = this.ordinalsByName.TryGetValue(name, out ordinal);
                if (found == false) { return null; }
                return this.columns[ordinal];
            }
        }
        #endregion

        #region Internal Properties
        /// <summary>
        /// True when at least one column carries a default value. The row layer tests this once per created row
        /// instead of walking the schema, so a table with no defaults - which is almost every table - pays one
        /// Boolean test per row rather than one call per column per row.
        /// </summary>
        internal Boolean HasDefaultValues
        {
            get { return this.hasDefaultValues; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Adds a nullable <see cref="String"/> column with a generated name, as
        /// <see cref="System.Data.DataColumnCollection"/> does when given nothing.
        /// </summary>
        /// <returns>The created column.</returns>
        public DataColumn Add()
        {
            String generatedName = GenerateColumnName();
            return Add(generatedName, typeof(String), true);
        }

        /// <summary>
        /// Adds a nullable <see cref="String"/> column, as <see cref="System.Data.DataColumnCollection"/> does when
        /// given a name alone.
        /// </summary>
        /// <param name="columnName">The column name.</param>
        /// <returns>The created column.</returns>
        public DataColumn Add(String columnName)
        {
            return Add(columnName, typeof(String), true);
        }

        /// <summary>
        /// Adds a nullable column of the given runtime type. Reflection, if any is needed at all, is paid here - once.
        /// </summary>
        /// <param name="columnName">The column name. Must be unique, case-insensitively.</param>
        /// <param name="type">The CLR type of the column's values. A <see cref="System.Nullable{T}"/> type is
        /// unwrapped and forces the column to be nullable.</param>
        /// <returns>The created column.</returns>
        public DataColumn Add(String columnName, Type type)
        {
            return Add(columnName, type, true);
        }

        /// <summary>
        /// Adds a column of the given runtime type and nullability.
        /// </summary>
        /// <param name="columnName">The column name. Must be unique, case-insensitively.</param>
        /// <param name="type">The CLR type of the column's values.</param>
        /// <param name="allowDBNull">Whether cells may be null.</param>
        /// <returns>The created column.</returns>
        /// <exception cref="ArgumentNullException">The name or type is null.</exception>
        /// <exception cref="ArgumentException">The name is blank or already in use.</exception>
        public DataColumn Add(String columnName, Type type, Boolean allowDBNull)
        {
            if (columnName == null) { throw new ArgumentNullException(nameof(columnName)); }
            if (type == null) { throw new ArgumentNullException(nameof(type)); }
            ValidateNewColumnName(columnName);
            DataColumn column = DataColumnFactory.Create(columnName, type, allowDBNull);
            Register(column);
            return column;
        }

        /// <summary>
        /// Adds an already-created column. The column must not already belong to a table.
        /// </summary>
        /// <param name="column">The column to add.</param>
        /// <exception cref="ArgumentNullException">The column is null.</exception>
        /// <exception cref="ArgumentException">The column already belongs to a table, or its name is already in use.</exception>
        public void Add(DataColumn column)
        {
            if (column == null) { throw new ArgumentNullException(nameof(column)); }
            if (column.Table != null) { throw new ArgumentException($"Column '{column.ColumnName}' already belongs to table '{column.Table.TableName}'. Remove it from that table first.", nameof(column)); }
            ValidateNewColumnName(column.ColumnName);
            Register(column);
        }

        /// <summary>
        /// Adds several already-created columns.
        /// </summary>
        /// <param name="columnsToAdd">The columns to add. A null entry is skipped, as System.Data does.</param>
        /// <exception cref="ArgumentNullException">The array is null.</exception>
        public void AddRange(DataColumn[] columnsToAdd)
        {
            if (columnsToAdd == null) { throw new ArgumentNullException(nameof(columnsToAdd)); }
            for (Int32 index = 0; index < columnsToAdd.Length; index++)
            {
                DataColumn column = columnsToAdd[index];
                if (column == null) { continue; }
                Add(column);
            }
        }

        /// <summary>
        /// Adds a column of the given compile-time type and returns the strongly typed handle. This is the preferred
        /// way to define a schema: no reflection, and the caller keeps a handle whose cell access is a direct array
        /// index.
        /// </summary>
        /// <typeparam name="T">The column's element type. Never <see cref="System.Nullable{T}"/> - pass the underlying
        /// type with <paramref name="allowDBNull"/> true instead.</typeparam>
        /// <param name="columnName">The column name. Must be unique, case-insensitively.</param>
        /// <param name="allowDBNull">Whether cells may be null.</param>
        /// <returns>The created column.</returns>
        /// <exception cref="ArgumentNullException">The name is null.</exception>
        /// <exception cref="ArgumentException">The name is blank or already in use, or T is a nullable value type.</exception>
        public DataColumn<T> Add<T>(String columnName, Boolean allowDBNull = true)
        {
            if (columnName == null) { throw new ArgumentNullException(nameof(columnName)); }
            ValidateNewColumnName(columnName);
            ValidateElementType<T>(columnName);
            DataColumn<T> column = new DataColumn<T>(columnName, allowDBNull);
            Register(column);
            return column;
        }

        /// <summary>
        /// Adds a column with an explicit storage chunk row count. Exposed for callers with unusual access patterns
        /// and for tests that need to force chunk-boundary conditions without materialising tens of thousands of rows.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="columnName">The column name.</param>
        /// <param name="allowDBNull">Whether cells may be null.</param>
        /// <param name="chunkRowCount">Rows per storage chunk. Must be a positive power of two.</param>
        /// <returns>The created column.</returns>
        public DataColumn<T> Add<T>(String columnName, Boolean allowDBNull, Int32 chunkRowCount)
        {
            if (columnName == null) { throw new ArgumentNullException(nameof(columnName)); }
            ValidateNewColumnName(columnName);
            ValidateElementType<T>(columnName);
            DataColumn<T> column = new DataColumn<T>(columnName, allowDBNull, chunkRowCount);
            Register(column);
            return column;
        }

        /// <summary>
        /// Removes the named column from the schema. The column is detached but keeps its data, so values can still be
        /// read out of a handle taken before the removal; dropping that handle is what releases the memory.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <exception cref="ArgumentNullException">The name is null.</exception>
        /// <exception cref="ArgumentException">No column has that name.</exception>
        public void Remove(String name)
        {
            DataColumn column = RequireColumn(name);
            Remove(column);
        }

        /// <summary>
        /// Removes a column from the schema.
        /// </summary>
        /// <param name="column">The column to remove.</param>
        /// <exception cref="ArgumentNullException">The column is null.</exception>
        /// <exception cref="ArgumentException">The column does not belong to this schema.</exception>
        public void Remove(DataColumn column)
        {
            if (column == null) { throw new ArgumentNullException(nameof(column)); }
            Int32 ordinal = this.columns.IndexOf(column);
            if (ordinal < 0) { throw new ArgumentException($"Column '{column.ColumnName}' does not belong to this table.", nameof(column)); }
            RemoveAt(ordinal);
        }

        /// <summary>
        /// Removes the column at the given ordinal. Every column after it moves down one position.
        /// </summary>
        /// <param name="index">Zero-based column ordinal.</param>
        /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the schema.</exception>
        public void RemoveAt(Int32 index)
        {
            DataColumn column = this[index];
            this.columns.RemoveAt(index);
            column.Attach(null, -1);
            ReindexAll();
            RefreshDefaultValueState();
        }

        /// <summary>
        /// True when the column could be removed from this schema. Provided for source compatibility with
        /// <see cref="System.Data.DataColumnCollection.CanRemove"/>; this library enforces no constraints, so the only
        /// reason a column cannot be removed is that it does not belong here.
        /// </summary>
        /// <param name="column">The column to test.</param>
        /// <returns>True when the column belongs to this schema.</returns>
        public Boolean CanRemove(DataColumn column)
        {
            if (column == null) { return false; }
            return this.columns.Contains(column);
        }

        /// <summary>
        /// Returns the ordinal of the named column, or -1 when no column has that name.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <returns>The zero-based ordinal, or -1. A null or blank name gives -1 rather than throwing, matching
        /// <see cref="System.Data.DataColumnCollection.IndexOf(String)"/>.</returns>
        public Int32 IndexOf(String name)
        {
            if (String.IsNullOrEmpty(name)) { return -1; }
            Int32 ordinal = 0;
            Boolean found = this.ordinalsByName.TryGetValue(name, out ordinal);
            if (found == false) { return -1; }
            return ordinal;
        }

        /// <summary>
        /// Returns the ordinal of a column, or -1 when it does not belong to this schema.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns>The zero-based ordinal, or -1.</returns>
        public Int32 IndexOf(DataColumn column)
        {
            if (column == null) { return -1; }
            return this.columns.IndexOf(column);
        }

        /// <summary>
        /// True when a column with that name exists.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <returns>True when the column exists. A null or blank name gives false rather than throwing, matching
        /// <see cref="System.Data.DataColumnCollection.Contains"/>.</returns>
        public Boolean Contains(String name)
        {
            if (String.IsNullOrEmpty(name)) { return false; }
            return this.ordinalsByName.ContainsKey(name);
        }

        /// <summary>
        /// Copies the columns into an array, in ordinal order.
        /// </summary>
        /// <param name="destination">The destination array.</param>
        /// <param name="destinationIndex">Where in the destination to start writing.</param>
        public void CopyTo(DataColumn[] destination, Int32 destinationIndex)
        {
            this.columns.CopyTo(destination, destinationIndex);
        }

        /// <summary>
        /// Returns the named column as a strongly typed handle. Resolve once, outside a loop, then use
        /// <see cref="DataColumn{T}.Get"/> and <see cref="DataColumn{T}.Set"/> per row.
        /// </summary>
        /// <typeparam name="T">The column's expected element type.</typeparam>
        /// <param name="name">The column name.</param>
        /// <returns>The typed column.</returns>
        /// <exception cref="ArgumentException">No column has that name.</exception>
        /// <exception cref="InvalidOperationException">The column holds a different type.</exception>
        public DataColumn<T> GetColumn<T>(String name)
        {
            DataColumn column = RequireColumn(name);
            DataColumn<T> typedColumn = AsTypedColumn<T>(column);
            return typedColumn;
        }

        /// <summary>
        /// Returns the column at the given ordinal as a strongly typed handle.
        /// </summary>
        /// <typeparam name="T">The column's expected element type.</typeparam>
        /// <param name="index">Zero-based column ordinal.</param>
        /// <returns>The typed column.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the schema.</exception>
        /// <exception cref="InvalidOperationException">The column holds a different type.</exception>
        public DataColumn<T> GetColumn<T>(Int32 index)
        {
            DataColumn column = this[index];
            DataColumn<T> typedColumn = AsTypedColumn<T>(column);
            return typedColumn;
        }

        /// <summary>
        /// Removes every column from the schema, detaching each one - which is what
        /// <see cref="System.Data.DataColumnCollection.Clear"/> does. To empty a table of its DATA while keeping its
        /// schema, call <see cref="DataTable.Clear"/> instead.
        /// </summary>
        public void Clear()
        {
            for (Int32 ordinal = 0; ordinal < this.columns.Count; ordinal++)
            {
                this.columns[ordinal].Attach(null, -1);
            }
            this.columns.Clear();
            this.ordinalsByName.Clear();
            this.hasDefaultValues = false;
        }

        /// <summary>
        /// Returns an enumerator over the columns in ordinal order.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public IEnumerator<DataColumn> GetEnumerator()
        {
            return this.columns.GetEnumerator();
        }
        #endregion

        #region Internal Methods
        /// <summary>
        /// Returns the named column, or throws naming it. The indexer deliberately returns null for a missing column
        /// because System.Data's does; every caller inside this library that cannot proceed without the column comes
        /// through here instead, so a typo surfaces as a named error rather than a NullReferenceException.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <returns>The column.</returns>
        /// <exception cref="ArgumentException">No column has that name.</exception>
        internal DataColumn RequireColumn(String name)
        {
            DataColumn column = this[name];
            if (column == null) { throw new ArgumentException($"Column '{name}' does not belong to table {DescribeOwningTable()}.", nameof(name)); }
            return column;
        }

        /// <summary>
        /// Releases every column's data while leaving the schema intact. This is what <see cref="DataTable.Clear"/>
        /// needs, and is deliberately NOT what the public <see cref="Clear"/> does, because System.Data's
        /// DataColumnCollection.Clear drops the columns themselves.
        /// </summary>
        internal void ClearColumnData()
        {
            for (Int32 ordinal = 0; ordinal < this.columns.Count; ordinal++)
            {
                this.columns[ordinal].Clear();
            }
        }

        /// <summary>
        /// Renames a column that belongs to this schema, re-keying the name map.
        /// </summary>
        /// <param name="column">The column to rename.</param>
        /// <param name="newName">The new name.</param>
        /// <exception cref="ArgumentException">The name is blank, or already used by a different column.</exception>
        internal void RenameColumn(DataColumn column, String newName)
        {
            if (String.IsNullOrWhiteSpace(newName)) { throw new ArgumentException("Column name must not be blank.", nameof(newName)); }
            Int32 ordinal = this.columns.IndexOf(column);
            if (ordinal < 0) { throw new ArgumentException($"Column '{column.ColumnName}' does not belong to this table.", nameof(column)); }
            Int32 existingOrdinal = 0;
            Boolean nameTaken = this.ordinalsByName.TryGetValue(newName, out existingOrdinal);
            if (nameTaken && existingOrdinal != ordinal) { throw new ArgumentException($"Column '{newName}' already exists in this table.", nameof(newName)); }
            this.ordinalsByName.Remove(column.ColumnName);
            column.OverwriteName(newName);
            this.ordinalsByName[newName] = ordinal;
        }

        /// <summary>
        /// Moves a column to a different position, shifting the columns between the two positions.
        /// </summary>
        /// <param name="column">The column to move.</param>
        /// <param name="newOrdinal">The position to move it to.</param>
        /// <exception cref="ArgumentOutOfRangeException">The position is outside the schema.</exception>
        internal void MoveColumn(DataColumn column, Int32 newOrdinal)
        {
            if (newOrdinal < 0 || newOrdinal >= this.columns.Count) { throw new ArgumentOutOfRangeException(nameof(newOrdinal), newOrdinal, $"Ordinal must be between 0 and {this.columns.Count - 1}."); }
            Int32 currentOrdinal = this.columns.IndexOf(column);
            if (currentOrdinal < 0) { throw new ArgumentException($"Column '{column.ColumnName}' does not belong to this table.", nameof(column)); }
            if (currentOrdinal == newOrdinal) { return; }
            this.columns.RemoveAt(currentOrdinal);
            this.columns.Insert(newOrdinal, column);
            ReindexAll();
        }

        /// <summary>
        /// Recomputes whether any column carries a default value. Called when a column's DefaultValue changes and when
        /// the schema itself changes.
        /// </summary>
        internal void RefreshDefaultValueState()
        {
            for (Int32 ordinal = 0; ordinal < this.columns.Count; ordinal++)
            {
                if (this.columns[ordinal].HasDefaultValue)
                {
                    this.hasDefaultValues = true;
                    return;
                }
            }
            this.hasDefaultValues = false;
        }

        /// <summary>
        /// Writes every column's default value into one freshly created row. Called by the row layer only when
        /// <see cref="HasDefaultValues"/> is true.
        /// </summary>
        /// <param name="physicalRowIndex">The new row's physical index.</param>
        internal void ApplyDefaultValues(Int32 physicalRowIndex)
        {
            for (Int32 ordinal = 0; ordinal < this.columns.Count; ordinal++)
            {
                this.columns[ordinal].ApplyDefaultValue(physicalRowIndex);
            }
        }
        #endregion

        #region Private Methods
        // Names the owning table for an error message, matching the shape System.Data uses ("does not belong to
        // table X").
        private String DescribeOwningTable()
        {
            if (this.owningTable == null) { return "(none)"; }
            if (String.IsNullOrEmpty(this.owningTable.TableName)) { return "(unnamed)"; }
            return this.owningTable.TableName;
        }

        // Non-generic enumeration, required by IEnumerable. Column enumeration is a schema-time activity, so the
        // boxing this incurs is not on any hot path.
        IEnumerator IEnumerable.GetEnumerator()
        {
            IEnumerator<DataColumn> enumerator = GetEnumerator();
            return enumerator;
        }

        // Invents a unique column name, matching System.Data's "Column1", "Column2" pattern.
        private String GenerateColumnName()
        {
            Int32 candidateNumber = this.columns.Count + 1;
            String candidate = GENERATED_NAME_PREFIX + candidateNumber.ToString();
            while (this.ordinalsByName.ContainsKey(candidate))
            {
                candidateNumber = candidateNumber + 1;
                candidate = GENERATED_NAME_PREFIX + candidateNumber.ToString();
            }
            return candidate;
        }

        // Rejects a blank or duplicate column name before any column object is built, so a failed Add leaves the
        // schema exactly as it was.
        private void ValidateNewColumnName(String columnName)
        {
            if (String.IsNullOrWhiteSpace(columnName)) { throw new ArgumentException("Column name must not be blank.", nameof(columnName)); }
            Boolean alreadyPresent = this.ordinalsByName.ContainsKey(columnName);
            if (alreadyPresent) { throw new ArgumentException($"Column '{columnName}' already exists in this table.", nameof(columnName)); }
        }

        // Rejects Nullable<T> as a generic column type. Silently accepting DataColumn<Int32?> would double the value
        // array's width and add a second layer of null tracking on top of the bitmap that already exists, which is
        // the opposite of what this library is for. The message tells the caller exactly what to write instead.
        private static void ValidateElementType<T>(String columnName)
        {
            Type underlyingType = Nullable.GetUnderlyingType(typeof(T));
            if (underlyingType != null) { throw new ArgumentException($"Column '{columnName}': use Add<{underlyingType.Name}>(name, allowDBNull: true) rather than a Nullable<{underlyingType.Name}> column type - nullability is tracked by a bit per row, not by widening the value.", nameof(columnName)); }
        }

        // Appends a built column, records its ordinal and attaches it to the owning table. The list and the name map
        // are only ever mutated together, here and in the removal paths.
        private void Register(DataColumn column)
        {
            Int32 ordinal = this.columns.Count;
            this.columns.Add(column);
            this.ordinalsByName[column.ColumnName] = ordinal;
            column.Attach(this.owningTable, ordinal);
            MarkExistingRowsNull(column);
            if (column.HasDefaultValue) { this.hasDefaultValues = true; }
        }

        // Rebuilds the name map and every column's cached ordinal after a removal or a move. O(columns), and only on
        // schema changes - never on a row or cell path.
        private void ReindexAll()
        {
            this.ordinalsByName.Clear();
            for (Int32 ordinal = 0; ordinal < this.columns.Count; ordinal++)
            {
                DataColumn column = this.columns[ordinal];
                this.ordinalsByName[column.ColumnName] = ordinal;
                column.Attach(this.owningTable, ordinal);
            }
        }

        // Marks every row that already existed before this column was added as null in it. Runs only for a nullable
        // column added to a non-empty table - the schema-then-rows ordering that almost every caller uses does no
        // work here at all, because there are no rows yet.
        private void MarkExistingRowsNull(DataColumn column)
        {
            if (this.owningTable == null) { return; }
            if (column.AllowDBNull == false) { return; }
            Int32 existingRowCount = this.owningTable.Rows.PhysicalCount;
            for (Int32 rowIndex = 0; rowIndex < existingRowCount; rowIndex++)
            {
                column.SetValue(rowIndex, null);
            }
        }

        // Casts a column to its typed form, reporting a mismatch with the column's name and both types rather than
        // letting a bare InvalidCastException escape (NFR-12).
        private static DataColumn<T> AsTypedColumn<T>(DataColumn column)
        {
            DataColumn<T> typedColumn = column as DataColumn<T>;
            if (typedColumn == null) { throw new InvalidOperationException($"Column '{column.ColumnName}' holds {column.DataType.FullName} and cannot be accessed as {typeof(T).FullName}."); }
            return typedColumn;
        }
        #endregion
    }
}
