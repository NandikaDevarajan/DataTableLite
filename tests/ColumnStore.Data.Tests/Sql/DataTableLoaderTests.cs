///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies the IDataReader load path (03_Design.md section 6.5): every dedicated typed getter, the Object
//   fallback, nulls, schema creation, nullability inference and its graceful degradation, repeatability, and that the
//   per-cell path allocates nothing.
// Assumptions: FakeDataReader stands in for SqlDataReader. It stores pre-boxed values so its own typed getters
//   allocate nothing, which means the allocation-delta test measures the library and not the double.
// Design Considerations: The repeat-load test guards FR-18, which is easy to break: a loader that rebuilt the schema
//   on every call would silently duplicate columns or throw on the second load. The allocation test guards the actual
//   reason this loader exists - the naive implementation is a per-cell box, and nothing but a measurement can prove
//   that has been avoided.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ColumnStore.Data.SqlServer;
using ColumnStore.Data.Tests.Support;
using Xunit;

namespace ColumnStore.Data.Tests.Sql
{
    /// <summary>
    /// Tests for <see cref="DataTableLoader"/>.
    /// </summary>
    public sealed class DataTableLoaderTests
    {
        #region Private Constants
        // Upper bound on the bytes one additional loaded row may allocate, for the five-column table the allocation
        // test uses. Its real storage need is about five bytes per row; five boxed cells would be at least 120.
        private const Int64 MAXIMUM_STORAGE_BYTES_PER_ROW = 16;
        #endregion

        #region Public Methods
        /// <summary>
        /// Every type with a dedicated reader getter round-trips its value, and a type without one round-trips
        /// through the Object fallback.
        /// </summary>
        [Fact]
        public void LoadEveryDedicatedGetterTypePlusAFallbackRoundTrips()
        {
            DateTime timestamp = new DateTime(2026, 9, 8, 10, 30, 45, DateTimeKind.Utc);
            Guid identifier = new Guid("3F2504E0-4F89-11D3-9A0C-0305E82C3301");
            Byte[] payload = new Byte[] { 1, 2, 3, 250 };
            FakeDataReader reader = BuildFullTypeReader(timestamp, identifier, payload);
            DataTable table = new DataTable("Loaded");
            DataTableLoader loader = new DataTableLoader();
            Int32 loadedRowCount = loader.Load(reader, true, table);
            Assert.Equal(2, loadedRowCount);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(12, table.Columns.Count);
            DataRow first = table.Rows[0];
            Assert.Equal(42, first.Get<Int32>("AsInt32"));
            Assert.Equal(9_000_000_000L, first.Get<Int64>("AsInt64"));
            Assert.Equal((Int16)7, first.Get<Int16>("AsInt16"));
            Assert.Equal((Byte)255, first.Get<Byte>("AsByte"));
            Assert.True(first.Get<Boolean>("AsBoolean"));
            Assert.Equal(12.34m, first.Get<Decimal>("AsDecimal"));
            Assert.Equal(1.5d, first.Get<Double>("AsDouble"));
            Assert.Equal(2.5f, first.Get<Single>("AsSingle"));
            Assert.Equal(timestamp, first.Get<DateTime>("AsDateTime"));
            Assert.Equal(identifier, first.Get<Guid>("AsGuid"));
            Assert.Equal("text", first.Get<String>("AsString"));
        }

        /// <summary>
        /// A null in any field arrives as a null cell, not as a default value.
        /// </summary>
        [Fact]
        public void LoadNullsInEveryFieldArriveAsNullCells()
        {
            DateTime timestamp = new DateTime(2026, 9, 8);
            Guid identifier = Guid.NewGuid();
            Byte[] payload = new Byte[] { 9 };
            FakeDataReader reader = BuildFullTypeReader(timestamp, identifier, payload);
            DataTable table = new DataTable("Loaded");
            DataTableLoader loader = new DataTableLoader();
            loader.Load(reader, true, table);
            DataRow second = table.Rows[1];
            for (Int32 ordinal = 0; ordinal < table.Columns.Count; ordinal++)
            {
                Assert.True(second.IsNull(ordinal), $"Column '{table.Columns[ordinal].ColumnName}' should be null in the second row.");
            }
        }

        /// <summary>
        /// A column type with no dedicated getter - a byte array - still round-trips, through the fallback.
        /// </summary>
        [Fact]
        public void LoadByteArrayColumnRoundTripsThroughTheFallback()
        {
            Byte[] payload = new Byte[] { 10, 20, 30 };
            FakeDataReader reader = BuildFullTypeReader(DateTime.UtcNow, Guid.NewGuid(), payload);
            DataTable table = new DataTable("Loaded");
            DataTableLoader loader = new DataTableLoader();
            loader.Load(reader, true, table);
            Byte[] loaded = table.Rows[0].Get<Byte[]>("AsBytes");
            Assert.Equal(payload, loaded);
        }

        /// <summary>
        /// Schema creation takes names and types from the reader, and nullability from its schema metadata when asked
        /// to infer it.
        /// </summary>
        [Fact]
        public void LoadWithNullabilityInferenceMatchesTheReaderSchema()
        {
            String[] names = new String[] { "Required", "Optional" };
            Type[] types = new Type[] { typeof(Int32), typeof(String) };
            Boolean[] allowsNull = new Boolean[] { false, true };
            List<Object[]> rows = new List<Object[]> { new Object[] { 1, "a" } };
            FakeDataReader reader = new FakeDataReader(names, types, allowsNull, rows, SchemaTableBehaviour.Supported);
            DataTable table = new DataTable("Loaded");
            DataTableLoader loader = new DataTableLoader();
            loader.Load(reader, true, table);
            Assert.False(table.Columns["Required"].AllowDBNull);
            Assert.True(table.Columns["Optional"].AllowDBNull);
        }

        /// <summary>
        /// Nullability inference degrades gracefully: with inference off, or against a provider that returns null,
        /// throws, or omits the metadata column, every column defaults to nullable and the load still succeeds.
        /// </summary>
        [Theory]
        [InlineData(false, SchemaTableBehaviour.Supported)]
        [InlineData(true, SchemaTableBehaviour.ReturnsNull)]
        [InlineData(true, SchemaTableBehaviour.Throws)]
        [InlineData(true, SchemaTableBehaviour.MissingNullabilityColumn)]
        public void LoadWhenNullabilityCannotBeInferredDefaultsToNullableAndSucceeds(Boolean inferNullability, SchemaTableBehaviour behaviour)
        {
            String[] names = new String[] { "Required", "Optional" };
            Type[] types = new Type[] { typeof(Int32), typeof(String) };
            Boolean[] allowsNull = new Boolean[] { false, true };
            List<Object[]> rows = new List<Object[]> { new Object[] { 1, "a" } };
            FakeDataReader reader = new FakeDataReader(names, types, allowsNull, rows, behaviour);
            DataTable table = new DataTable("Loaded");
            DataTableLoader loader = new DataTableLoader();
            loader.Load(reader, inferNullability, table);
            Assert.True(table.Columns["Required"].AllowDBNull);
            Assert.True(table.Columns["Optional"].AllowDBNull);
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(1, table.Rows[0].Get<Int32>("Required"));
        }

        /// <summary>
        /// Loading twice into the same table appends rows and does not recreate the columns (FR-18).
        /// </summary>
        [Fact]
        public void LoadTwiceAppendsRowsWithoutRecreatingColumns()
        {
            DataTable table = new DataTable("Loaded");
            DataTableLoader loader = new DataTableLoader();
            loader.Load(BuildSmallReader(1, 2), true, table);
            IDataColumn columnAfterFirstLoad = table.Columns["Id"];
            loader.Load(BuildSmallReader(3, 4), true, table);
            Assert.Equal(2, table.Columns.Count);
            Assert.Same(columnAfterFirstLoad, table.Columns["Id"]);
            Assert.Equal(4, table.Rows.Count);
            Assert.Equal(1, table.Rows[0].Get<Int32>("Id"));
            Assert.Equal(4, table.Rows[3].Get<Int32>("Id"));
        }

        /// <summary>
        /// A pre-declared schema is authoritative: the loader uses the existing columns and does not add new ones,
        /// which is what makes a non-nullable, deliberately typed schema usable with a generic SELECT.
        /// </summary>
        [Fact]
        public void LoadIntoAPreDeclaredSchemaUsesTheExistingColumns()
        {
            DataTable table = new DataTable("Loaded");
            DataColumn<Int32> id = table.Columns.Add<Int32>("Id", false);
            DataColumn<String> name = table.Columns.Add<String>("Name", true);
            DataTableLoader loader = new DataTableLoader();
            loader.Load(BuildSmallReader(5, 6), true, table);
            Assert.Equal(2, table.Columns.Count);
            Assert.False(id.AllowDBNull);
            Assert.Equal(5, id.Get(0));
            Assert.Equal("row5", name.Get(0));
        }

        /// <summary>
        /// A reader field with no matching table column is skipped rather than failing the load, so a widened SELECT
        /// against a narrow schema still works.
        /// </summary>
        [Fact]
        public void LoadWithAnUnmatchedReaderFieldSkipsIt()
        {
            DataTable table = new DataTable("Loaded");
            table.Columns.Add<Int32>("Id", false);
            DataTableLoader loader = new DataTableLoader();
            loader.Load(BuildSmallReader(1, 1), true, table);
            Assert.Equal(1, table.Columns.Count);
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(1, table.Rows[0].Get<Int32>("Id"));
        }

        /// <summary>
        /// A null arriving for a non-nullable column fails loudly and names the column, rather than being written as
        /// a zero.
        /// </summary>
        [Fact]
        public void LoadNullIntoANonNullableColumnThrowsNamingTheColumn()
        {
            String[] names = new String[] { "Id" };
            Type[] types = new Type[] { typeof(Int32) };
            Boolean[] allowsNull = new Boolean[] { true };
            List<Object[]> rows = new List<Object[]> { new Object[] { null } };
            FakeDataReader reader = new FakeDataReader(names, types, allowsNull, rows, SchemaTableBehaviour.Supported);
            DataTable table = new DataTable("Loaded");
            table.Columns.Add<Int32>("Id", false);
            DataTableLoader loader = new DataTableLoader();
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => loader.Load(reader, true, table));
            Assert.Contains("Id", failure.Message);
        }

        /// <summary>
        /// The non-nullable binding drops its IsDBNull test and recovers the "does not allow null values" diagnostic
        /// from an exception filter instead. That filter must only claim failures caused by a null cell: a genuine
        /// type mismatch on a cell that holds a value has to surface as the provider's own exception, not be dressed
        /// up as a nullability error.
        /// </summary>
        [Fact]
        public void LoadTypeMismatchOnANonNullCellSurfacesAsTheProvidersOwnFailure()
        {
            String[] names = new String[] { "Id" };
            Type[] types = new Type[] { typeof(Int32) };
            Boolean[] allowsNull = new Boolean[] { false };
            List<Object[]> rows = new List<Object[]> { new Object[] { "not an int" } };
            FakeDataReader reader = new FakeDataReader(names, types, allowsNull, rows, SchemaTableBehaviour.Supported);
            DataTable table = new DataTable("Loaded");
            table.Columns.Add<Int32>("Id", false);
            DataTableLoader loader = new DataTableLoader();
            Assert.Throws<InvalidCastException>(() => loader.Load(reader, true, table));
        }

        /// <summary>
        /// Bad inputs are rejected before anything is loaded.
        /// </summary>
        [Fact]
        public void LoadWithInvalidInputThrows()
        {
            DataTableLoader loader = new DataTableLoader();
            DataTable table = new DataTable("Loaded");
            Assert.Throws<ArgumentNullException>(() => loader.Load(null, true, table));
            Assert.Throws<ArgumentNullException>(() => loader.Load(BuildSmallReader(1, 1), true, null));
            String[] noNames = new String[0];
            Type[] noTypes = new Type[0];
            Boolean[] noNullability = new Boolean[0];
            List<Object[]> noRows = new List<Object[]>();
            FakeDataReader emptyReader = new FakeDataReader(noNames, noTypes, noNullability, noRows, SchemaTableBehaviour.Supported);
            Assert.Throws<ArgumentException>(() => loader.Load(emptyReader, true, table));
        }

        /// <summary>
        /// A reader with no rows leaves an empty but fully schema'd table.
        /// </summary>
        [Fact]
        public void LoadWithNoRowsCreatesTheSchemaAndNoRows()
        {
            DataTable table = new DataTable("Loaded");
            DataTableLoader loader = new DataTableLoader();
            Int32 loadedRowCount = loader.Load(BuildSmallReader(1, 0), true, table);
            Assert.Equal(0, loadedRowCount);
            Assert.Equal(2, table.Columns.Count);
            Assert.Equal(0, table.Rows.Count);
        }

        /// <summary>
        /// The asynchronous entry point produces the same result as the synchronous one.
        /// </summary>
        [Fact]
        public async Task LoadAsyncMatchesTheSynchronousLoad()
        {
            System.Data.DataTable source = new System.Data.DataTable("Source");
            source.Columns.Add("Id", typeof(Int32));
            source.Columns.Add("Name", typeof(String));
            source.Rows.Add(1, "first");
            source.Rows.Add(2, null);
            DataTable table = new DataTable("Loaded");
            DataTableLoader loader = new DataTableLoader();
            using (System.Data.DataTableReader reader = source.CreateDataReader())
            {
                Int32 loadedRowCount = await loader.LoadAsync(reader, true, table, CancellationToken.None);
                Assert.Equal(2, loadedRowCount);
            }
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(1, table.Rows[0].Get<Int32>("Id"));
            Assert.Equal("first", table.Rows[0].Get<String>("Name"));
            Assert.True(table.Rows[1].IsNull("Name"));
        }

        /// <summary>
        /// The load path allocates no per-row object and no per-cell box: all it allocates is the column storage the
        /// rows actually need (NFR-1, NFR-7). The table is chosen so that storage is nearly free - four bit-packed
        /// Boolean columns and one Int32 - which leaves boxing nowhere to hide, since five boxed cells per row would
        /// cost at least 120 bytes on their own.
        /// </summary>
        [Fact]
        public void LoadHotPathAllocatesOnlyColumnStorage()
        {
            DataTableLoader loader = new DataTableLoader();
            Int32 smallRowCount = 8192;
            Int32 largeRowCount = 65536;
            Int64 smallLoadBytes = MeasureLoadAllocation(loader, smallRowCount);
            Int64 largeLoadBytes = MeasureLoadAllocation(loader, largeRowCount);
            Int64 extraRows = largeRowCount - smallRowCount;
            Int64 bytesPerExtraRow = (largeLoadBytes - smallLoadBytes) / extraRows;
            Assert.True(bytesPerExtraRow < MAXIMUM_STORAGE_BYTES_PER_ROW, $"Loading allocated {bytesPerExtraRow} bytes per additional row, which is more than this table's storage needs and indicates a per-row object or per-cell boxing.");
        }
        #endregion

        #region Private Methods
        // A reader covering every type with a dedicated getter plus a byte array for the fallback path, with a fully
        // populated first row and an all-null second row.
        private static FakeDataReader BuildFullTypeReader(DateTime timestamp, Guid identifier, Byte[] payload)
        {
            String[] names = new String[] { "AsInt32", "AsInt64", "AsInt16", "AsByte", "AsBoolean", "AsDecimal", "AsDouble", "AsSingle", "AsDateTime", "AsGuid", "AsString", "AsBytes" };
            Type[] types = new Type[] { typeof(Int32), typeof(Int64), typeof(Int16), typeof(Byte), typeof(Boolean), typeof(Decimal), typeof(Double), typeof(Single), typeof(DateTime), typeof(Guid), typeof(String), typeof(Byte[]) };
            Boolean[] allowsNull = new Boolean[types.Length];
            for (Int32 ordinal = 0; ordinal < allowsNull.Length; ordinal++)
            {
                allowsNull[ordinal] = true;
            }
            Object[] populatedRow = new Object[] { 42, 9_000_000_000L, (Int16)7, (Byte)255, true, 12.34m, 1.5d, 2.5f, timestamp, identifier, "text", payload };
            Object[] nullRow = new Object[types.Length];
            List<Object[]> rows = new List<Object[]> { populatedRow, nullRow };
            return new FakeDataReader(names, types, allowsNull, rows, SchemaTableBehaviour.Supported);
        }

        // A two-column reader carrying the given inclusive id range, or no rows when the range is empty.
        private static FakeDataReader BuildSmallReader(Int32 firstId, Int32 lastId)
        {
            String[] names = new String[] { "Id", "Name" };
            Type[] types = new Type[] { typeof(Int32), typeof(String) };
            Boolean[] allowsNull = new Boolean[] { true, true };
            List<Object[]> rows = new List<Object[]>();
            Boolean isEmptyRange = firstId > lastId;
            if (isEmptyRange == false)
            {
                for (Int32 id = firstId; id <= lastId; id++)
                {
                    rows.Add(new Object[] { id, "row" + id });
                }
            }
            return new FakeDataReader(names, types, allowsNull, rows, SchemaTableBehaviour.Supported);
        }

        // Measures the bytes one load of the given row count allocates, into a table whose schema already exists so
        // the measurement covers the row loop and its storage rather than schema creation. Four bit-packed Boolean
        // columns plus one Int32 means genuine storage costs about five bytes per row, so any per-row or per-cell
        // allocation stands out immediately.
        private static Int64 MeasureLoadAllocation(DataTableLoader loader, Int32 rowCount)
        {
            String[] names = new String[] { "Id", "FlagA", "FlagB", "FlagC", "FlagD" };
            Type[] types = new Type[] { typeof(Int32), typeof(Boolean), typeof(Boolean), typeof(Boolean), typeof(Boolean) };
            Boolean[] allowsNull = new Boolean[] { false, false, false, false, false };
            List<Object[]> rows = new List<Object[]>(rowCount);
            Object boxedTrue = true;
            Object boxedFalse = false;
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                Object boxedId = rowIndex;
                rows.Add(new Object[] { boxedId, boxedTrue, boxedFalse, boxedTrue, boxedFalse });
            }
            Action work = () =>
            {
                FakeDataReader reader = new FakeDataReader(names, types, allowsNull, rows, SchemaTableBehaviour.Supported);
                DataTable table = new DataTable("Measured");
                table.Columns.Add<Int32>("Id", false);
                table.Columns.Add<Boolean>("FlagA", false);
                table.Columns.Add<Boolean>("FlagB", false);
                table.Columns.Add<Boolean>("FlagC", false);
                table.Columns.Add<Boolean>("FlagD", false);
                loader.Load(reader, false, table);
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work, 2);
            return allocatedBytes;
        }
        #endregion
    }
}
