///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Builds the ten-column benchmark table in both implementations, from identical data, using each one's
//   fastest natural insert path.
// Assumptions: Both tables are populated from the same string pool, so the two measurements differ in table
//   structure and not in string payload.
// Design Considerations: THE STRING POOL MATTERS MORE THAN IT LOOKS. A million distinct strings costs tens of
//   megabytes of char data in either implementation, which would swamp the structural difference the benchmark is
//   trying to show - both tables would look similar and the measurement would say nothing. Cycling a small pool of
//   shared references keeps the string payload identical and constant on both sides, so the difference that remains
//   is the per-row object and per-cell box overhead, which is exactly what NFR-1 and NFR-4 are about.
//   Each side uses its own idiomatic fastest path, not a lowest common denominator: the columnar table writes through
//   typed column handles resolved once, and the framework table uses BeginLoadData with positional Object arrays,
//   which is materially faster than a naive NewRow loop because it suspends constraint and index maintenance. A
//   comparison against a deliberately slow use of the framework would be worthless.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Creates and populates the benchmark's ten-column table in both implementations.
    /// </summary>
    public static class BenchmarkTableFactory
    {
        #region Private Constants
        // Number of distinct strings cycled through the Name column. Small, so the string payload is a constant few
        // kilobytes on both sides rather than the dominant term.
        private const Int32 NAME_POOL_SIZE = 128;

        // Number of columns in the benchmark table, matching the ~10 columns of NFR-4's representative table.
        private const Int32 COLUMN_COUNT = 10;

        // Sentinel chunk row count meaning "let the library choose", i.e. use ChunkSizing.DefaultRowCount. Not a
        // legal chunk size itself, so it cannot collide with a real request.
        private const Int32 USE_DEFAULT_CHUNK_SIZE = 0;
        #endregion

        #region Public Properties
        /// <summary>Number of columns in the benchmark table.</summary>
        public static Int32 ColumnCount
        {
            get { return COLUMN_COUNT; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Builds the shared pool of strings both tables draw their Name values from.
        /// </summary>
        /// <returns>The pool.</returns>
        public static String[] BuildNamePool()
        {
            String[] namePool = new String[NAME_POOL_SIZE];
            for (Int32 poolIndex = 0; poolIndex < NAME_POOL_SIZE; poolIndex++)
            {
                namePool[poolIndex] = "name-" + poolIndex.ToString();
            }
            return namePool;
        }

        /// <summary>
        /// Builds and populates the column-oriented table, writing through typed column handles resolved once.
        /// </summary>
        /// <param name="rowCount">Number of rows to insert.</param>
        /// <param name="namePool">Shared string pool for the Name column.</param>
        /// <returns>The populated table.</returns>
        public static DataTable BuildLiteTable(Int32 rowCount, String[] namePool)
        {
            DataTable table = BuildLiteSchema(USE_DEFAULT_CHUNK_SIZE);
            PopulateLiteTable(table, rowCount, namePool);
            return table;
        }

        /// <summary>
        /// Builds and populates the column-oriented table with an explicit storage chunk row count, so the effect of
        /// that choice can be measured directly.
        /// </summary>
        /// <param name="rowCount">Number of rows to insert.</param>
        /// <param name="namePool">Shared string pool for the Name column.</param>
        /// <param name="chunkRowCount">Rows per storage chunk. Must be a positive power of two.</param>
        /// <returns>The populated table.</returns>
        public static DataTable BuildLiteTable(Int32 rowCount, String[] namePool, Int32 chunkRowCount)
        {
            DataTable table = BuildLiteSchema(chunkRowCount);
            PopulateLiteTable(table, rowCount, namePool);
            return table;
        }

        /// <summary>
        /// Builds and populates the equivalent framework table, using BeginLoadData and positional Object arrays -
        /// the fastest documented bulk-population path for <see cref="System.Data.DataTable"/>.
        /// </summary>
        /// <param name="rowCount">Number of rows to insert.</param>
        /// <param name="namePool">Shared string pool for the Name column.</param>
        /// <returns>The populated table.</returns>
        public static System.Data.DataTable BuildFrameworkTable(Int32 rowCount, String[] namePool)
        {
            System.Data.DataTable table = BuildFrameworkSchema();
            DateTime baseTimestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Guid constantKey = new Guid("9E107D9D-3722-4EA1-B84C-B0AB9BF0F0C1");
            table.BeginLoadData();
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                Object[] values = new Object[COLUMN_COUNT];
                values[0] = rowIndex;
                values[1] = rowIndex * 3L;
                values[2] = 19.99m;
                values[3] = 0.075d;
                values[4] = baseTimestamp;
                values[5] = (rowIndex & 1) == 0;
                values[6] = namePool[rowIndex % namePool.Length];
                values[7] = constantKey;
                values[8] = (Int16)(rowIndex % 500);
                values[9] = (Byte)(rowIndex % 7);
                table.Rows.Add(values);
            }
            table.EndLoadData();
            return table;
        }
        #endregion

        #region Private Methods
        // Declares the columnar table's schema, either with the library's default chunk size or with an explicit one.
        private static DataTable BuildLiteSchema(Int32 chunkRowCount)
        {
            DataTable table = new DataTable("Benchmark");
            Boolean useDefaultChunkSize = chunkRowCount == USE_DEFAULT_CHUNK_SIZE;
            if (useDefaultChunkSize)
            {
                table.Columns.Add<Int32>("Id", false);
                table.Columns.Add<Int64>("Amount", false);
                table.Columns.Add<Decimal>("Price", false);
                table.Columns.Add<Double>("Rate", false);
                table.Columns.Add<DateTime>("Created", false);
                table.Columns.Add<Boolean>("Active", false);
                table.Columns.Add<String>("Name", false);
                table.Columns.Add<Guid>("Key", false);
                table.Columns.Add<Int16>("Category", false);
                table.Columns.Add<Byte>("Status", false);
                return table;
            }
            table.Columns.Add<Int32>("Id", false, chunkRowCount);
            table.Columns.Add<Int64>("Amount", false, chunkRowCount);
            table.Columns.Add<Decimal>("Price", false, chunkRowCount);
            table.Columns.Add<Double>("Rate", false, chunkRowCount);
            table.Columns.Add<DateTime>("Created", false, chunkRowCount);
            table.Columns.Add<Boolean>("Active", false, chunkRowCount);
            table.Columns.Add<String>("Name", false, chunkRowCount);
            table.Columns.Add<Guid>("Key", false, chunkRowCount);
            table.Columns.Add<Int16>("Category", false, chunkRowCount);
            table.Columns.Add<Byte>("Status", false, chunkRowCount);
            return table;
        }

        // Fills a columnar table through typed column handles resolved once - the pattern the library recommends.
        private static void PopulateLiteTable(DataTable table, Int32 rowCount, String[] namePool)
        {
            DataColumn<Int32> id = table.Columns.GetColumn<Int32>("Id");
            DataColumn<Int64> amount = table.Columns.GetColumn<Int64>("Amount");
            DataColumn<Decimal> price = table.Columns.GetColumn<Decimal>("Price");
            DataColumn<Double> rate = table.Columns.GetColumn<Double>("Rate");
            DataColumn<DateTime> created = table.Columns.GetColumn<DateTime>("Created");
            DataColumn<Boolean> active = table.Columns.GetColumn<Boolean>("Active");
            DataColumn<String> name = table.Columns.GetColumn<String>("Name");
            DataColumn<Guid> key = table.Columns.GetColumn<Guid>("Key");
            DataColumn<Int16> category = table.Columns.GetColumn<Int16>("Category");
            DataColumn<Byte> status = table.Columns.GetColumn<Byte>("Status");
            DateTime baseTimestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Guid constantKey = new Guid("9E107D9D-3722-4EA1-B84C-B0AB9BF0F0C1");
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                DataRow row = table.Rows.AddNewRow();
                Int32 physicalRowIndex = row.RowIndex;
                id.Set(physicalRowIndex, rowIndex);
                amount.Set(physicalRowIndex, rowIndex * 3L);
                price.Set(physicalRowIndex, 19.99m);
                rate.Set(physicalRowIndex, 0.075d);
                created.Set(physicalRowIndex, baseTimestamp);
                active.Set(physicalRowIndex, (rowIndex & 1) == 0);
                name.Set(physicalRowIndex, namePool[rowIndex % namePool.Length]);
                key.Set(physicalRowIndex, constantKey);
                category.Set(physicalRowIndex, (Int16)(rowIndex % 500));
                status.Set(physicalRowIndex, (Byte)(rowIndex % 7));
            }
        }
        // Declares the framework table's schema, matching the columnar table's column names, types and non-nullable
        // declaration exactly.
        private static System.Data.DataTable BuildFrameworkSchema()
        {
            System.Data.DataTable table = new System.Data.DataTable("Benchmark");
            AddFrameworkColumn(table, "Id", typeof(Int32));
            AddFrameworkColumn(table, "Amount", typeof(Int64));
            AddFrameworkColumn(table, "Price", typeof(Decimal));
            AddFrameworkColumn(table, "Rate", typeof(Double));
            AddFrameworkColumn(table, "Created", typeof(DateTime));
            AddFrameworkColumn(table, "Active", typeof(Boolean));
            AddFrameworkColumn(table, "Name", typeof(String));
            AddFrameworkColumn(table, "Key", typeof(Guid));
            AddFrameworkColumn(table, "Category", typeof(Int16));
            AddFrameworkColumn(table, "Status", typeof(Byte));
            return table;
        }

        // Adds one non-nullable column to a framework table.
        private static void AddFrameworkColumn(System.Data.DataTable table, String name, Type dataType)
        {
            System.Data.DataColumn column = table.Columns.Add(name, dataType);
            column.AllowDBNull = false;
        }
        #endregion
    }
}
