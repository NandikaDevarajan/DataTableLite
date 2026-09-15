///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: An IDataReader over an already-materialised columnar snapshot, whose per-cell cost is as close to zero as
//   a reader can be: one array index for a value, one array index for a null test.
// Assumptions: Built from a System.Data.DataTable snapshot, so the shapes under test stay defined in one place
//   (TableShape). Only the members the loader actually calls are implemented; the rest throw.
// Design Considerations: This exists because DataTableReader - the obvious stand-in - has an EXPENSIVE IsDBNull, and
//   measuring a loader against it prices the reader rather than the loader. SqlDataReader's IsDBNull, on an already
//   fetched row, is a cheap read of buffered state, and a loader optimisation that only looks good against an
//   expensive IsDBNull will not survive contact with a real provider. Anything compared here is therefore compared
//   under the pessimistic assumption that the reader gives the loader nothing to hide behind.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// A minimal, very fast <see cref="IDataReader"/> over a columnar snapshot of a table.
    /// </summary>
    public sealed class BufferedColumnReader : IDataReader
    {
        #region Private Members
        // Field names, in ordinal order.
        private readonly String[] fieldNames;

        // Field types, in ordinal order.
        private readonly Type[] fieldTypes;

        // One typed array per field, holding every row's value for that field.
        private readonly Array[] fieldValues;

        // One flag array per field: true where that row's cell is null.
        private readonly Boolean[][] fieldNulls;

        // Number of rows in the snapshot.
        private readonly Int32 rowCount;

        // Index of the row the reader is positioned on, or -1 before the first Read.
        private Int32 currentRowIndex;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Snapshots a <see cref="System.Data.DataTable"/> into columnar arrays.
        /// </summary>
        /// <param name="source">The table to snapshot.</param>
        /// <exception cref="ArgumentNullException">The source is null.</exception>
        public BufferedColumnReader(System.Data.DataTable source)
        {
            if (source == null) { throw new ArgumentNullException(nameof(source)); }
            Int32 columnCount = source.Columns.Count;
            this.rowCount = source.Rows.Count;
            this.fieldNames = new String[columnCount];
            this.fieldTypes = new Type[columnCount];
            this.fieldValues = new Array[columnCount];
            this.fieldNulls = new Boolean[columnCount][];
            for (Int32 ordinal = 0; ordinal < columnCount; ordinal++)
            {
                System.Data.DataColumn column = source.Columns[ordinal];
                this.fieldNames[ordinal] = column.ColumnName;
                this.fieldTypes[ordinal] = column.DataType;
                this.fieldValues[ordinal] = Array.CreateInstance(column.DataType, this.rowCount);
                this.fieldNulls[ordinal] = new Boolean[this.rowCount];
                FillColumn(source, ordinal);
            }
            this.currentRowIndex = -1;
        }
        #endregion

        #region Public Properties
        /// <summary>Number of fields the reader exposes.</summary>
        public Int32 FieldCount
        {
            get { return this.fieldNames.Length; }
        }

        /// <summary>Always zero; nested readers are not supported.</summary>
        public Int32 Depth
        {
            get { return 0; }
        }

        /// <summary>Always false; the reader holds a snapshot and never closes.</summary>
        public Boolean IsClosed
        {
            get { return false; }
        }

        /// <summary>Always -1; nothing is modified by reading.</summary>
        public Int32 RecordsAffected
        {
            get { return -1; }
        }

        /// <summary>Boxing accessor by ordinal.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value, or <see cref="DBNull.Value"/>.</returns>
        public Object this[Int32 ordinal]
        {
            get { return GetValue(ordinal); }
        }

        /// <summary>Boxing accessor by name.</summary>
        /// <param name="name">Field name.</param>
        /// <returns>The value, or <see cref="DBNull.Value"/>.</returns>
        public Object this[String name]
        {
            get { return GetValue(GetOrdinal(name)); }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Rewinds the reader so the same snapshot can be replayed, which is what makes repeated timing possible
        /// without rebuilding the data.
        /// </summary>
        public void Reset()
        {
            this.currentRowIndex = -1;
        }

        /// <summary>Advances to the next row.</summary>
        /// <returns>True while a row is available.</returns>
        public Boolean Read()
        {
            this.currentRowIndex = this.currentRowIndex + 1;
            return this.currentRowIndex < this.rowCount;
        }

        /// <summary>Null test. One array index - the whole point of this class.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>True when the cell is null.</returns>
        public Boolean IsDBNull(Int32 ordinal)
        {
            return this.fieldNulls[ordinal][this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Int32 GetInt32(Int32 ordinal)
        {
            return ((Int32[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Int64 GetInt64(Int32 ordinal)
        {
            return ((Int64[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Int16 GetInt16(Int32 ordinal)
        {
            return ((Int16[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Byte GetByte(Int32 ordinal)
        {
            return ((Byte[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Decimal GetDecimal(Int32 ordinal)
        {
            return ((Decimal[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Double GetDouble(Int32 ordinal)
        {
            return ((Double[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter. IDataRecord spells this one GetFloat.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Single GetFloat(Int32 ordinal)
        {
            return ((Single[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Boolean GetBoolean(Int32 ordinal)
        {
            return ((Boolean[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public DateTime GetDateTime(Int32 ordinal)
        {
            return ((DateTime[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Guid GetGuid(Int32 ordinal)
        {
            return ((Guid[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public String GetString(Int32 ordinal)
        {
            return ((String[])this.fieldValues[ordinal])[this.currentRowIndex];
        }

        /// <summary>Boxing getter, used by the loader's Object fallback.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value, or <see cref="DBNull.Value"/> when the cell is null.</returns>
        public Object GetValue(Int32 ordinal)
        {
            if (this.fieldNulls[ordinal][this.currentRowIndex]) { return DBNull.Value; }
            return this.fieldValues[ordinal].GetValue(this.currentRowIndex);
        }

        /// <summary>Field name for an ordinal.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The name.</returns>
        public String GetName(Int32 ordinal)
        {
            return this.fieldNames[ordinal];
        }

        /// <summary>Field type for an ordinal.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The CLR type.</returns>
        public Type GetFieldType(Int32 ordinal)
        {
            return this.fieldTypes[ordinal];
        }

        /// <summary>Ordinal for a field name.</summary>
        /// <param name="name">Field name.</param>
        /// <returns>The ordinal, or -1.</returns>
        public Int32 GetOrdinal(String name)
        {
            for (Int32 ordinal = 0; ordinal < this.fieldNames.Length; ordinal++)
            {
                if (String.Equals(this.fieldNames[ordinal], name, StringComparison.Ordinal)) { return ordinal; }
            }
            return -1;
        }

        /// <summary>Provider type name for an ordinal.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The CLR type's name.</returns>
        public String GetDataTypeName(Int32 ordinal)
        {
            return this.fieldTypes[ordinal].Name;
        }

        /// <summary>Copies the current row's values out.</summary>
        /// <param name="values">Destination array.</param>
        /// <returns>How many values were written.</returns>
        public Int32 GetValues(Object[] values)
        {
            Int32 copied = Math.Min(values.Length, this.fieldNames.Length);
            for (Int32 ordinal = 0; ordinal < copied; ordinal++)
            {
                values[ordinal] = GetValue(ordinal);
            }
            return copied;
        }

        /// <summary>Not supported.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>Never returns.</returns>
        public Char GetChar(Int32 ordinal)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <param name="dataOffset">Source offset.</param>
        /// <param name="buffer">Destination.</param>
        /// <param name="bufferOffset">Destination offset.</param>
        /// <param name="length">Bytes to copy.</param>
        /// <returns>Never returns.</returns>
        public Int64 GetBytes(Int32 ordinal, Int64 dataOffset, Byte[] buffer, Int32 bufferOffset, Int32 length)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <param name="dataOffset">Source offset.</param>
        /// <param name="buffer">Destination.</param>
        /// <param name="bufferOffset">Destination offset.</param>
        /// <param name="length">Characters to copy.</param>
        /// <returns>Never returns.</returns>
        public Int64 GetChars(Int32 ordinal, Int64 dataOffset, Char[] buffer, Int32 bufferOffset, Int32 length)
        {
            throw new NotSupportedException();
        }

        /// <summary>Not supported.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>Never returns.</returns>
        public IDataReader GetData(Int32 ordinal)
        {
            throw new NotSupportedException();
        }

        /// <summary>No schema metadata is offered, so the loader's inference degrades gracefully.</summary>
        /// <returns>Null.</returns>
        public System.Data.DataTable GetSchemaTable()
        {
            return null;
        }

        /// <summary>Single result set only.</summary>
        /// <returns>False.</returns>
        public Boolean NextResult()
        {
            return false;
        }

        /// <summary>No-op.</summary>
        public void Close()
        {
        }

        /// <summary>No-op; the reader owns nothing unmanaged.</summary>
        public void Dispose()
        {
        }
        #endregion

        #region Private Methods
        // Copies one of the source table's columns into the typed array and null flags for that ordinal.
        private void FillColumn(System.Data.DataTable source, Int32 ordinal)
        {
            Array destination = this.fieldValues[ordinal];
            Boolean[] nulls = this.fieldNulls[ordinal];
            for (Int32 rowIndex = 0; rowIndex < this.rowCount; rowIndex++)
            {
                Object value = source.Rows[rowIndex][ordinal];
                if (value == null || value is DBNull)
                {
                    nulls[rowIndex] = true;
                    continue;
                }
                destination.SetValue(value, rowIndex);
            }
        }
        #endregion
    }
}
