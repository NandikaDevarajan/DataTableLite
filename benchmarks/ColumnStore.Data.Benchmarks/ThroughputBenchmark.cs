///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Measures insert and read throughput for both implementations - the CPU half of the claim, alongside
//   MemoryBenchmark's memory half.
// Assumptions: Every measurement goes through TimedMeasurement, which warms the work up and then repeats it until a
//   meaningful duration has accumulated. That is what makes these figures usable at a hundred rows as well as at a
//   million; a single Stopwatch reading over a hundred-row loop is timer noise, not a measurement.
// Design Considerations: Read throughput is measured twice for the columnar table, on purpose. Through a cached
//   DataColumn<T> handle it is the pattern the library recommends and the number that shows what columnar storage can
//   do. Through the Object indexer it is the pattern a straight source-level port from System.Data.DataTable produces,
//   and it boxes - so it shows what a caller gets before they change a single loop, which is the honest number for
//   "what does a drop-in replacement buy me on day one". Reporting only the first would overstate the library; only
//   the second would understate it.
//   Throughput is expressed in cells per second rather than rows per second, because a row is not a fixed unit of
//   work across tables of different widths and cells are what the two implementations actually differ on.
//   Every read scenario checksums both implementations and compares the results, so a benchmark that quietly read
//   different data on each side fails loudly instead of reporting an impressive number.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Insert and read throughput comparison between the two table implementations.
    /// </summary>
    public sealed class ThroughputBenchmark
    {
        #region Private Constants
        // Index step used by the scattered-read scenario. A prime, so for any realistic row count it walks the table
        // in an order that defeats sequential prefetching instead of settling into a short cycle.
        private const Int32 SCATTER_STRIDE = 65_537;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates the benchmark.
        /// </summary>
        public ThroughputBenchmark()
        {
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Runs the insert and read scenarios against both implementations and records the results.
        /// </summary>
        /// <param name="rowCount">Number of rows to use.</param>
        /// <param name="reporter">Where to record the results.</param>
        public void Run(Int32 rowCount, BenchmarkReporter reporter)
        {
            if (reporter == null) { throw new ArgumentNullException(nameof(reporter)); }
            reporter.WriteHeading("Throughput");
            String[] namePool = BenchmarkTableFactory.BuildNamePool();
            MeasureInserts(rowCount, namePool, reporter);
            DataTable liteTable = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool);
            System.Data.DataTable frameworkTable = BenchmarkTableFactory.BuildFrameworkTable(rowCount, namePool);
            MeasureTypedReads(liteTable, frameworkTable, reporter);
            MeasureObjectReads(liteTable, frameworkTable, reporter);
            MeasureScatteredReads(liteTable, frameworkTable, reporter);
        }
        #endregion

        #region Private Methods
        // Measures how long it takes each implementation to build the whole table from scratch, using its own fastest
        // natural population path.
        private static void MeasureInserts(Int32 rowCount, String[] namePool, BenchmarkReporter reporter)
        {
            Action liteWork = () =>
            {
                DataTable table = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool);
                GC.KeepAlive(table);
            };
            Double liteMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteWork);
            Action frameworkWork = () =>
            {
                System.Data.DataTable table = BenchmarkTableFactory.BuildFrameworkTable(rowCount, namePool);
                GC.KeepAlive(table);
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkWork);
            ReportCellRates("Insert (each side's fastest path)", liteMicroseconds, frameworkMicroseconds, rowCount, BenchmarkTableFactory.ColumnCount, reporter);
        }

        // Measures a full sequential read: the columnar table through cached typed column handles, the framework
        // table through its Object indexer, which is the fastest each one offers.
        private static void MeasureTypedReads(DataTable liteTable, System.Data.DataTable frameworkTable, BenchmarkReporter reporter)
        {
            Int64 liteChecksum = 0;
            Action liteWork = () =>
            {
                liteChecksum = SumViaTypedColumns(liteTable);
            };
            Double liteMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteWork);
            Int64 frameworkChecksum = 0;
            Action frameworkWork = () =>
            {
                frameworkChecksum = SumViaFrameworkIndexer(frameworkTable);
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkWork);
            VerifyChecksums(liteChecksum, frameworkChecksum);
            ReportCellRates("Sequential read (fastest path)", liteMicroseconds, frameworkMicroseconds, liteTable.Rows.Count, BenchmarkTableFactory.ColumnCount, reporter);
        }

        // Measures the same read through the Object indexer on both sides - the shape an unmodified source-level port
        // produces, before the caller adopts typed column handles.
        private static void MeasureObjectReads(DataTable liteTable, System.Data.DataTable frameworkTable, BenchmarkReporter reporter)
        {
            Int64 liteChecksum = 0;
            Action liteWork = () =>
            {
                liteChecksum = SumViaObjectIndexer(liteTable);
            };
            Double liteMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteWork);
            Int64 frameworkChecksum = 0;
            Action frameworkWork = () =>
            {
                frameworkChecksum = SumViaFrameworkIndexer(frameworkTable);
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkWork);
            VerifyChecksums(liteChecksum, frameworkChecksum);
            ReportCellRates("Sequential read (Object indexer)", liteMicroseconds, frameworkMicroseconds, liteTable.Rows.Count, BenchmarkTableFactory.ColumnCount, reporter);
        }

        // Measures scattered single-column reads, which stress cache behaviour rather than sequential prefetching.
        // The access pattern is a fixed prime stride, so both implementations visit exactly the same rows in exactly
        // the same order.
        private static void MeasureScatteredReads(DataTable liteTable, System.Data.DataTable frameworkTable, BenchmarkReporter reporter)
        {
            Int32 rowCount = liteTable.Rows.Count;
            Int32 stride = SCATTER_STRIDE;
            if (stride >= rowCount) { stride = 1; }
            DataColumn<Int32> liteId = liteTable.Columns.GetColumn<Int32>("Id");
            Int64 liteChecksum = 0;
            Action liteWork = () =>
            {
                Int64 total = 0;
                Int32 scatteredIndex = 0;
                for (Int32 step = 0; step < rowCount; step++)
                {
                    total = total + liteId.Get(scatteredIndex);
                    scatteredIndex = (scatteredIndex + stride) % rowCount;
                }
                liteChecksum = total;
            };
            Double liteMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteWork);
            Int64 frameworkChecksum = 0;
            Action frameworkWork = () =>
            {
                Int64 total = 0;
                Int32 scatteredIndex = 0;
                for (Int32 step = 0; step < rowCount; step++)
                {
                    System.Data.DataRow row = frameworkTable.Rows[scatteredIndex];
                    total = total + (Int32)row[0];
                    scatteredIndex = (scatteredIndex + stride) % rowCount;
                }
                frameworkChecksum = total;
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkWork);
            VerifyChecksums(liteChecksum, frameworkChecksum);
            ReportCellRates("Scattered single-column read", liteMicroseconds, frameworkMicroseconds, rowCount, 1, reporter);
        }

        // Records one scenario's pair of measurements as a cell throughput, and prints the per-operation cost
        // alongside it so a reader can see both the rate and the absolute time.
        private static void ReportCellRates(String scenario, Double liteMicroseconds, Double frameworkMicroseconds, Int32 rowCount, Int32 columnCount, BenchmarkReporter reporter)
        {
            Int64 cellsPerIteration = (Int64)rowCount * columnCount;
            Double liteRate = TimedMeasurement.ToMillionItemsPerSecond(liteMicroseconds, cellsPerIteration);
            Double frameworkRate = TimedMeasurement.ToMillionItemsPerSecond(frameworkMicroseconds, cellsPerIteration);
            reporter.WriteLine($"  {scenario,-34} ColumnStore.Data {liteRate,8:N1} vs System.Data {frameworkRate,8:N1} million cells/s  ({liteMicroseconds:N2} us vs {frameworkMicroseconds:N2} us per pass)");
            reporter.Record(new BenchmarkResult(scenario, BenchmarkReporter.LiteImplementation, liteRate, "million cells/s"));
            reporter.Record(new BenchmarkResult(scenario, BenchmarkReporter.FrameworkImplementation, frameworkRate, "million cells/s"));
        }

        // Reads every cell of every row through column handles resolved once, outside the loop - the pattern the
        // library recommends. Returns a checksum so the loop cannot be optimised away.
        private static Int64 SumViaTypedColumns(DataTable table)
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
            Int64 checksum = 0;
            Int32 rowCount = table.Rows.Count;
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                checksum = checksum + id.Get(rowIndex);
                checksum = checksum + amount.Get(rowIndex);
                checksum = checksum + (Int64)price.Get(rowIndex);
                checksum = checksum + (Int64)rate.Get(rowIndex);
                checksum = checksum + created.Get(rowIndex).Year;
                if (active.Get(rowIndex)) { checksum = checksum + 1; }
                checksum = checksum + name.Get(rowIndex).Length;
                checksum = checksum + key.Get(rowIndex).GetHashCode();
                checksum = checksum + category.Get(rowIndex);
                checksum = checksum + status.Get(rowIndex);
            }
            return checksum;
        }

        // Reads every cell of every row through the columnar table's Object indexer - the unmodified-port pattern.
        private static Int64 SumViaObjectIndexer(DataTable table)
        {
            Int64 checksum = 0;
            Int32 rowCount = table.Rows.Count;
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                DataRow row = table.Rows[rowIndex];
                checksum = checksum + (Int32)row[0];
                checksum = checksum + (Int64)row[1];
                checksum = checksum + (Int64)(Decimal)row[2];
                checksum = checksum + (Int64)(Double)row[3];
                checksum = checksum + ((DateTime)row[4]).Year;
                if ((Boolean)row[5]) { checksum = checksum + 1; }
                checksum = checksum + ((String)row[6]).Length;
                checksum = checksum + row[7].GetHashCode();
                checksum = checksum + (Int16)row[8];
                checksum = checksum + (Byte)row[9];
            }
            return checksum;
        }

        // Reads every cell of every row through the framework table's Object indexer, which is its fastest read path.
        private static Int64 SumViaFrameworkIndexer(System.Data.DataTable table)
        {
            Int64 checksum = 0;
            Int32 rowCount = table.Rows.Count;
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                System.Data.DataRow row = table.Rows[rowIndex];
                checksum = checksum + (Int32)row[0];
                checksum = checksum + (Int64)row[1];
                checksum = checksum + (Int64)(Decimal)row[2];
                checksum = checksum + (Int64)(Double)row[3];
                checksum = checksum + ((DateTime)row[4]).Year;
                if ((Boolean)row[5]) { checksum = checksum + 1; }
                checksum = checksum + ((String)row[6]).Length;
                checksum = checksum + row[7].GetHashCode();
                checksum = checksum + (Int16)row[8];
                checksum = checksum + (Byte)row[9];
            }
            return checksum;
        }

        // Confirms both implementations read the same data. A benchmark that quietly reads different rows in each
        // implementation measures nothing, so the comparison is verified rather than assumed.
        private static void VerifyChecksums(Int64 first, Int64 second)
        {
            if (first != second) { throw new InvalidOperationException($"The two implementations produced different checksums ({first} and {second}); the benchmark is not comparing like with like."); }
        }
        #endregion
    }
}
