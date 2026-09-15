///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: A forward-only DbDataReader over a ColumnStore.Data table's visible rows. This is what makes
//   SqlBulkCopy.WriteToServer work against a columnar table (FR-20), and it is useful anywhere a reader is the
//   expected input shape - including as the value of a Structured (table-valued) parameter, which SQL Server accepts
//   as a DbDataReader but not as a bare IDataReader. Deriving from DbDataReader rather than implementing IDataReader
//   is what buys that second use: DbDataReader already implements IDataReader, so nothing that worked before stops
//   working, and the two extra members it demands - HasRows and GetEnumerator - are a row count and a delegation to
//   ADO.NET's own DbEnumerator.
// Assumptions: The table is not modified while the reader is open - the same single-writer assumption the rest of the
//   library makes. Deleted rows are skipped automatically because the reader advances through the table's own row
//   enumerator. The reader does not own the table and disposing it releases nothing.
// Design Considerations: IsDBNull delegates to IDataColumn.IsNull, which is precisely why IsNull lives on the
//   non-generic column interface (03_Design.md section 2.1) - without it, every null test here would need a per-cell
//   type switch.
//   ON "RESOLVE THE TYPED COLUMN ONCE": 03_Design.md section 4.2 asks each typed getter to resolve its backing
//   DataColumn<T> at construction. What is stored here instead is the IDataColumn[] resolved once at construction,
//   with each typed getter performing one interface cast per call. The reason is that a genuinely per-type resolved
//   layout needs one parallel array per supported type (eleven arrays, mostly null entries) to hand a typed getter a
//   pre-cast reference, because a cast is the only way to recover T from IDataColumn at the call site. The cast is a
//   single castclass against a sealed type: no reflection, no type search, no allocation and no boxing, which is what
//   NFR-7 actually requires. The eleven-array variant was judged worse code for an unmeasurable gain against
//   SqlBulkCopy's own per-row cost. Nothing on this path looks at a Type or boxes a value.
//   GetValue and GetValues box by definition - that is the IDataRecord contract, and SqlBulkCopy uses them for types
//   without a dedicated getter. Nothing can be done about that here, and NFR-7 explicitly allows what the target API
//   forces.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections;
using System.Data;
using System.Data.Common;

namespace ColumnStore.Data.SqlServer
{
    /// <summary>
    /// Presents a <see cref="DataTable"/>'s visible rows as a forward-only <see cref="DbDataReader"/>.
    /// </summary>
    public sealed class DataTableDataReader : DbDataReader
    {
        #region Private Constants
        // Sentinel for "not positioned on a row" - before the first Read and after the last.
        private const Int32 NO_CURRENT_ROW = -1;

        // Schema-table column holding a field's name. Named by ADO.NET's GetSchemaTable contract; only the subset of
        // that contract consumers actually read is produced.
        private const String SCHEMA_COLUMN_NAME = "ColumnName";

        // Schema-table column holding a field's zero-based ordinal.
        private const String SCHEMA_COLUMN_ORDINAL = "ColumnOrdinal";

        // Schema-table column holding a field's maximum size. Always reported as -1 ("unknown"), because a columnar
        // store has no declared width - a String column is not a varchar(50).
        private const String SCHEMA_COLUMN_SIZE = "ColumnSize";

        // Schema-table column holding a field's CLR type.
        private const String SCHEMA_DATA_TYPE = "DataType";

        // Schema-table column holding a field's nullability.
        private const String SCHEMA_ALLOW_DBNULL = "AllowDBNull";
        #endregion

        #region Private Members
        // The table being read. Held only to synthesize the schema table and to keep the row enumerator meaningful.
        private readonly DataTable table;

        // The table's columns, resolved once at construction so no per-cell lookup goes through the collection's
        // indexer or its name dictionary.
        private readonly IDataColumn[] columns;

        // Walks visible physical slots, skipping tombstones. A struct enumerator held by value: advancing it
        // allocates nothing.
        private DataRowCollection.Enumerator rowEnumerator;

        // Physical slot of the row the reader is currently positioned on, or NO_CURRENT_ROW.
        private Int32 currentPhysicalRowIndex;

        // Set by Close/Dispose. A closed reader rejects every data access.
        private Boolean isClosed;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a reader positioned before the table's first visible row.
        /// </summary>
        /// <param name="table">The table to read.</param>
        /// <exception cref="ArgumentNullException">The table is null.</exception>
        public DataTableDataReader(DataTable table)
        {
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            this.table = table;
            this.columns = new IDataColumn[table.Columns.Count];
            for (Int32 ordinal = 0; ordinal < this.columns.Length; ordinal++)
            {
                this.columns[ordinal] = table.Columns[ordinal];
            }
            this.rowEnumerator = table.Rows.GetEnumerator();
            this.currentPhysicalRowIndex = NO_CURRENT_ROW;
            this.isClosed = false;
        }
        #endregion

        #region Public Properties
        /// <summary>Number of fields, one per table column.</summary>
        public override Int32 FieldCount
        {
            get { return this.columns.Length; }
        }

        /// <summary>Always zero - this reader exposes no nested rowsets.</summary>
        public override Int32 Depth
        {
            get { return 0; }
        }

        /// <summary>True once <see cref="Close"/> or <see cref="Dispose"/> has been called.</summary>
        public override Boolean IsClosed
        {
            get { return this.isClosed; }
        }

        /// <summary>Always -1 - this reader is a read-only projection and affects no rows.</summary>
        public override Int32 RecordsAffected
        {
            get { return -1; }
        }

        /// <summary>
        /// The current row's value at the given field index, boxed.
        /// </summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The boxed value, or <see cref="DBNull.Value"/>.</returns>
        public override Object this[Int32 ordinal]
        {
            get { return GetValue(ordinal); }
        }

        /// <summary>
        /// The current row's value in the named field, boxed.
        /// </summary>
        /// <param name="name">The field name.</param>
        /// <returns>The boxed value, or <see cref="DBNull.Value"/>.</returns>
        public override Object this[String name]
        {
            get
            {
                Int32 ordinal = GetOrdinal(name);
                return GetValue(ordinal);
            }
        }
        /// <summary>
        /// True when the table has at least one visible row. Answered from the table's live count rather than
        /// remembered at construction, which matches what a provider reader reports and costs nothing to keep exact.
        /// </summary>
        public override Boolean HasRows
        {
            get { return this.table.Rows.Count > 0; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Enumerates the reader's remaining rows as <see cref="DbDataRecord"/>s, which is what
        /// <see cref="DbDataReader"/> requires of every implementation. ADO.NET's own DbEnumerator does the work, so
        /// the records it yields behave exactly as a provider reader's would.
        /// </summary>
        /// <returns>An enumerator over the remaining rows.</returns>
        public override IEnumerator GetEnumerator()
        {
            return new DbEnumerator(this, false);
        }

        /// <summary>
        /// Advances to the next visible row, skipping deleted rows.
        /// </summary>
        /// <returns>True when a row was reached.</returns>
        /// <exception cref="InvalidOperationException">The reader is closed.</exception>
        public override Boolean Read()
        {
            if (this.isClosed) { throw new InvalidOperationException("This reader has been closed."); }
            Boolean moved = this.rowEnumerator.MoveNext();
            if (moved == false)
            {
                this.currentPhysicalRowIndex = NO_CURRENT_ROW;
                return false;
            }
            DataRow row = this.rowEnumerator.Current;
            this.currentPhysicalRowIndex = row.RowIndex;
            return true;
        }

        /// <summary>
        /// Always false - a table is a single result set.
        /// </summary>
        /// <returns>False.</returns>
        public override Boolean NextResult()
        {
            return false;
        }

        /// <summary>
        /// Closes the reader. The underlying table is untouched.
        /// </summary>
        public override void Close()
        {
            this.isClosed = true;
            this.currentPhysicalRowIndex = NO_CURRENT_ROW;
        }

        /// <summary>
        /// Closes the reader. Equivalent to <see cref="Close"/>; nothing is owned, so nothing is released - the
        /// table this reader walks outlives it and is not touched.
        /// </summary>
        /// <param name="disposing">True when called from Dispose rather than from a finalizer.</param>
        protected override void Dispose(Boolean disposing)
        {
            if (disposing) { Close(); }
            base.Dispose(disposing);
        }

        /// <summary>The name of the field at the given index.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The column name.</returns>
        public override String GetName(Int32 ordinal)
        {
            IDataColumn column = ColumnAt(ordinal);
            return column.ColumnName;
        }

        /// <summary>The index of the named field, compared case-insensitively.</summary>
        /// <param name="name">The field name.</param>
        /// <returns>The zero-based field index.</returns>
        /// <exception cref="IndexOutOfRangeException">No field has that name - the exception type ADO.NET consumers
        /// expect from GetOrdinal.</exception>
        public override Int32 GetOrdinal(String name)
        {
            if (name == null) { throw new ArgumentNullException(nameof(name)); }
            Int32 ordinal = this.table.Columns.IndexOf(name);
            if (ordinal < 0) { throw new IndexOutOfRangeException($"Field '{name}' does not exist in this reader."); }
            return ordinal;
        }

        /// <summary>The CLR type of the field's values.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The column's data type.</returns>
        public override Type GetFieldType(Int32 ordinal)
        {
            IDataColumn column = ColumnAt(ordinal);
            return column.DataType;
        }

        /// <summary>The field's data type name, as its CLR type's full name.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The type name.</returns>
        public override String GetDataTypeName(Int32 ordinal)
        {
            IDataColumn column = ColumnAt(ordinal);
            return column.DataType.FullName;
        }

        /// <summary>True when the current row's field is null.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>True when the cell is null.</returns>
        public override Boolean IsDBNull(Int32 ordinal)
        {
            IDataColumn column = ColumnAt(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.IsNull(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="Boolean"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Boolean GetBoolean(Int32 ordinal)
        {
            IDataColumn<Boolean> column = TypedColumnAt<Boolean>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="Byte"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Byte GetByte(Int32 ordinal)
        {
            IDataColumn<Byte> column = TypedColumnAt<Byte>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as an <see cref="Int16"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Int16 GetInt16(Int32 ordinal)
        {
            IDataColumn<Int16> column = TypedColumnAt<Int16>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as an <see cref="Int32"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Int32 GetInt32(Int32 ordinal)
        {
            IDataColumn<Int32> column = TypedColumnAt<Int32>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as an <see cref="Int64"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Int64 GetInt64(Int32 ordinal)
        {
            IDataColumn<Int64> column = TypedColumnAt<Int64>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="Single"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Single GetFloat(Int32 ordinal)
        {
            IDataColumn<Single> column = TypedColumnAt<Single>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="Double"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Double GetDouble(Int32 ordinal)
        {
            IDataColumn<Double> column = TypedColumnAt<Double>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="Decimal"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Decimal GetDecimal(Int32 ordinal)
        {
            IDataColumn<Decimal> column = TypedColumnAt<Decimal>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="DateTime"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override DateTime GetDateTime(Int32 ordinal)
        {
            IDataColumn<DateTime> column = TypedColumnAt<DateTime>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="Guid"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Guid GetGuid(Int32 ordinal)
        {
            IDataColumn<Guid> column = TypedColumnAt<Guid>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="String"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override String GetString(Int32 ordinal)
        {
            IDataColumn<String> column = TypedColumnAt<String>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>Reads the current row's field as a <see cref="Char"/>.</summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The value.</returns>
        public override Char GetChar(Int32 ordinal)
        {
            IDataColumn<Char> column = TypedColumnAt<Char>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            return column.Get(rowIndex);
        }

        /// <summary>
        /// The current row's value at the given field index, boxed, with <see cref="DBNull.Value"/> for a null cell.
        /// Boxes value types - that is the <see cref="IDataRecord"/> contract.
        /// </summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>The boxed value, or <see cref="DBNull.Value"/>.</returns>
        public override Object GetValue(Int32 ordinal)
        {
            IDataColumn column = ColumnAt(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            Object value = column.GetValue(rowIndex);
            if (value == null) { return DBNull.Value; }
            return value;
        }

        /// <summary>
        /// Copies the current row's values into the supplied array, as far as it will hold them.
        /// </summary>
        /// <param name="values">Destination array.</param>
        /// <returns>The number of values copied.</returns>
        /// <exception cref="ArgumentNullException">The array is null.</exception>
        public override Int32 GetValues(Object[] values)
        {
            if (values == null) { throw new ArgumentNullException(nameof(values)); }
            Int32 copyCount = Math.Min(values.Length, this.columns.Length);
            for (Int32 ordinal = 0; ordinal < copyCount; ordinal++)
            {
                values[ordinal] = GetValue(ordinal);
            }
            return copyCount;
        }

        /// <summary>
        /// Copies bytes out of a <see cref="Byte"/> array field. Passing a null buffer returns the field's total
        /// length, per the <see cref="IDataRecord"/> contract.
        /// </summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <param name="dataOffset">Offset within the field's value to start from.</param>
        /// <param name="buffer">Destination buffer, or null to query the length.</param>
        /// <param name="bufferOffset">Offset within the destination buffer.</param>
        /// <param name="length">Maximum number of bytes to copy.</param>
        /// <returns>The number of bytes copied, or the field's length when the buffer is null.</returns>
        public override Int64 GetBytes(Int32 ordinal, Int64 dataOffset, Byte[] buffer, Int32 bufferOffset, Int32 length)
        {
            IDataColumn<Byte[]> column = TypedColumnAt<Byte[]>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            Byte[] fieldValue = column.Get(rowIndex);
            if (fieldValue == null) { return 0L; }
            if (buffer == null) { return fieldValue.LongLength; }
            Int32 copyCount = CalculateCopyCount(fieldValue.Length, dataOffset, buffer.Length, bufferOffset, length);
            Array.Copy(fieldValue, (Int32)dataOffset, buffer, bufferOffset, copyCount);
            return copyCount;
        }

        /// <summary>
        /// Copies characters out of a <see cref="String"/> field. Passing a null buffer returns the field's total
        /// length, per the <see cref="IDataRecord"/> contract.
        /// </summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <param name="dataOffset">Offset within the field's value to start from.</param>
        /// <param name="buffer">Destination buffer, or null to query the length.</param>
        /// <param name="bufferOffset">Offset within the destination buffer.</param>
        /// <param name="length">Maximum number of characters to copy.</param>
        /// <returns>The number of characters copied, or the field's length when the buffer is null.</returns>
        public override Int64 GetChars(Int32 ordinal, Int64 dataOffset, Char[] buffer, Int32 bufferOffset, Int32 length)
        {
            IDataColumn<String> column = TypedColumnAt<String>(ordinal);
            Int32 rowIndex = CurrentRowIndex();
            String fieldValue = column.Get(rowIndex);
            if (fieldValue == null) { return 0L; }
            if (buffer == null) { return fieldValue.Length; }
            Int32 copyCount = CalculateCopyCount(fieldValue.Length, dataOffset, buffer.Length, bufferOffset, length);
            fieldValue.CopyTo((Int32)dataOffset, buffer, bufferOffset, copyCount);
            return copyCount;
        }

        /// <summary>
        /// Not supported - a table has no nested rowsets.
        /// </summary>
        /// <param name="ordinal">Zero-based field index.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotSupportedException">Always.</exception>
        protected override DbDataReader GetDbDataReader(Int32 ordinal)
        {
            throw new NotSupportedException("ColumnStore.Data tables have no nested rowsets.");
        }

        /// <summary>
        /// Builds the ADO.NET schema table describing this reader's fields. Produced on demand and not cached, since
        /// consumers ask for it at most once per reader.
        /// </summary>
        /// <returns>A schema table with one row per field.</returns>
        public override System.Data.DataTable GetSchemaTable()
        {
            System.Data.DataTable schemaTable = new System.Data.DataTable("SchemaTable");
            schemaTable.Columns.Add(SCHEMA_COLUMN_NAME, typeof(String));
            schemaTable.Columns.Add(SCHEMA_COLUMN_ORDINAL, typeof(Int32));
            schemaTable.Columns.Add(SCHEMA_COLUMN_SIZE, typeof(Int32));
            schemaTable.Columns.Add(SCHEMA_DATA_TYPE, typeof(Type));
            schemaTable.Columns.Add(SCHEMA_ALLOW_DBNULL, typeof(Boolean));
            for (Int32 ordinal = 0; ordinal < this.columns.Length; ordinal++)
            {
                IDataColumn column = this.columns[ordinal];
                Object[] schemaValues = new Object[] { column.ColumnName, ordinal, -1, column.DataType, column.AllowDBNull };
                schemaTable.Rows.Add(schemaValues);
            }
            return schemaTable;
        }
        #endregion

        #region Private Methods
        // Returns the column at an ordinal, rejecting an out-of-range index with the exception type ADO.NET consumers
        // expect from a field accessor.
        private IDataColumn ColumnAt(Int32 ordinal)
        {
            if (ordinal < 0 || ordinal >= this.columns.Length) { throw new IndexOutOfRangeException($"Field Index: {ordinal}"); }
            return this.columns[ordinal];
        }

        // Returns the column at an ordinal as its typed interface. One castclass against a sealed type - no
        // reflection, no allocation, no boxing. A mismatch names the field and both types rather than surfacing a
        // bare InvalidCastException from the cast itself.
        private IDataColumn<T> TypedColumnAt<T>(Int32 ordinal)
        {
            IDataColumn column = ColumnAt(ordinal);
            IDataColumn<T> typedColumn = column as IDataColumn<T>;
            if (typedColumn == null) { throw new InvalidCastException($"Field '{column.ColumnName}' holds {column.DataType.FullName} and cannot be read as {typeof(T).FullName}."); }
            return typedColumn;
        }

        // Returns the physical slot the reader is positioned on, rejecting access before the first Read, after the
        // last row, and after Close.
        private Int32 CurrentRowIndex()
        {
            if (this.isClosed) { throw new InvalidOperationException("This reader has been closed."); }
            if (this.currentPhysicalRowIndex == NO_CURRENT_ROW) { throw new InvalidOperationException("The reader is not positioned on a row. Call Read first."); }
            return this.currentPhysicalRowIndex;
        }

        // Works out how many elements a GetBytes/GetChars call may copy, validating every offset and length first so a
        // bad request fails with a named argument rather than inside Array.Copy.
        private static Int32 CalculateCopyCount(Int32 fieldLength, Int64 dataOffset, Int32 bufferLength, Int32 bufferOffset, Int32 length)
        {
            if (dataOffset < 0 || dataOffset > fieldLength) { throw new ArgumentOutOfRangeException(nameof(dataOffset), dataOffset, $"Data offset must be between 0 and {fieldLength}."); }
            if (bufferOffset < 0 || bufferOffset > bufferLength) { throw new ArgumentOutOfRangeException(nameof(bufferOffset), bufferOffset, $"Buffer offset must be between 0 and {bufferLength}."); }
            if (length < 0) { throw new ArgumentOutOfRangeException(nameof(length), length, "Length must not be negative."); }
            Int64 availableInField = fieldLength - dataOffset;
            Int32 availableInBuffer = bufferLength - bufferOffset;
            Int64 copyCount = Math.Min(length, availableInField);
            copyCount = Math.Min(copyCount, availableInBuffer);
            return (Int32)copyCount;
        }
        #endregion
    }
}
