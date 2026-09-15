///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Sends a ColumnStore.Data table to SQL Server in both supported shapes - as a stream of SqlDataRecords for a
//   table-valued parameter (FR-19), and as an IDataReader for SqlBulkCopy.WriteToServer (FR-20) - populating each
//   from typed column storage without boxing on the non-null path (FR-21, FR-22).
// Assumptions: The table is not modified while a returned sequence or reader is being consumed - the same
//   single-writer assumption the rest of the library makes. Deleted rows are skipped by both paths, because both walk
//   the table's own row enumerator over logical rows.
// Design Considerations: A ColumnStore.Data.DataTable cannot be assigned to SqlParameter.Value the way
//   System.Data.DataTable can - ADO.NET only understands its own type - so this class is the bridge, and it bridges
//   by streaming rather than by materialising an intermediate System.Data.DataTable, which would reintroduce every
//   per-row allocation the library exists to remove.
//   AsSqlDataRecords REUSES ONE SqlDataRecord, repopulating it per row. That is the documented ADO.NET streaming
//   pattern for table-valued parameters and it is what keeps a million-row TVP allocation free. It also means the
//   sequence must be consumed once, streaming: buffering it yields N references to the same record. The XML
//   documentation says so, and readme.txt says so, because it is the one surprising thing about this API.
//   SqlMetaData is caller-supplied (03_Design.md section 4.2, open question 3): precision, scale and length cannot be
//   derived from a CLR type, since decimal(18,2) and decimal(38,6) are both Decimal, and guessing wrong on a TVP
//   silently truncates money. InferSqlMetaData exists as an explicitly best-guess convenience with its guesses
//   spelled out in its own documentation - it deliberately chooses maximum widths over plausible ones, so that a bad
//   guess costs bandwidth rather than data.
//   Per-field setters are built once per call, capturing the field index, the already-cast typed column and the
//   SqlDataRecord setter to invoke. The per-row loop invokes delegates and nothing else - no Type inspection, no
//   cast, no box (NFR-7).
//   MEASURED EXCEPTION: streaming allocates exactly zero bytes per row for Int32, Int64, Int16, Byte, Boolean,
//   Double, Single, DateTime, Guid and String columns. A Decimal column costs about forty bytes per row inside
//   Microsoft.Data.SqlClient's own SqlDataRecord.SetDecimal, which no calling convention avoids. That is the
//   "beyond what the target API itself requires" clause of NFR-7 in practice, and it is asserted as a bound in
//   DataTableSqlWriterTests so a regression on this side of the boundary still fails.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace ColumnStore.Data.SqlServer
{
    /// <summary>
    /// Adapts a <see cref="DataTable"/> for SQL Server writes: table-valued parameters and bulk copy.
    /// </summary>
    public sealed class DataTableSqlWriter
    {
        #region Private Constants
        // Length marker meaning "max" for SQL Server's variable-length types (nvarchar(max), varbinary(max)). Used
        // only by the best-guess metadata helper.
        private const Int64 MAX_LENGTH_MARKER = -1L;

        // Precision used by the best-guess metadata helper for Decimal columns. The maximum SQL Server allows, so an
        // inferred TVP cannot overflow a value the CLR was able to hold.
        private const Byte INFERRED_DECIMAL_PRECISION = 38;

        // Scale used by the best-guess metadata helper for Decimal columns. Six digits covers currency and unit-rate
        // data; anything more precise needs caller-supplied metadata.
        private const Byte INFERRED_DECIMAL_SCALE = 6;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a writer. The type is stateless - one instance can adapt any number of tables.
        /// </summary>
        public DataTableSqlWriter()
        {
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Streams the table's visible rows as <see cref="SqlDataRecord"/>s, ready to be assigned to a
        /// <see cref="Microsoft.Data.SqlClient.SqlParameter"/> whose <c>SqlDbType</c> is <c>Structured</c>.
        /// <para>
        /// The sequence yields the SAME record instance repopulated per row, which is the ADO.NET streaming
        /// convention and what keeps a large table's send allocation free. Consume it exactly once, streaming; do not
        /// buffer it.
        /// </para>
        /// </summary>
        /// <param name="table">The table to send.</param>
        /// <param name="metadata">One entry per TVP column, in TVP column order. Each entry's name must match a
        /// column of the table, compared case-insensitively.</param>
        /// <returns>A lazily evaluated sequence of records, one per visible row.</returns>
        /// <exception cref="ArgumentNullException">The table or the metadata array is null.</exception>
        /// <exception cref="ArgumentException">The metadata array is empty, or an entry names a column the table does
        /// not have.</exception>
        public IEnumerable<SqlDataRecord> AsSqlDataRecords(DataTable table, SqlMetaData[] metadata)
        {
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            if (metadata == null) { throw new ArgumentNullException(nameof(metadata)); }
            if (metadata.Length == 0) { throw new ArgumentException("At least one metadata entry is required to build a table-valued parameter.", nameof(metadata)); }
            Action<SqlDataRecord, Int32>[] fieldSetters = BuildFieldSetters(table, metadata);
            return IterateRecords(metadata, table, fieldSetters);
        }

        /// <summary>
        /// Presents the table's visible rows as a forward-only <see cref="DbDataReader"/>, ready for
        /// <see cref="SqlBulkCopy.WriteToServer(IDataReader)"/> - and, because SQL Server accepts a
        /// <see cref="DbDataReader"/> as a table-valued parameter value, usable as a <c>Structured</c> parameter too.
        /// <para>
        /// For a TVP, prefer <see cref="AsStructuredParameter(String, String, DataTable, SqlMetaData[])"/>: a reader
        /// makes SQL Server infer the TVP's shape from the reader's schema, where records carry the metadata you
        /// declared. Use this one when the consumer genuinely wants a reader, such as bulk copy.
        /// </para>
        /// </summary>
        /// <param name="table">The table to send.</param>
        /// <returns>A reader positioned before the first visible row.</returns>
        /// <exception cref="ArgumentNullException">The table is null.</exception>
        public DbDataReader AsDataReader(DataTable table)
        {
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            DataTableDataReader reader = new DataTableDataReader(table);
            return reader;
        }

        /// <summary>
        /// Builds a ready-to-use table-valued parameter: a <see cref="SqlParameter"/> whose <c>SqlDbType</c> is
        /// <c>Structured</c>, whose <c>TypeName</c> is the SQL Server table type, and whose value streams this
        /// table's visible rows. Add it to a command's <c>Parameters</c> and execute.
        /// <para>
        /// THE VALUE IS A STREAM, NOT A SNAPSHOT. It is consumed once, while the command executes, and it reads the
        /// table as it is at that moment - so do not modify the table between building the parameter and executing,
        /// and do not reuse one parameter for two executions. Build a new one per execution; it costs one small
        /// object.
        /// </para>
        /// </summary>
        /// <param name="parameterName">The parameter name, with or without the leading @.</param>
        /// <param name="tableTypeName">The SQL Server user-defined table type, for example <c>dbo.OrderList</c>.</param>
        /// <param name="table">The table to send.</param>
        /// <param name="metadata">One entry per TVP column, in TVP column order. Pass
        /// <see cref="InferSqlMetaData"/>'s result only when the target type is not width sensitive - read that
        /// method's documentation first.</param>
        /// <returns>A parameter ready to add to a command.</returns>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        /// <exception cref="ArgumentException">The parameter name or table type name is blank, the metadata array is
        /// empty, or an entry names a column the table does not have.</exception>
        public SqlParameter AsStructuredParameter(String parameterName, String tableTypeName, DataTable table, SqlMetaData[] metadata)
        {
            if (parameterName == null) { throw new ArgumentNullException(nameof(parameterName)); }
            if (tableTypeName == null) { throw new ArgumentNullException(nameof(tableTypeName)); }
            if (String.IsNullOrWhiteSpace(parameterName)) { throw new ArgumentException("A parameter name is required.", nameof(parameterName)); }
            if (String.IsNullOrWhiteSpace(tableTypeName)) { throw new ArgumentException("A SQL Server table type name is required, for example \"dbo.OrderList\".", nameof(tableTypeName)); }
            // AsSqlDataRecords validates the table and the metadata, and does it before anything is allocated here.
            IEnumerable<SqlDataRecord> records = AsSqlDataRecords(table, metadata);
            SqlParameter parameter = new SqlParameter(parameterName, SqlDbType.Structured);
            parameter.TypeName = tableTypeName;
            parameter.Direction = ParameterDirection.Input;
            parameter.Value = records;
            return parameter;
        }

        /// <summary>
        /// As <see cref="AsStructuredParameter(String, String, DataTable, SqlMetaData[])"/>, with the metadata
        /// inferred from the table's schema by <see cref="InferSqlMetaData"/>.
        /// <para>
        /// CONVENIENT, AND WIDER THAN YOUR TABLE TYPE PROBABLY IS. The inferred metadata favours maximum widths, so
        /// a <c>decimal(19,4)</c>, <c>char(3)</c> or <c>date</c> column will be converted by SQL Server rather than
        /// matched exactly. Supply metadata yourself for anything width sensitive.
        /// </para>
        /// </summary>
        /// <param name="parameterName">The parameter name, with or without the leading @.</param>
        /// <param name="tableTypeName">The SQL Server user-defined table type, for example <c>dbo.OrderList</c>.</param>
        /// <param name="table">The table to send.</param>
        /// <returns>A parameter ready to add to a command.</returns>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        /// <exception cref="ArgumentException">The parameter name or table type name is blank.</exception>
        /// <exception cref="NotSupportedException">A column's type has no obvious SQL Server counterpart.</exception>
        public SqlParameter AsStructuredParameter(String parameterName, String tableTypeName, DataTable table)
        {
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            SqlMetaData[] metadata = InferSqlMetaData(table);
            return AsStructuredParameter(parameterName, tableTypeName, table, metadata);
        }

        /// <summary>
        /// Builds a best-guess <see cref="SqlMetaData"/> array from the table's schema, for callers whose TVP columns
        /// are simple enough not to need explicit metadata.
        /// <para>
        /// THE GUESSES ARE DELIBERATELY WIDE, favouring bandwidth over silent truncation:
        /// <see cref="String"/> becomes <c>nvarchar(max)</c>, <see cref="Byte"/>[] becomes <c>varbinary(max)</c>,
        /// <see cref="Decimal"/> becomes <c>decimal(38,6)</c>, <see cref="DateTime"/> becomes <c>datetime2</c>.
        /// A TVP type whose declared precision, scale or length differs will still be converted by SQL Server, and a
        /// value that does not fit the real column will fail at the server rather than here. If the target type is
        /// <c>decimal(19,4)</c>, <c>char(3)</c>, <c>date</c> or anything else width-sensitive, supply the metadata
        /// yourself.
        /// </para>
        /// </summary>
        /// <param name="table">The table whose schema to describe.</param>
        /// <returns>One metadata entry per column, in column order.</returns>
        /// <exception cref="ArgumentNullException">The table is null.</exception>
        /// <exception cref="NotSupportedException">A column's type has no obvious SQL Server counterpart.</exception>
        public SqlMetaData[] InferSqlMetaData(DataTable table)
        {
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            Int32 columnCount = table.Columns.Count;
            SqlMetaData[] metadata = new SqlMetaData[columnCount];
            for (Int32 ordinal = 0; ordinal < columnCount; ordinal++)
            {
                IDataColumn column = table.Columns[ordinal];
                metadata[ordinal] = InferColumnMetaData(column);
            }
            return metadata;
        }
        #endregion

        #region Private Methods
        // The per-row loop. One record instance, repopulated and re-yielded; iteration walks the table's own row
        // enumerator, so deleted rows never appear. Separated from the public method so that argument validation
        // happens when the caller calls, not when they first enumerate.
        private static IEnumerable<SqlDataRecord> IterateRecords(SqlMetaData[] metadata, DataTable table, Action<SqlDataRecord, Int32>[] fieldSetters)
        {
            SqlDataRecord record = new SqlDataRecord(metadata);
            foreach (DataRow row in table.Rows)
            {
                Int32 physicalRowIndex = row.RowIndex;
                for (Int32 fieldIndex = 0; fieldIndex < fieldSetters.Length; fieldIndex++)
                {
                    Action<SqlDataRecord, Int32> setter = fieldSetters[fieldIndex];
                    setter(record, physicalRowIndex);
                }
                yield return record;
            }
        }

        // Resolves each metadata entry to a table column and builds its setter. This is the whole schema-time cost of
        // a TVP send.
        private static Action<SqlDataRecord, Int32>[] BuildFieldSetters(DataTable table, SqlMetaData[] metadata)
        {
            Action<SqlDataRecord, Int32>[] fieldSetters = new Action<SqlDataRecord, Int32>[metadata.Length];
            for (Int32 fieldIndex = 0; fieldIndex < metadata.Length; fieldIndex++)
            {
                SqlMetaData fieldMetadata = metadata[fieldIndex];
                if (fieldMetadata == null) { throw new ArgumentException($"Metadata entry {fieldIndex} is null.", nameof(metadata)); }
                String fieldName = fieldMetadata.Name;
                Int32 columnOrdinal = table.Columns.IndexOf(fieldName);
                if (columnOrdinal < 0) { throw new ArgumentException($"Metadata entry {fieldIndex} names column '{fieldName}', which the table does not have.", nameof(metadata)); }
                IDataColumn column = table.Columns[columnOrdinal];
                fieldSetters[fieldIndex] = CreateFieldSetter(fieldIndex, column);
            }
            return fieldSetters;
        }

        // Chooses the setter for one field: a dedicated typed setter where the column's type has one, the Object
        // fallback otherwise. Split by category to keep each method's branch count low.
        private static Action<SqlDataRecord, Int32> CreateFieldSetter(Int32 fieldIndex, IDataColumn column)
        {
            Action<SqlDataRecord, Int32> numericSetter = TryCreateNumericSetter(fieldIndex, column);
            if (numericSetter != null) { return numericSetter; }
            Action<SqlDataRecord, Int32> otherSetter = TryCreateNonNumericSetter(fieldIndex, column);
            if (otherSetter != null) { return otherSetter; }
            Action<SqlDataRecord, Int32> fallbackSetter = CreateFallbackSetter(fieldIndex, column);
            return fallbackSetter;
        }

        // Setters for the numeric types SqlDataRecord has dedicated setters for.
        private static Action<SqlDataRecord, Int32> TryCreateNumericSetter(Int32 fieldIndex, IDataColumn column)
        {
            Type dataType = column.DataType;
            if (dataType == typeof(Int32)) { return CreateTypedSetter<Int32>(fieldIndex, (DataColumn<Int32>)column, WriteInt32); }
            if (dataType == typeof(Int64)) { return CreateTypedSetter<Int64>(fieldIndex, (DataColumn<Int64>)column, WriteInt64); }
            if (dataType == typeof(Int16)) { return CreateTypedSetter<Int16>(fieldIndex, (DataColumn<Int16>)column, WriteInt16); }
            if (dataType == typeof(Byte)) { return CreateTypedSetter<Byte>(fieldIndex, (DataColumn<Byte>)column, WriteByte); }
            if (dataType == typeof(Decimal)) { return CreateTypedSetter<Decimal>(fieldIndex, (DataColumn<Decimal>)column, WriteDecimal); }
            if (dataType == typeof(Double)) { return CreateTypedSetter<Double>(fieldIndex, (DataColumn<Double>)column, WriteDouble); }
            if (dataType == typeof(Single)) { return CreateTypedSetter<Single>(fieldIndex, (DataColumn<Single>)column, WriteSingle); }
            return null;
        }

        // Setters for the remaining types SqlDataRecord has dedicated setters for.
        private static Action<SqlDataRecord, Int32> TryCreateNonNumericSetter(Int32 fieldIndex, IDataColumn column)
        {
            Type dataType = column.DataType;
            if (dataType == typeof(Boolean)) { return CreateTypedSetter<Boolean>(fieldIndex, (DataColumn<Boolean>)column, WriteBoolean); }
            if (dataType == typeof(DateTime)) { return CreateTypedSetter<DateTime>(fieldIndex, (DataColumn<DateTime>)column, WriteDateTime); }
            if (dataType == typeof(Guid)) { return CreateTypedSetter<Guid>(fieldIndex, (DataColumn<Guid>)column, WriteGuid); }
            if (dataType == typeof(String)) { return CreateTypedSetter<String>(fieldIndex, (DataColumn<String>)column, WriteString); }
            if (dataType == typeof(Byte[])) { return CreateTypedSetter<Byte[]>(fieldIndex, (DataColumn<Byte[]>)column, WriteBytes); }
            return null;
        }

        // Builds the per-field closure. Everything type-dependent - the field index, the cast column, the setter - is
        // captured here, once. What survives into the per-row loop is a delegate whose body is a null test and either
        // a typed store or SetDBNull.
        private static Action<SqlDataRecord, Int32> CreateTypedSetter<T>(Int32 fieldIndex, DataColumn<T> column, Action<SqlDataRecord, Int32, T> typedSetter)
        {
            return (record, rowIndex) =>
            {
                Boolean isNull = column.IsNull(rowIndex);
                if (isNull)
                {
                    record.SetDBNull(fieldIndex);
                    return;
                }
                T value = column.Get(rowIndex);
                typedSetter(record, fieldIndex, value);
            };
        }

        // Setter for a column whose type has no dedicated SqlDataRecord setter. Boxes on the non-null path because
        // the record's own API leaves no alternative (NFR-7 permits exactly this), but the decision to take this path
        // was still made once, at schema time.
        private static Action<SqlDataRecord, Int32> CreateFallbackSetter(Int32 fieldIndex, IDataColumn column)
        {
            return (record, rowIndex) =>
            {
                Object value = column.GetValue(rowIndex);
                if (value == null)
                {
                    record.SetDBNull(fieldIndex);
                    return;
                }
                record.SetValue(fieldIndex, value);
            };
        }

        // Maps one column to a best-guess metadata entry. Split out of InferSqlMetaData so the loop stays readable
        // and the guesses live in one place.
        private static SqlMetaData InferColumnMetaData(IDataColumn column)
        {
            Type dataType = column.DataType;
            String name = column.ColumnName;
            if (dataType == typeof(Int32)) { return new SqlMetaData(name, SqlDbType.Int); }
            if (dataType == typeof(Int64)) { return new SqlMetaData(name, SqlDbType.BigInt); }
            if (dataType == typeof(Int16)) { return new SqlMetaData(name, SqlDbType.SmallInt); }
            if (dataType == typeof(Byte)) { return new SqlMetaData(name, SqlDbType.TinyInt); }
            if (dataType == typeof(Boolean)) { return new SqlMetaData(name, SqlDbType.Bit); }
            if (dataType == typeof(Double)) { return new SqlMetaData(name, SqlDbType.Float); }
            if (dataType == typeof(Single)) { return new SqlMetaData(name, SqlDbType.Real); }
            if (dataType == typeof(DateTime)) { return new SqlMetaData(name, SqlDbType.DateTime2); }
            if (dataType == typeof(Guid)) { return new SqlMetaData(name, SqlDbType.UniqueIdentifier); }
            if (dataType == typeof(Decimal)) { return new SqlMetaData(name, SqlDbType.Decimal, INFERRED_DECIMAL_PRECISION, INFERRED_DECIMAL_SCALE); }
            if (dataType == typeof(String)) { return new SqlMetaData(name, SqlDbType.NVarChar, MAX_LENGTH_MARKER); }
            if (dataType == typeof(Byte[])) { return new SqlMetaData(name, SqlDbType.VarBinary, MAX_LENGTH_MARKER); }
            throw new NotSupportedException($"Column '{name}' holds {dataType.FullName}, which has no inferable SQL Server type. Supply the SqlMetaData array explicitly.");
        }

        // The typed setters, one per supported type. Each exists so a method-group conversion can capture it into a
        // setter closure without allocating a lambda per row.
        private static void WriteInt32(SqlDataRecord record, Int32 fieldIndex, Int32 value)
        {
            record.SetInt32(fieldIndex, value);
        }

        // Typed setter for Int64 columns.
        private static void WriteInt64(SqlDataRecord record, Int32 fieldIndex, Int64 value)
        {
            record.SetInt64(fieldIndex, value);
        }

        // Typed setter for Int16 columns.
        private static void WriteInt16(SqlDataRecord record, Int32 fieldIndex, Int16 value)
        {
            record.SetInt16(fieldIndex, value);
        }

        // Typed setter for Byte columns.
        private static void WriteByte(SqlDataRecord record, Int32 fieldIndex, Byte value)
        {
            record.SetByte(fieldIndex, value);
        }

        // Typed setter for Decimal columns.
        private static void WriteDecimal(SqlDataRecord record, Int32 fieldIndex, Decimal value)
        {
            record.SetDecimal(fieldIndex, value);
        }

        // Typed setter for Double columns.
        private static void WriteDouble(SqlDataRecord record, Int32 fieldIndex, Double value)
        {
            record.SetDouble(fieldIndex, value);
        }

        // Typed setter for Single columns. SqlDataRecord spells this one SetFloat.
        private static void WriteSingle(SqlDataRecord record, Int32 fieldIndex, Single value)
        {
            record.SetFloat(fieldIndex, value);
        }

        // Typed setter for Boolean columns.
        private static void WriteBoolean(SqlDataRecord record, Int32 fieldIndex, Boolean value)
        {
            record.SetBoolean(fieldIndex, value);
        }

        // Typed setter for DateTime columns.
        private static void WriteDateTime(SqlDataRecord record, Int32 fieldIndex, DateTime value)
        {
            record.SetDateTime(fieldIndex, value);
        }

        // Typed setter for Guid columns.
        private static void WriteGuid(SqlDataRecord record, Int32 fieldIndex, Guid value)
        {
            record.SetGuid(fieldIndex, value);
        }

        // Typed setter for String columns. Passes a reference, so there is nothing to box.
        private static void WriteString(SqlDataRecord record, Int32 fieldIndex, String value)
        {
            record.SetString(fieldIndex, value);
        }

        // Typed setter for Byte[] columns. SetBytes copies from an offset, so the whole array is written in one call.
        private static void WriteBytes(SqlDataRecord record, Int32 fieldIndex, Byte[] value)
        {
            record.SetBytes(fieldIndex, 0L, value, 0, value.Length);
        }
        #endregion
    }
}
