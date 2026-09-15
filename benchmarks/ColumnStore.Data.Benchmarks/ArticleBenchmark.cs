///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Produces the figures quoted in the public write-up, in one run, so that every number in it can be
//   reproduced by anyone who clones the repository: the cost of a box, per-type column memory, a row-count sweep, the
//   Boolean bitmap, TVP record production, and deletion.
// Assumptions: Workstation GC, no server instance required. Memory is measured as RETAINED bytes after a forced
//   gen-2 collection, not working set, because working set includes the runtime and pages the GC has not returned.
// Design Considerations: Everything here compares like with like. The System.Data arm and the ColumnStore.Data arm
//   are handed the same values in the same order and asked for the same result, so the difference reported is the
//   difference between the two data structures rather than between two ways of writing a loop.
//   The boxing measurement is deliberately the FIRST thing reported, because it is the mechanism behind every other
//   number: one box is 24 bytes on x64 for anything up to 8 bytes of payload, and a row-oriented table pays it per
//   value-typed CELL, on write and again on read.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Data;

using Microsoft.Data.SqlClient.Server;

using ColumnStore.Data.SqlServer;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// The consolidated measurement run behind the public write-up.
    /// </summary>
    public sealed class ArticleBenchmark
    {
        #region Private Constants
        // Bytes in a kilobyte and a megabyte, for reporting.
        private const Double BYTES_PER_KILOBYTE = 1024d;

        // Megabyte divisor, for the large-table rows.
        private const Double BYTES_PER_MEGABYTE = 1048576d;

        // How many cells the boxing probe writes and reads.
        private const Int32 BOXING_CELL_COUNT = 1000000;

        // Rows used by the per-type and TVP comparisons.
        private const Int32 SINGLE_COLUMN_ROW_COUNT = 1000000;

        // Rows used by the TVP comparison. Smaller, because SqlDataRecord population dominates.
        private const Int32 TVP_ROW_COUNT = 200000;

        // Length of the pooled strings, so String columns measure the table rather than string allocation.
        private const Int32 STRING_LENGTH = 24;

        // How many distinct strings the String columns cycle through.
        private const Int32 NAME_POOL_SIZE = 64;
        #endregion

        #region Private Members
        // Shared string pool.
        private readonly String[] namePool;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates the benchmark and its shared string pool.
        /// </summary>
        public ArticleBenchmark()
        {
            this.namePool = new String[NAME_POOL_SIZE];
            for (Int32 poolIndex = 0; poolIndex < NAME_POOL_SIZE; poolIndex++)
            {
                this.namePool[poolIndex] = new String((Char)('a' + poolIndex % 26), STRING_LENGTH);
            }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Runs every measurement and writes the results.
        /// </summary>
        /// <param name="reporter">Where the results are written.</param>
        /// <exception cref="ArgumentNullException">The reporter is null.</exception>
        public void Run(BenchmarkReporter reporter)
        {
            if (reporter == null) { throw new ArgumentNullException(nameof(reporter)); }
            MeasureBoxingCost(reporter);
            MeasurePerTypeColumnMemory(reporter);
            MeasureRowCountSweep(reporter);
            MeasureTvpProduction(reporter);
            MeasureDeletion(reporter);
        }
        #endregion

        #region Private Methods
        // The mechanism behind every other number here: what one box costs in bytes and in time, and what the same
        // work costs when the value never leaves its typed array.
        private void MeasureBoxingCost(BenchmarkReporter reporter)
        {
            reporter.WriteHeading($"1. What a box costs - {BOXING_CELL_COUNT:N0} Int32 cells written then read");
            Int64 boxedBytes = MeasureAllocatedBytes(() =>
            {
                Object[] cells = new Object[BOXING_CELL_COUNT];
                for (Int32 index = 0; index < BOXING_CELL_COUNT; index++)
                {
                    cells[index] = index;
                }
                Int64 total = 0;
                for (Int32 index = 0; index < BOXING_CELL_COUNT; index++)
                {
                    total = total + (Int32)cells[index];
                }
                GC.KeepAlive(total);
            });
            Int64 typedBytes = MeasureAllocatedBytes(() =>
            {
                Int32[] cells = new Int32[BOXING_CELL_COUNT];
                for (Int32 index = 0; index < BOXING_CELL_COUNT; index++)
                {
                    cells[index] = index;
                }
                Int64 total = 0;
                for (Int32 index = 0; index < BOXING_CELL_COUNT; index++)
                {
                    total = total + cells[index];
                }
                GC.KeepAlive(total);
            });
            Double boxedMicroseconds = TimedMeasurement.BestMicrosecondsPerIteration(null, BuildBoxedRoundTrip(), 3);
            Double typedMicroseconds = TimedMeasurement.BestMicrosecondsPerIteration(null, BuildTypedRoundTrip(), 3);
            reporter.WriteLine($"  boxed Object[]   : {boxedBytes / BYTES_PER_MEGABYTE,8:N1} MB   {boxedMicroseconds,10:N0} us   {boxedBytes / (Double)BOXING_CELL_COUNT,6:N1} bytes/cell");
            reporter.WriteLine($"  typed Int32[]    : {typedBytes / BYTES_PER_MEGABYTE,8:N1} MB   {typedMicroseconds,10:N0} us   {typedBytes / (Double)BOXING_CELL_COUNT,6:N1} bytes/cell");
            reporter.WriteLine($"  ratio            : {boxedBytes / (Double)typedBytes,8:N1}x memory, {boxedMicroseconds / typedMicroseconds,7:N1}x time");
        }

        // One column of each type, one million rows, both implementations. This is where the Boolean bitmap shows up.
        private void MeasurePerTypeColumnMemory(BenchmarkReporter reporter)
        {
            reporter.WriteHeading($"2. One column of {SINGLE_COLUMN_ROW_COUNT:N0} rows, by type - retained bytes per row");
            reporter.WriteLine("       type |  System.Data B/row |  ColumnStore B/row |  saved |  ratio");
            Type[] columnTypes = new Type[] { typeof(Boolean), typeof(Byte), typeof(Int16), typeof(Int32), typeof(Int64), typeof(Double), typeof(Decimal), typeof(DateTime), typeof(Guid), typeof(String) };
            for (Int32 typeIndex = 0; typeIndex < columnTypes.Length; typeIndex++)
            {
                Type columnType = columnTypes[typeIndex];
                Int64 frameworkBytes = MeasureRetained(() => BuildSingleColumnFrameworkTable(columnType));
                Int64 columnStoreBytes = MeasureRetained(() => BuildSingleColumnTable(columnType));
                Double frameworkPerRow = frameworkBytes / (Double)SINGLE_COLUMN_ROW_COUNT;
                Double columnStorePerRow = columnStoreBytes / (Double)SINGLE_COLUMN_ROW_COUNT;
                Double saved = (1d - columnStorePerRow / frameworkPerRow) * 100d;
                reporter.WriteLine($"  {columnType.Name,9} | {frameworkPerRow,18:N1} | {columnStorePerRow,18:N1} | {saved,5:N1}% | {frameworkPerRow / columnStorePerRow,5:N1}x");
            }
        }

        // A mixed ten-column table across four orders of magnitude, so the write-up can say how the saving scales.
        private void MeasureRowCountSweep(BenchmarkReporter reporter)
        {
            reporter.WriteHeading("3. Mixed 10-column table - memory and build time by row count");
            reporter.WriteLine("      rows |  System.Data MB |  ColumnStore MB |  saved |  SysData ms |  ColumnStore ms |  faster");
            Int32[] rowCounts = new Int32[] { 1000, 10000, 100000, 1000000 };
            for (Int32 index = 0; index < rowCounts.Length; index++)
            {
                Int32 rowCount = rowCounts[index];
                Int64 frameworkBytes = MeasureRetained(() => BuildMixedFrameworkTable(rowCount));
                Int64 columnStoreBytes = MeasureRetained(() => BuildMixedTable(rowCount));
                Double frameworkMilliseconds = TimedMeasurement.BestMicrosecondsPerIteration(null, () => GC.KeepAlive(BuildMixedFrameworkTable(rowCount)), 3) / 1000d;
                Double columnStoreMilliseconds = TimedMeasurement.BestMicrosecondsPerIteration(null, () => GC.KeepAlive(BuildMixedTable(rowCount)), 3) / 1000d;
                Double saved = (1d - columnStoreBytes / (Double)frameworkBytes) * 100d;
                reporter.WriteLine($"  {rowCount,8:N0} | {frameworkBytes / BYTES_PER_MEGABYTE,15:N1} | {columnStoreBytes / BYTES_PER_MEGABYTE,15:N1} | {saved,5:N1}% | {frameworkMilliseconds,11:N1} | {columnStoreMilliseconds,15:N1} | {frameworkMilliseconds / columnStoreMilliseconds,5:N1}x");
            }
        }

        // Producing the SqlDataRecord stream a table-valued parameter sends. No server is involved: this measures the
        // work of getting the values out of the table and into the record, which is the part the table decides.
        private void MeasureTvpProduction(BenchmarkReporter reporter)
        {
            reporter.WriteHeading($"4. Table-valued parameter - producing {TVP_ROW_COUNT:N0} SqlDataRecords");
            SqlMetaData[] metadata = BuildTvpMetadata();
            DataTable columnStoreTable = BuildMixedTable(TVP_ROW_COUNT);
            System.Data.DataTable frameworkTable = BuildMixedFrameworkTable(TVP_ROW_COUNT);
            DataTableSqlWriter writer = new DataTableSqlWriter();

            Action columnStoreWork = () =>
            {
                Int32 produced = 0;
                foreach (SqlDataRecord record in writer.AsSqlDataRecords(columnStoreTable, metadata))
                {
                    produced = produced + 1;
                }
                GC.KeepAlive(produced);
            };
            Action frameworkWork = () =>
            {
                Int32 produced = 0;
                foreach (SqlDataRecord record in EnumerateFrameworkRecords(frameworkTable, metadata))
                {
                    produced = produced + 1;
                }
                GC.KeepAlive(produced);
            };

            Double columnStoreMicroseconds = TimedMeasurement.BestMicrosecondsPerIteration(null, columnStoreWork, 3);
            Double frameworkMicroseconds = TimedMeasurement.BestMicrosecondsPerIteration(null, frameworkWork, 3);
            Int64 columnStoreAllocated = MeasureAllocatedBytes(columnStoreWork);
            Int64 frameworkAllocated = MeasureAllocatedBytes(frameworkWork);
            reporter.WriteLine("        source |      us |  us/row |  allocated MB |  bytes/row");
            reporter.WriteLine($"   System.Data | {frameworkMicroseconds,7:N0} | {frameworkMicroseconds / TVP_ROW_COUNT,7:N3} | {frameworkAllocated / BYTES_PER_MEGABYTE,13:N1} | {frameworkAllocated / (Double)TVP_ROW_COUNT,10:N1}");
            reporter.WriteLine($"   ColumnStore | {columnStoreMicroseconds,7:N0} | {columnStoreMicroseconds / TVP_ROW_COUNT,7:N3} | {columnStoreAllocated / BYTES_PER_MEGABYTE,13:N1} | {columnStoreAllocated / (Double)TVP_ROW_COUNT,10:N1}");
            reporter.WriteLine($"         ratio | {frameworkMicroseconds / columnStoreMicroseconds,6:N1}x |         | {frameworkAllocated / (Double)Math.Max(columnStoreAllocated, 1L),12:N0}x |");
        }

        // Deleting a tenth of a large table, then reading what is left. Row-oriented deletion shifts an index;
        // tombstoning does not move anything at all.
        private void MeasureDeletion(BenchmarkReporter reporter)
        {
            const Int32 DELETION_ROW_COUNT = 200000;
            reporter.WriteHeading($"5. Deleting every tenth row of {DELETION_ROW_COUNT:N0}, then reading the survivors");
            Double frameworkMicroseconds = TimedMeasurement.BestMicrosecondsPerIteration(
                null,
                () =>
                {
                    System.Data.DataTable table = BuildMixedFrameworkTable(DELETION_ROW_COUNT);
                    for (Int32 rowIndex = table.Rows.Count - 1; rowIndex >= 0; rowIndex = rowIndex - 10)
                    {
                        table.Rows.RemoveAt(rowIndex);
                    }
                    Int64 total = 0;
                    foreach (System.Data.DataRow row in table.Rows) { total = total + (Int32)row[0]; }
                    GC.KeepAlive(total);
                },
                3);
            Double columnStoreMicroseconds = TimedMeasurement.BestMicrosecondsPerIteration(
                null,
                () =>
                {
                    DataTable table = BuildMixedTable(DELETION_ROW_COUNT);
                    for (Int32 rowIndex = table.Rows.Count - 1; rowIndex >= 0; rowIndex = rowIndex - 10)
                    {
                        table.Rows.RemoveAt(rowIndex);
                    }
                    DataColumn<Int32> identifiers = table.Columns.GetColumn<Int32>("Id");
                    Int64 total = 0;
                    foreach (DataRow row in table.Rows) { total = total + identifiers.Get(row.RowIndex); }
                    GC.KeepAlive(total);
                },
                3);
            reporter.WriteLine($"   System.Data : {frameworkMicroseconds / 1000d,9:N1} ms");
            reporter.WriteLine($"   ColumnStore : {columnStoreMicroseconds / 1000d,9:N1} ms   ({frameworkMicroseconds / columnStoreMicroseconds:N1}x faster)");
        }

        // The boxed arm of the round-trip timing: an Object[] written and read, which is what a row-oriented cell is.
        private static Action BuildBoxedRoundTrip()
        {
            Object[] cells = new Object[BOXING_CELL_COUNT];
            return () =>
            {
                for (Int32 index = 0; index < BOXING_CELL_COUNT; index++)
                {
                    cells[index] = index;
                }
                Int64 total = 0;
                for (Int32 index = 0; index < BOXING_CELL_COUNT; index++)
                {
                    total = total + (Int32)cells[index];
                }
                GC.KeepAlive(total);
            };
        }

        // The typed arm of the same round-trip.
        private static Action BuildTypedRoundTrip()
        {
            Int32[] cells = new Int32[BOXING_CELL_COUNT];
            return () =>
            {
                for (Int32 index = 0; index < BOXING_CELL_COUNT; index++)
                {
                    cells[index] = index;
                }
                Int64 total = 0;
                for (Int32 index = 0; index < BOXING_CELL_COUNT; index++)
                {
                    total = total + cells[index];
                }
                GC.KeepAlive(total);
            };
        }

        // Builds a one-column ColumnStore table of the given type, filled.
        private DataTable BuildSingleColumnTable(Type columnType)
        {
            DataTable table = new DataTable("Single");
            table.Columns.Add("Value", columnType, false);
            DataColumn column = table.Columns[0];
            for (Int32 rowIndex = 0; rowIndex < SINGLE_COLUMN_ROW_COUNT; rowIndex++)
            {
                table.Rows.AddNewRow();
                column.SetValue(rowIndex, BuildCellValue(columnType, rowIndex));
            }
            return table;
        }

        // Builds the System.Data equivalent of the above.
        private System.Data.DataTable BuildSingleColumnFrameworkTable(Type columnType)
        {
            System.Data.DataTable table = new System.Data.DataTable("Single");
            table.Columns.Add("Value", columnType);
            Object[] values = new Object[1];
            for (Int32 rowIndex = 0; rowIndex < SINGLE_COLUMN_ROW_COUNT; rowIndex++)
            {
                values[0] = BuildCellValue(columnType, rowIndex);
                table.Rows.Add(values);
            }
            return table;
        }

        // Builds the mixed ten-column ColumnStore table used by the sweep, the TVP arm and the deletion arm.
        private DataTable BuildMixedTable(Int32 rowCount)
        {
            DataTable table = new DataTable("Mixed");
            DataColumn<Int32> identifier = table.Columns.Add<Int32>("Id", false);
            DataColumn<String> name = table.Columns.Add<String>("Name", true);
            DataColumn<Boolean> active = table.Columns.Add<Boolean>("Active", false);
            DataColumn<Decimal> price = table.Columns.Add<Decimal>("Price", true);
            DataColumn<DateTime> created = table.Columns.Add<DateTime>("Created", false);
            DataColumn<Int64> reference = table.Columns.Add<Int64>("Reference", false);
            DataColumn<Guid> key = table.Columns.Add<Guid>("Key", false);
            DataColumn<Int16> category = table.Columns.Add<Int16>("Category", false);
            DataColumn<Double> weight = table.Columns.Add<Double>("Weight", false);
            DataColumn<Boolean> archived = table.Columns.Add<Boolean>("Archived", false);
            DateTime epoch = new DateTime(2020, 1, 1);
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                table.Rows.AddNewRow();
                identifier.Set(rowIndex, rowIndex);
                name.Set(rowIndex, this.namePool[rowIndex % NAME_POOL_SIZE]);
                active.Set(rowIndex, (rowIndex & 1) == 0);
                price.Set(rowIndex, rowIndex * 0.25m);
                created.Set(rowIndex, epoch.AddMinutes(rowIndex));
                reference.Set(rowIndex, rowIndex * 3L);
                key.Set(rowIndex, Guid.Empty);
                category.Set(rowIndex, (Int16)(rowIndex % 500));
                weight.Set(rowIndex, rowIndex * 1.5d);
                archived.Set(rowIndex, (rowIndex % 3) == 0);
            }
            return table;
        }

        // Builds the System.Data equivalent of the mixed table, from the same values in the same order.
        private System.Data.DataTable BuildMixedFrameworkTable(Int32 rowCount)
        {
            System.Data.DataTable table = new System.Data.DataTable("Mixed");
            table.Columns.Add("Id", typeof(Int32));
            table.Columns.Add("Name", typeof(String));
            table.Columns.Add("Active", typeof(Boolean));
            table.Columns.Add("Price", typeof(Decimal));
            table.Columns.Add("Created", typeof(DateTime));
            table.Columns.Add("Reference", typeof(Int64));
            table.Columns.Add("Key", typeof(Guid));
            table.Columns.Add("Category", typeof(Int16));
            table.Columns.Add("Weight", typeof(Double));
            table.Columns.Add("Archived", typeof(Boolean));
            DateTime epoch = new DateTime(2020, 1, 1);
            Object[] values = new Object[10];
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                values[0] = rowIndex;
                values[1] = this.namePool[rowIndex % NAME_POOL_SIZE];
                values[2] = (rowIndex & 1) == 0;
                values[3] = rowIndex * 0.25m;
                values[4] = epoch.AddMinutes(rowIndex);
                values[5] = rowIndex * 3L;
                values[6] = Guid.Empty;
                values[7] = (Int16)(rowIndex % 500);
                values[8] = rowIndex * 1.5d;
                values[9] = (rowIndex % 3) == 0;
                table.Rows.Add(values);
            }
            return table;
        }

        // The metadata describing the mixed table as a table-valued parameter.
        private static SqlMetaData[] BuildTvpMetadata()
        {
            return new SqlMetaData[]
            {
                new SqlMetaData("Id", SqlDbType.Int),
                new SqlMetaData("Name", SqlDbType.NVarChar, STRING_LENGTH),
                new SqlMetaData("Active", SqlDbType.Bit),
                new SqlMetaData("Price", SqlDbType.Decimal, 18, 4),
                new SqlMetaData("Created", SqlDbType.DateTime2),
                new SqlMetaData("Reference", SqlDbType.BigInt),
                new SqlMetaData("Key", SqlDbType.UniqueIdentifier),
                new SqlMetaData("Category", SqlDbType.SmallInt),
                new SqlMetaData("Weight", SqlDbType.Float),
                new SqlMetaData("Archived", SqlDbType.Bit),
            };
        }

        // The System.Data arm of the TVP comparison: the way ADO.NET code produces records from a DataTable, reading
        // each cell as an Object and handing it to SqlDataRecord.SetValue. Every value-typed cell is already boxed in
        // the table, and SetValue keeps it boxed.
        private static IEnumerable<SqlDataRecord> EnumerateFrameworkRecords(System.Data.DataTable table, SqlMetaData[] metadata)
        {
            SqlDataRecord record = new SqlDataRecord(metadata);
            Int32 fieldCount = metadata.Length;
            foreach (System.Data.DataRow row in table.Rows)
            {
                for (Int32 fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                {
                    record.SetValue(fieldIndex, row[fieldIndex]);
                }
                yield return record;
            }
        }

        // A deterministic value of the requested type, so both arms are filled identically.
        private Object BuildCellValue(Type columnType, Int32 rowIndex)
        {
            if (columnType == typeof(Boolean)) { return (rowIndex & 1) == 0; }
            if (columnType == typeof(Byte)) { return (Byte)(rowIndex % 256); }
            if (columnType == typeof(Int16)) { return (Int16)(rowIndex % 30000); }
            if (columnType == typeof(Int32)) { return rowIndex; }
            if (columnType == typeof(Int64)) { return rowIndex * 3L; }
            if (columnType == typeof(Double)) { return rowIndex * 1.5d; }
            if (columnType == typeof(Decimal)) { return rowIndex * 0.25m; }
            if (columnType == typeof(DateTime)) { return new DateTime(2020, 1, 1).AddMinutes(rowIndex); }
            if (columnType == typeof(Guid)) { return Guid.Empty; }
            return this.namePool[rowIndex % NAME_POOL_SIZE];
        }

        // Retained bytes held by whatever the factory returns, measured after forcing the heap into a settled state.
        // The table is built INSIDE this method so it becomes unreachable before the baseline for the next arm.
        private static Int64 MeasureRetained(Func<Object> buildTable)
        {
            Int64 baseline = SettledBytes();
            Object table = buildTable();
            Int64 loaded = SettledBytes();
            GC.KeepAlive(table);
            return loaded - baseline;
        }

        // Bytes allocated by one run of the work, including garbage the collector has already reclaimed.
        private static Int64 MeasureAllocatedBytes(Action work)
        {
            work();
            Int64 before = GC.GetAllocatedBytesForCurrentThread();
            work();
            Int64 after = GC.GetAllocatedBytesForCurrentThread();
            return after - before;
        }

        // Forces the heap into a settled state and returns the bytes it holds.
        private static Int64 SettledBytes()
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            return GC.GetTotalMemory(true);
        }
        #endregion
    }
}
