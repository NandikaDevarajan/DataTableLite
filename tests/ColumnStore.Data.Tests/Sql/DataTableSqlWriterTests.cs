///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies both SQL Server write paths (03_Design.md section 6.6): the table-valued-parameter record stream
//   and the IDataReader bulk-copy adapter. Values and nulls must round-trip, deleted rows must be skipped, and the
//   per-cell path must not allocate.
// Assumptions: SqlDataRecord and SqlMetaData work entirely in memory, so both paths are testable without a SQL Server
//   instance. Records are read back through the same SqlDataRecord API SQL Server would use.
// Design Considerations: The record stream deliberately reuses one SqlDataRecord instance, so a test that buffered
//   the sequence would see N references to the last row and prove nothing. Every test here consumes it the way
//   ADO.NET does - streaming, reading each record before advancing - and one test pins that documented behaviour down
//   explicitly so nobody "fixes" it into a per-row allocation later.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

using Microsoft.Data.SqlClient;

using ColumnStore.Data.SqlServer;
using ColumnStore.Data.Tests.Support;
using Microsoft.Data.SqlClient.Server;
using Xunit;

namespace ColumnStore.Data.Tests.Sql
{
    /// <summary>
    /// Tests for <see cref="DataTableSqlWriter"/> and <see cref="DataTableDataReader"/>.
    /// </summary>
    public sealed class DataTableSqlWriterTests
    {
        #region Private Constants
        // Bytes one additional streamed row is allowed to allocate. Zero: the record instance is created once and
        // repopulated, the row walk uses the struct enumerator, and every typed setter takes its value unboxed.
        private const Int64 MAXIMUM_BYTES_PER_STREAMED_ROW = 0;

        // Bytes one additional streamed row is allowed to allocate when a Decimal column is present.
        // SqlDataRecord.SetDecimal allocates inside Microsoft.Data.SqlClient on every call, which no caller can
        // avoid - it is the "beyond what the target API itself requires" clause of NFR-7 in practice. Each bound is
        // set just above the cost MEASURED ON THAT FRAMEWORK, so a regression on OUR side still fails the test.
        // The two figures differ because the .NET Framework build of Microsoft.Data.SqlClient allocates roughly
        // twice as much there - 80 bytes against 40 - and nothing in this library influences either number. The
        // companion test AsSqlDataRecordsHotPathAllocatesNothingPerRow still demands an exact zero on every
        // framework, which is what pins our own side of the boundary.
#if NETFRAMEWORK
        private const Int64 MAXIMUM_BYTES_PER_STREAMED_DECIMAL_ROW = 96;
#else
        private const Int64 MAXIMUM_BYTES_PER_STREAMED_DECIMAL_ROW = 48;
#endif
        #endregion

        #region Public Methods
        /// <summary>
        /// Every typed setter round-trips its value into the record stream, one record per visible row.
        /// </summary>
        [Fact]
        public void AsSqlDataRecordsRoundTripsEveryTypedSetter()
        {
            DateTime timestamp = new DateTime(2026, 9, 8, 11, 22, 33, DateTimeKind.Unspecified);
            Guid identifier = new Guid("6F9619FF-8B86-D011-B42D-00C04FC964FF");
            Byte[] payload = new Byte[] { 4, 5, 6 };
            DataTable table = BuildFullTypeTable(timestamp, identifier, payload);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = writer.InferSqlMetaData(table);
            List<Object[]> streamedRows = StreamRecordValues(writer, table, metadata);
            Assert.Equal(2, streamedRows.Count);
            Object[] first = streamedRows[0];
            Assert.Equal(42, first[0]);
            Assert.Equal(9_000_000_000L, first[1]);
            Assert.Equal((Int16)7, first[2]);
            Assert.Equal((Byte)255, first[3]);
            Assert.Equal(true, first[4]);
            Assert.Equal(12.34m, first[5]);
            Assert.Equal(1.5d, first[6]);
            Assert.Equal(2.5f, first[7]);
            Assert.Equal(timestamp, first[8]);
            Assert.Equal(identifier, first[9]);
            Assert.Equal("text", first[10]);
            Assert.Equal(payload, first[11]);
        }

        /// <summary>
        /// A null cell becomes <see cref="DBNull"/> in the record, in every column type (FR-22).
        /// </summary>
        [Fact]
        public void AsSqlDataRecordsNullCellsBecomeDBNull()
        {
            DataTable table = BuildFullTypeTable(new DateTime(2026, 9, 8), Guid.NewGuid(), new Byte[] { 1 });
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = writer.InferSqlMetaData(table);
            List<Object[]> streamedRows = StreamRecordValues(writer, table, metadata);
            Object[] second = streamedRows[1];
            for (Int32 fieldIndex = 0; fieldIndex < second.Length; fieldIndex++)
            {
                Assert.Equal(DBNull.Value, second[fieldIndex]);
            }
        }

        /// <summary>
        /// Deleted rows never reach the record stream - the writer walks logical rows, so tombstones are invisible to
        /// it without any deletion-specific code of its own.
        /// </summary>
        [Fact]
        public void AsSqlDataRecordsSkipsDeletedRows()
        {
            DataTable table = BuildSimpleTable(10);
            table.Rows.Delete(table.Rows[0]);
            table.Rows.Delete(table.Rows[4]);
            table.Rows.Delete(table.Rows[table.Rows.Count - 1]);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = writer.InferSqlMetaData(table);
            List<Object[]> streamedRows = StreamRecordValues(writer, table, metadata);
            Assert.Equal(7, streamedRows.Count);
            List<Int32> expectedIds = new List<Int32> { 1, 2, 3, 4, 6, 7, 8 };
            for (Int32 position = 0; position < expectedIds.Count; position++)
            {
                Assert.Equal(expectedIds[position], streamedRows[position][0]);
            }
        }

        /// <summary>
        /// Metadata order drives the record's field order, and a subset of the table's columns is legitimate.
        /// </summary>
        [Fact]
        public void AsSqlDataRecordsMetadataOrderDrivesFieldOrder()
        {
            DataTable table = BuildSimpleTable(2);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = new SqlMetaData[] { new SqlMetaData("Name", SqlDbType.NVarChar, -1L), new SqlMetaData("Id", SqlDbType.Int) };
            List<Object[]> streamedRows = StreamRecordValues(writer, table, metadata);
            Assert.Equal(2, streamedRows.Count);
            Assert.Equal("row0", streamedRows[0][0]);
            Assert.Equal(0, streamedRows[0][1]);
        }

        /// <summary>
        /// The stream is documented to reuse one record instance. This test pins that down: buffering the sequence
        /// yields the same reference every time, which is why callers must consume it streaming.
        /// </summary>
        [Fact]
        public void AsSqlDataRecordsReusesOneRecordInstance()
        {
            DataTable table = BuildSimpleTable(3);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = writer.InferSqlMetaData(table);
            List<SqlDataRecord> buffered = new List<SqlDataRecord>();
            foreach (SqlDataRecord record in writer.AsSqlDataRecords(table, metadata))
            {
                buffered.Add(record);
            }
            Assert.Equal(3, buffered.Count);
            Assert.Same(buffered[0], buffered[1]);
            Assert.Same(buffered[1], buffered[2]);
        }

        /// <summary>
        /// Bad inputs are refused when the caller calls, not lazily when they first enumerate.
        /// </summary>
        [Fact]
        public void AsSqlDataRecordsWithInvalidInputThrowsEagerly()
        {
            DataTable table = BuildSimpleTable(1);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = writer.InferSqlMetaData(table);
            Assert.Throws<ArgumentNullException>(() => writer.AsSqlDataRecords(null, metadata));
            Assert.Throws<ArgumentNullException>(() => writer.AsSqlDataRecords(table, null));
            Assert.Throws<ArgumentException>(() => writer.AsSqlDataRecords(table, new SqlMetaData[0]));
            SqlMetaData[] unknownColumn = new SqlMetaData[] { new SqlMetaData("Missing", SqlDbType.Int) };
            ArgumentException failure = Assert.Throws<ArgumentException>(() => writer.AsSqlDataRecords(table, unknownColumn));
            Assert.Contains("Missing", failure.Message);
        }

        /// <summary>
        /// Streaming records allocates NOTHING per row: no row object, no box, no per-row record. The columns chosen
        /// here cover every typed setter whose SqlDataRecord implementation is itself allocation free.
        /// </summary>
        [Fact]
        public void AsSqlDataRecordsHotPathAllocatesNothingPerRow()
        {
            DataTableSqlWriter writer = new DataTableSqlWriter();
            Int64 bytesPerExtraRow = MeasurePerRowStreamAllocation(writer, false);
            Assert.True(bytesPerExtraRow <= MAXIMUM_BYTES_PER_STREAMED_ROW, $"Streaming allocated {bytesPerExtraRow} bytes per additional row, which indicates a per-row allocation or per-cell boxing.");
        }

        /// <summary>
        /// A Decimal column is the one measured exception, and the cost is not ours: SqlDataRecord.SetDecimal
        /// allocates inside Microsoft.Data.SqlClient regardless of how it is called. This test records that fact and
        /// still bounds it tightly enough to catch a regression on this side of the boundary.
        /// </summary>
        [Fact]
        public void AsSqlDataRecordsWithADecimalColumnAllocatesOnlyWhatSetDecimalCosts()
        {
            DataTableSqlWriter writer = new DataTableSqlWriter();
            Int64 bytesPerExtraRow = MeasurePerRowStreamAllocation(writer, true);
            Assert.True(bytesPerExtraRow <= MAXIMUM_BYTES_PER_STREAMED_DECIMAL_ROW, $"Streaming a Decimal column allocated {bytesPerExtraRow} bytes per additional row, more than SqlDataRecord.SetDecimal's own measured cost.");
        }

        /// <summary>
        /// THE ONE-LINER A TABLE-VALUED PARAMETER IS SUPPOSED TO BE. The parameter comes back declared Structured,
        /// carrying the table type name, with a value SQL Server accepts - and the value still streams, so the rows
        /// are read out of the table when the sequence is consumed rather than copied into the parameter now.
        /// </summary>
        [Fact]
        public void AsStructuredParameterProducesAUsableTableValuedParameter()
        {
            DataTable table = BuildSimpleTable(3);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlParameter parameter = writer.AsStructuredParameter("@rows", "dbo.SimpleList", table);

            Assert.Equal("@rows", parameter.ParameterName);
            Assert.Equal(SqlDbType.Structured, parameter.SqlDbType);
            Assert.Equal("dbo.SimpleList", parameter.TypeName);
            Assert.Equal(ParameterDirection.Input, parameter.Direction);

            // The value is the record stream, and reading it yields this table's rows in order.
            IEnumerable<SqlDataRecord> records = Assert.IsAssignableFrom<IEnumerable<SqlDataRecord>>(parameter.Value);
            List<Int32> streamedIds = new List<Int32>();
            foreach (SqlDataRecord record in records)
            {
                streamedIds.Add(record.GetInt32(0));
            }
            Assert.Equal(new List<Int32> { 0, 1, 2 }, streamedIds);
        }

        /// <summary>
        /// The explicit-metadata overload uses the metadata it is given rather than inferring, which is the whole
        /// reason to reach for it: a width-sensitive TVP column has to be declared, not guessed.
        /// </summary>
        [Fact]
        public void AsStructuredParameterHonoursSuppliedMetadata()
        {
            DataTable table = BuildSimpleTable(2);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = new SqlMetaData[]
            {
                new SqlMetaData("Id", SqlDbType.Int),
                new SqlMetaData("Name", SqlDbType.NVarChar, 20)
            };
            SqlParameter parameter = writer.AsStructuredParameter("rows", "dbo.SimpleList", table, metadata);

            Assert.Equal(SqlDbType.Structured, parameter.SqlDbType);
            IEnumerable<SqlDataRecord> records = (IEnumerable<SqlDataRecord>)parameter.Value;
            foreach (SqlDataRecord record in records)
            {
                Assert.Equal(20, record.GetSqlMetaData(1).MaxLength);
            }
        }

        /// <summary>
        /// Bad arguments are refused here rather than at the server, and the metadata overload delegates its
        /// validation to AsSqlDataRecords so the two cannot drift apart.
        /// </summary>
        [Fact]
        public void AsStructuredParameterRejectsBadArguments()
        {
            DataTable table = BuildSimpleTable(1);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = writer.InferSqlMetaData(table);

            Assert.Throws<ArgumentNullException>(() => writer.AsStructuredParameter(null, "dbo.T", table, metadata));
            Assert.Throws<ArgumentNullException>(() => writer.AsStructuredParameter("@rows", null, table, metadata));
            Assert.Throws<ArgumentNullException>(() => writer.AsStructuredParameter("@rows", "dbo.T", null, metadata));
            Assert.Throws<ArgumentNullException>(() => writer.AsStructuredParameter("@rows", "dbo.T", table, null));
            Assert.Throws<ArgumentException>(() => writer.AsStructuredParameter("   ", "dbo.T", table, metadata));
            Assert.Throws<ArgumentException>(() => writer.AsStructuredParameter("@rows", "   ", table, metadata));
            Assert.Throws<ArgumentException>(() => writer.AsStructuredParameter("@rows", "dbo.T", table, new SqlMetaData[0]));
            Assert.Throws<ArgumentNullException>(() => writer.AsStructuredParameter("@rows", "dbo.T", null));
        }

        /// <summary>
        /// The reader adapter is a DbDataReader, not merely an IDataReader - which is what lets it be used as a
        /// Structured parameter value as well as for bulk copy, since SQL Server accepts the former and not the
        /// latter. The two members DbDataReader adds over IDataReader are asserted here because nothing else in the
        /// suite would notice if they stopped working.
        /// </summary>
        [Fact]
        public void AsDataReaderIsADbDataReaderSoItCanAlsoBeAStructuredValue()
        {
            DataTableSqlWriter writer = new DataTableSqlWriter();
            DbDataReader populated = writer.AsDataReader(BuildSimpleTable(3));
            Assert.IsAssignableFrom<DbDataReader>(populated);
            Assert.IsAssignableFrom<IDataReader>(populated);
            Assert.True(populated.HasRows);

            Int32 enumeratedRows = 0;
            IEnumerator records = populated.GetEnumerator();
            while (records.MoveNext())
            {
                DbDataRecord record = Assert.IsAssignableFrom<DbDataRecord>(records.Current);
                Assert.Equal(enumeratedRows, record.GetInt32(0));
                enumeratedRows = enumeratedRows + 1;
            }
            Assert.Equal(3, enumeratedRows);

            DbDataReader empty = writer.AsDataReader(BuildSimpleTable(0));
            Assert.False(empty.HasRows);
        }

        /// <summary>
        /// The bulk-copy adapter reports the table's schema and reads every typed value and null back.
        /// </summary>
        [Fact]
        public void AsDataReaderExposesSchemaAndTypedValues()
        {
            DateTime timestamp = new DateTime(2026, 9, 8, 1, 2, 3, DateTimeKind.Unspecified);
            Guid identifier = Guid.NewGuid();
            Byte[] payload = new Byte[] { 7, 8 };
            DataTable table = BuildFullTypeTable(timestamp, identifier, payload);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            using (IDataReader reader = writer.AsDataReader(table))
            {
                Assert.Equal(12, reader.FieldCount);
                Assert.Equal("AsInt32", reader.GetName(0));
                Assert.Equal(0, reader.GetOrdinal("asint32"));
                Assert.Equal(typeof(Decimal), reader.GetFieldType(5));
                Assert.True(reader.Read());
                Assert.Equal(42, reader.GetInt32(0));
                Assert.Equal(9_000_000_000L, reader.GetInt64(1));
                Assert.Equal((Int16)7, reader.GetInt16(2));
                Assert.Equal((Byte)255, reader.GetByte(3));
                Assert.True(reader.GetBoolean(4));
                Assert.Equal(12.34m, reader.GetDecimal(5));
                Assert.Equal(1.5d, reader.GetDouble(6));
                Assert.Equal(2.5f, reader.GetFloat(7));
                Assert.Equal(timestamp, reader.GetDateTime(8));
                Assert.Equal(identifier, reader.GetGuid(9));
                Assert.Equal("text", reader.GetString(10));
                Assert.False(reader.IsDBNull(0));
                Assert.True(reader.Read());
                for (Int32 ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    Assert.True(reader.IsDBNull(ordinal));
                    Assert.Equal(DBNull.Value, reader.GetValue(ordinal));
                }
                Assert.False(reader.Read());
            }
        }

        /// <summary>
        /// The adapter's schema table describes every field, which is what a bulk-copy consumer reads for column
        /// mapping.
        /// </summary>
        [Fact]
        public void AsDataReaderSchemaTableDescribesEveryField()
        {
            DataTable table = BuildSimpleTable(1);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            using (IDataReader reader = writer.AsDataReader(table))
            {
                System.Data.DataTable schemaTable = reader.GetSchemaTable();
                Assert.Equal(2, schemaTable.Rows.Count);
                Assert.Equal("Id", schemaTable.Rows[0]["ColumnName"]);
                Assert.Equal(0, schemaTable.Rows[0]["ColumnOrdinal"]);
                Assert.Equal(typeof(Int32), schemaTable.Rows[0]["DataType"]);
                Assert.Equal(false, schemaTable.Rows[0]["AllowDBNull"]);
                Assert.Equal(true, schemaTable.Rows[1]["AllowDBNull"]);
            }
        }

        /// <summary>
        /// The adapter skips deleted rows, so a bulk copy of a table with tombstones sends exactly the visible rows.
        /// </summary>
        [Fact]
        public void AsDataReaderSkipsDeletedRows()
        {
            DataTable table = BuildSimpleTable(10);
            table.Rows.Delete(table.Rows[0]);
            table.Rows.Delete(table.Rows[3]);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            List<Int32> readIds = new List<Int32>();
            using (IDataReader reader = writer.AsDataReader(table))
            {
                while (reader.Read())
                {
                    readIds.Add(reader.GetInt32(0));
                }
            }
            Assert.Equal(new List<Int32> { 1, 2, 3, 5, 6, 7, 8, 9 }, readIds);
        }

        /// <summary>
        /// GetValues copies the current row, and the row is unreadable before the first Read and after the last.
        /// </summary>
        [Fact]
        public void AsDataReaderEnforcesItsPositionContract()
        {
            DataTable table = BuildSimpleTable(1);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            using (IDataReader reader = writer.AsDataReader(table))
            {
                Assert.Throws<InvalidOperationException>(() => reader.GetInt32(0));
                Assert.True(reader.Read());
                Object[] values = new Object[2];
                Assert.Equal(2, reader.GetValues(values));
                Assert.Equal(0, values[0]);
                Assert.Equal("row0", values[1]);
                Assert.False(reader.Read());
                Assert.Throws<InvalidOperationException>(() => reader.GetInt32(0));
            }
        }

        /// <summary>
        /// A closed reader refuses further access, and reading a field as the wrong type or an unknown field fails
        /// with the exception types ADO.NET consumers expect.
        /// </summary>
        [Fact]
        public void AsDataReaderRejectsMisuse()
        {
            DataTable table = BuildSimpleTable(1);
            DataTableSqlWriter writer = new DataTableSqlWriter();
            IDataReader reader = writer.AsDataReader(table);
            Assert.True(reader.Read());
            Assert.Throws<InvalidCastException>(() => reader.GetInt64(0));
            Assert.Throws<IndexOutOfRangeException>(() => reader.GetInt32(99));
            Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("Missing"));
            Assert.Throws<NotSupportedException>(() => reader.GetData(0));
            Assert.False(reader.NextResult());
            Assert.Equal(0, reader.Depth);
            Assert.Equal(-1, reader.RecordsAffected);
            reader.Dispose();
            Assert.True(reader.IsClosed);
            Assert.Throws<InvalidOperationException>(() => reader.Read());
            Assert.Throws<InvalidOperationException>(() => reader.GetInt32(0));
        }

        /// <summary>
        /// GetBytes and GetChars follow the IDataRecord contract: a null buffer asks for the length, and a buffer
        /// receives as much as fits.
        /// </summary>
        [Fact]
        public void AsDataReaderGetBytesAndGetCharsFollowTheRecordContract()
        {
            DataTable table = new DataTable("Blobs");
            table.Columns.Add<Byte[]>("Payload");
            table.Columns.Add<String>("Text");
            table.Rows.Add(new Byte[] { 1, 2, 3, 4, 5 }, "abcdefgh");
            DataTableSqlWriter writer = new DataTableSqlWriter();
            using (IDataReader reader = writer.AsDataReader(table))
            {
                Assert.True(reader.Read());
                Assert.Equal(5L, reader.GetBytes(0, 0L, null, 0, 0));
                Byte[] byteBuffer = new Byte[3];
                Int64 copiedBytes = reader.GetBytes(0, 1L, byteBuffer, 0, 3);
                Assert.Equal(3L, copiedBytes);
                Assert.Equal(new Byte[] { 2, 3, 4 }, byteBuffer);
                Assert.Equal(8L, reader.GetChars(1, 0L, null, 0, 0));
                Char[] charBuffer = new Char[4];
                Int64 copiedChars = reader.GetChars(1, 2L, charBuffer, 0, 4);
                Assert.Equal(4L, copiedChars);
                Assert.Equal("cdef".ToCharArray(), charBuffer);
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetBytes(0, -1L, byteBuffer, 0, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetBytes(0, 0L, byteBuffer, -1, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetBytes(0, 0L, byteBuffer, 0, -1));
            }
        }

        /// <summary>
        /// The metadata inference helper covers the types it claims to and refuses the ones it cannot describe,
        /// rather than guessing silently.
        /// </summary>
        [Fact]
        public void InferSqlMetaDataCoversKnownTypesAndRefusesUnknownOnes()
        {
            DataTable table = new DataTable("Inferred");
            table.Columns.Add<Int32>("AsInt32");
            table.Columns.Add<String>("AsString");
            table.Columns.Add<Decimal>("AsDecimal");
            DataTableSqlWriter writer = new DataTableSqlWriter();
            SqlMetaData[] metadata = writer.InferSqlMetaData(table);
            Assert.Equal(SqlDbType.Int, metadata[0].SqlDbType);
            Assert.Equal(SqlDbType.NVarChar, metadata[1].SqlDbType);
            Assert.Equal(-1L, metadata[1].MaxLength);
            Assert.Equal(SqlDbType.Decimal, metadata[2].SqlDbType);
            Assert.Equal((Byte)38, metadata[2].Precision);
            Assert.Equal((Byte)6, metadata[2].Scale);
            DataTable unsupported = new DataTable("Unsupported");
            unsupported.Columns.Add<TimeSpan>("AsTimeSpan");
            Assert.Throws<NotSupportedException>(() => writer.InferSqlMetaData(unsupported));
            Assert.Throws<ArgumentNullException>(() => writer.InferSqlMetaData(null));
        }

        /// <summary>
        /// Both entry points refuse a null table.
        /// </summary>
        [Fact]
        public void AsDataReaderWithNullTableThrows()
        {
            DataTableSqlWriter writer = new DataTableSqlWriter();
            Assert.Throws<ArgumentNullException>(() => writer.AsDataReader(null));
            Assert.Throws<ArgumentNullException>(() => new DataTableDataReader(null));
        }
        #endregion

        #region Private Methods
        // A table covering every type with a dedicated SqlDataRecord setter plus a byte array, with a fully populated
        // first row and an all-null second row.
        private static DataTable BuildFullTypeTable(DateTime timestamp, Guid identifier, Byte[] payload)
        {
            DataTable table = new DataTable("Everything");
            table.Columns.Add<Int32>("AsInt32");
            table.Columns.Add<Int64>("AsInt64");
            table.Columns.Add<Int16>("AsInt16");
            table.Columns.Add<Byte>("AsByte");
            table.Columns.Add<Boolean>("AsBoolean");
            table.Columns.Add<Decimal>("AsDecimal");
            table.Columns.Add<Double>("AsDouble");
            table.Columns.Add<Single>("AsSingle");
            table.Columns.Add<DateTime>("AsDateTime");
            table.Columns.Add<Guid>("AsGuid");
            table.Columns.Add<String>("AsString");
            table.Columns.Add<Byte[]>("AsBytes");
            table.Rows.Add(42, 9_000_000_000L, (Int16)7, (Byte)255, true, 12.34m, 1.5d, 2.5f, timestamp, identifier, "text", payload);
            table.Rows.AddNewRow();
            return table;
        }

        // A two-column table of sequential rows: a non-nullable Int32 and a nullable String.
        private static DataTable BuildSimpleTable(Int32 rowCount)
        {
            DataTable table = new DataTable("Simple");
            table.Columns.Add<Int32>("Id", false);
            table.Columns.Add<String>("Name", true);
            for (Int32 id = 0; id < rowCount; id++)
            {
                table.Rows.Add(id, "row" + id);
            }
            return table;
        }

        // Streams the record sequence and copies each record's values out before advancing, which is the only correct
        // way to observe a stream that reuses one record instance.
        private static List<Object[]> StreamRecordValues(DataTableSqlWriter writer, DataTable table, SqlMetaData[] metadata)
        {
            List<Object[]> streamedRows = new List<Object[]>();
            IEnumerable<SqlDataRecord> records = writer.AsSqlDataRecords(table, metadata);
            foreach (SqlDataRecord record in records)
            {
                Object[] values = new Object[record.FieldCount];
                record.GetValues(values);
                streamedRows.Add(values);
            }
            return streamedRows;
        }

        // Measures the bytes one additional streamed row allocates, by differencing a small and a large stream so
        // that the fixed per-stream setup cancels out. The Decimal column is switchable because it is the one
        // measured source of unavoidable allocation, inside the SqlDataRecord API rather than in this library.
        private static Int64 MeasurePerRowStreamAllocation(DataTableSqlWriter writer, Boolean includeDecimalColumn)
        {
            Int32 smallRowCount = 256;
            Int32 largeRowCount = 16384;
            Int64 smallStreamBytes = MeasureStreamAllocation(writer, smallRowCount, includeDecimalColumn);
            Int64 largeStreamBytes = MeasureStreamAllocation(writer, largeRowCount, includeDecimalColumn);
            Int64 extraRows = largeRowCount - smallRowCount;
            return (largeStreamBytes - smallStreamBytes) / extraRows;
        }

        // Measures the bytes streaming the given number of rows allocates, excluding table construction.
        private static Int64 MeasureStreamAllocation(DataTableSqlWriter writer, Int32 rowCount, Boolean includeDecimalColumn)
        {
            DataTable table = new DataTable("Measured");
            table.Columns.Add<Int32>("Id", false);
            table.Columns.Add<Boolean>("Active", false);
            table.Columns.Add<DateTime>("When", false);
            table.Columns.Add<String>("Name", false);
            table.Columns.Add<Guid>("Key", false);
            if (includeDecimalColumn) { table.Columns.Add<Decimal>("Amount", false); }
            DateTime timestamp = new DateTime(2026, 9, 8);
            Guid key = new Guid("11111111-2222-3333-4444-555555555555");
            for (Int32 id = 0; id < rowCount; id++)
            {
                DataRow row = table.Rows.AddNewRow();
                Int32 physicalRowIndex = row.RowIndex;
                table.Columns.GetColumn<Int32>("Id").Set(physicalRowIndex, id);
                table.Columns.GetColumn<Boolean>("Active").Set(physicalRowIndex, true);
                table.Columns.GetColumn<DateTime>("When").Set(physicalRowIndex, timestamp);
                table.Columns.GetColumn<String>("Name").Set(physicalRowIndex, "constant");
                table.Columns.GetColumn<Guid>("Key").Set(physicalRowIndex, key);
                if (includeDecimalColumn) { table.Columns.GetColumn<Decimal>("Amount").Set(physicalRowIndex, 1.25m); }
            }
            DataTableSqlWriter localWriter = writer;
            SqlMetaData[] metadata = localWriter.InferSqlMetaData(table);
            Action work = () =>
            {
                IEnumerable<SqlDataRecord> records = localWriter.AsSqlDataRecords(table, metadata);
                foreach (SqlDataRecord record in records)
                {
                    Boolean isNull = record.IsDBNull(0);
                    if (isNull) { throw new InvalidOperationException("Unexpected null while measuring."); }
                }
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work, 2);
            return allocatedBytes;
        }
        #endregion
    }
}
