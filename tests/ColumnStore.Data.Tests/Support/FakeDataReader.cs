///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: An in-memory IDataReader test double, so DataTableLoader can be verified against every dedicated typed
//   getter, against nulls, and against providers with and without schema metadata - none of which needs a SQL Server
//   instance (03_Design.md section 6.5).
// Assumptions: Cell values are supplied pre-boxed in Object arrays. That is deliberate: unboxing in a typed getter
//   allocates nothing, so a reader built this way stays invisible to the allocation-delta tests and any bytes they
//   measure belong to the library under test.
// Design Considerations: Schema-metadata behaviour is switchable, because the loader's contract is that nullability
//   inference degrades gracefully. The three cases that matter - metadata present, GetSchemaTable returning null, and
//   GetSchemaTable throwing NotSupportedException the way several providers do - are all reachable from the
//   constructor rather than needing three separate doubles.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Data;

namespace ColumnStore.Data.Tests.Support
{
    /// <summary>
    /// A forward-only <see cref="IDataReader"/> over in-memory rows.
    /// </summary>
    internal sealed class FakeDataReader : IDataReader
    {
        #region Private Members
        // Field names, in ordinal order.
        private readonly String[] fieldNames;

        // Field CLR types, in ordinal order.
        private readonly Type[] fieldTypes;

        // Per-field nullability, reported through the schema table when the behaviour allows it.
        private readonly Boolean[] fieldAllowsNull;

        // The rows to hand out, each an Object array in ordinal order. Null elements mean DBNull.
        private readonly List<Object[]> rows;

        // How GetSchemaTable should behave.
        private readonly SchemaTableBehaviour schemaTableBehaviour;

        // Index of the current row, or -1 before the first Read.
        private Int32 currentRowIndex;

        // Set by Close/Dispose.
        private Boolean isClosed;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a reader over the given schema and rows.
        /// </summary>
        /// <param name="names">Field names.</param>
        /// <param name="types">Field CLR types.</param>
        /// <param name="allowsNull">Per-field nullability for the schema table.</param>
        /// <param name="rows">Rows, each an Object array in ordinal order.</param>
        /// <param name="schemaTableBehaviour">How GetSchemaTable should behave.</param>
        internal FakeDataReader(String[] names, Type[] types, Boolean[] allowsNull, List<Object[]> rows, SchemaTableBehaviour schemaTableBehaviour)
        {
            this.fieldNames = names;
            this.fieldTypes = types;
            this.fieldAllowsNull = allowsNull;
            this.rows = rows;
            this.schemaTableBehaviour = schemaTableBehaviour;
            this.currentRowIndex = -1;
            this.isClosed = false;
        }
        #endregion

        #region Public Properties
        /// <summary>Number of fields.</summary>
        public Int32 FieldCount
        {
            get { return this.fieldNames.Length; }
        }

        /// <summary>Always zero.</summary>
        public Int32 Depth
        {
            get { return 0; }
        }

        /// <summary>True once closed.</summary>
        public Boolean IsClosed
        {
            get { return this.isClosed; }
        }

        /// <summary>Always -1.</summary>
        public Int32 RecordsAffected
        {
            get { return -1; }
        }

        /// <summary>Boxed value at an ordinal.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Object this[Int32 ordinal]
        {
            get { return GetValue(ordinal); }
        }

        /// <summary>Boxed value by field name.</summary>
        /// <param name="name">Field name.</param>
        /// <returns>The value.</returns>
        public Object this[String name]
        {
            get
            {
                Int32 ordinal = GetOrdinal(name);
                return GetValue(ordinal);
            }
        }
        #endregion

        #region Public Methods
        /// <summary>Advances to the next row.</summary>
        /// <returns>True when a row was reached.</returns>
        public Boolean Read()
        {
            Int32 nextRowIndex = this.currentRowIndex + 1;
            if (nextRowIndex >= this.rows.Count) { return false; }
            this.currentRowIndex = nextRowIndex;
            return true;
        }

        /// <summary>Always false - a single result set.</summary>
        /// <returns>False.</returns>
        public Boolean NextResult()
        {
            return false;
        }

        /// <summary>Marks the reader closed.</summary>
        public void Close()
        {
            this.isClosed = true;
        }

        /// <summary>Marks the reader closed.</summary>
        public void Dispose()
        {
            Close();
        }

        /// <summary>Field name at an ordinal.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The name.</returns>
        public String GetName(Int32 ordinal)
        {
            return this.fieldNames[ordinal];
        }

        /// <summary>Ordinal of a named field.</summary>
        /// <param name="name">Field name.</param>
        /// <returns>The ordinal.</returns>
        public Int32 GetOrdinal(String name)
        {
            for (Int32 ordinal = 0; ordinal < this.fieldNames.Length; ordinal++)
            {
                Boolean matches = String.Equals(this.fieldNames[ordinal], name, StringComparison.OrdinalIgnoreCase);
                if (matches) { return ordinal; }
            }
            throw new IndexOutOfRangeException(name);
        }

        /// <summary>CLR type of a field.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The type.</returns>
        public Type GetFieldType(Int32 ordinal)
        {
            return this.fieldTypes[ordinal];
        }

        /// <summary>Type name of a field.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The type's full name.</returns>
        public String GetDataTypeName(Int32 ordinal)
        {
            return this.fieldTypes[ordinal].FullName;
        }

        /// <summary>True when the current row's field is null.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>True when null.</returns>
        public Boolean IsDBNull(Int32 ordinal)
        {
            Object value = RawValue(ordinal);
            return value == null || value is DBNull;
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Boolean GetBoolean(Int32 ordinal)
        {
            return (Boolean)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Byte GetByte(Int32 ordinal)
        {
            return (Byte)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Int16 GetInt16(Int32 ordinal)
        {
            return (Int16)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Int32 GetInt32(Int32 ordinal)
        {
            return (Int32)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Int64 GetInt64(Int32 ordinal)
        {
            return (Int64)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Single GetFloat(Int32 ordinal)
        {
            return (Single)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Double GetDouble(Int32 ordinal)
        {
            return (Double)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Decimal GetDecimal(Int32 ordinal)
        {
            return (Decimal)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public DateTime GetDateTime(Int32 ordinal)
        {
            return (DateTime)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Guid GetGuid(Int32 ordinal)
        {
            return (Guid)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public String GetString(Int32 ordinal)
        {
            return (String)RawValue(ordinal);
        }

        /// <summary>Typed getter.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value.</returns>
        public Char GetChar(Int32 ordinal)
        {
            return (Char)RawValue(ordinal);
        }

        /// <summary>Boxed value of the current row's field.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>The value, or DBNull.</returns>
        public Object GetValue(Int32 ordinal)
        {
            Object value = RawValue(ordinal);
            if (value == null) { return DBNull.Value; }
            return value;
        }

        /// <summary>Copies the current row's values out.</summary>
        /// <param name="values">Destination array.</param>
        /// <returns>Number of values copied.</returns>
        public Int32 GetValues(Object[] values)
        {
            Int32 copyCount = Math.Min(values.Length, this.fieldNames.Length);
            for (Int32 ordinal = 0; ordinal < copyCount; ordinal++)
            {
                values[ordinal] = GetValue(ordinal);
            }
            return copyCount;
        }

        /// <summary>Copies bytes out of a byte-array field.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <param name="dataOffset">Offset in the field value.</param>
        /// <param name="buffer">Destination buffer, or null to query the length.</param>
        /// <param name="bufferOffset">Offset in the destination.</param>
        /// <param name="length">Maximum elements to copy.</param>
        /// <returns>Elements copied, or the field length.</returns>
        public Int64 GetBytes(Int32 ordinal, Int64 dataOffset, Byte[] buffer, Int32 bufferOffset, Int32 length)
        {
            Byte[] fieldValue = (Byte[])RawValue(ordinal);
            if (fieldValue == null) { return 0L; }
            if (buffer == null) { return fieldValue.LongLength; }
            Int32 copyCount = Math.Min(length, fieldValue.Length - (Int32)dataOffset);
            Array.Copy(fieldValue, (Int32)dataOffset, buffer, bufferOffset, copyCount);
            return copyCount;
        }

        /// <summary>Copies characters out of a string field.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <param name="dataOffset">Offset in the field value.</param>
        /// <param name="buffer">Destination buffer, or null to query the length.</param>
        /// <param name="bufferOffset">Offset in the destination.</param>
        /// <param name="length">Maximum elements to copy.</param>
        /// <returns>Elements copied, or the field length.</returns>
        public Int64 GetChars(Int32 ordinal, Int64 dataOffset, Char[] buffer, Int32 bufferOffset, Int32 length)
        {
            String fieldValue = (String)RawValue(ordinal);
            if (fieldValue == null) { return 0L; }
            if (buffer == null) { return fieldValue.Length; }
            Int32 copyCount = Math.Min(length, fieldValue.Length - (Int32)dataOffset);
            fieldValue.CopyTo((Int32)dataOffset, buffer, bufferOffset, copyCount);
            return copyCount;
        }

        /// <summary>Not supported.</summary>
        /// <param name="ordinal">Field index.</param>
        /// <returns>Never returns.</returns>
        public IDataReader GetData(Int32 ordinal)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Schema metadata, or the configured failure mode.
        /// </summary>
        /// <returns>A schema table, or null.</returns>
        public System.Data.DataTable GetSchemaTable()
        {
            if (this.schemaTableBehaviour == SchemaTableBehaviour.Throws) { throw new NotSupportedException("This provider does not support schema metadata."); }
            if (this.schemaTableBehaviour == SchemaTableBehaviour.ReturnsNull) { return null; }
            Boolean includeNullability = this.schemaTableBehaviour == SchemaTableBehaviour.Supported;
            System.Data.DataTable schemaTable = BuildSchemaTable(includeNullability);
            return schemaTable;
        }
        #endregion

        #region Private Methods
        // Returns the raw stored value for the current row, without the DBNull substitution GetValue performs.
        private Object RawValue(Int32 ordinal)
        {
            if (this.currentRowIndex < 0) { throw new InvalidOperationException("Read must be called before reading a value."); }
            Object[] row = this.rows[this.currentRowIndex];
            return row[ordinal];
        }

        // Builds an ADO.NET-shaped schema table, optionally omitting the AllowDBNull column so the loader's
        // degrade-gracefully path can be exercised.
        private System.Data.DataTable BuildSchemaTable(Boolean includeNullability)
        {
            System.Data.DataTable schemaTable = new System.Data.DataTable("SchemaTable");
            schemaTable.Columns.Add("ColumnName", typeof(String));
            schemaTable.Columns.Add("ColumnOrdinal", typeof(Int32));
            schemaTable.Columns.Add("DataType", typeof(Type));
            if (includeNullability) { schemaTable.Columns.Add("AllowDBNull", typeof(Boolean)); }
            for (Int32 ordinal = 0; ordinal < this.fieldNames.Length; ordinal++)
            {
                System.Data.DataRow schemaRow = schemaTable.NewRow();
                schemaRow["ColumnName"] = this.fieldNames[ordinal];
                schemaRow["ColumnOrdinal"] = ordinal;
                schemaRow["DataType"] = this.fieldTypes[ordinal];
                if (includeNullability) { schemaRow["AllowDBNull"] = this.fieldAllowsNull[ordinal]; }
                schemaTable.Rows.Add(schemaRow);
            }
            return schemaTable;
        }
        #endregion
    }
}
