///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Measures what the storage chunk size actually costs and saves, by building the same table at several chunk
//   sizes and reporting retained memory, insert cost and sequential-read throughput for each.
// Assumptions: Only the columnar table is varied - there is no framework equivalent of a chunk size, so this scenario
//   compares ColumnStore.Data against itself rather than against System.Data.
// Design Considerations: THIS SCENARIO EXISTS BECAUSE THE DEFAULT CHANGED. ChunkSizing's default is now a fixed 128
//   rows per chunk rather than the 16-64 KB figure NFR-3 describes, and that is a trade rather than an improvement:
//   small tables stop paying for storage they do not use, large tables gain thousands of small arrays per column.
//   Guessing which way that lands is exactly the kind of thing a benchmark is for, so the two settings - and the ones
//   in between - are measured side by side at whatever row count the suite is running.
//   The sizes swept are powers of two spanning three orders of magnitude, plus the LOH-safe figure the sizing rule
//   computes per element type. That last one is not a single number across the table: an Int32 column's LOH-safe size
//   is 8,192 rows while a Decimal column's is 2,048, so it is applied per column rather than as a fixed count, which
//   is why it is measured through the default-sizing path rather than as another explicit size.
//   Retained memory is measured the same way MemoryBenchmark measures it - a settled heap either side of the build -
//   so the figures in the two sections are directly comparable.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Compares storage chunk sizes against each other: retained memory, insert cost and read throughput.
    /// </summary>
    public sealed class ChunkSizeBenchmark
    {
        #region Private Constants
        // Bytes in a kilobyte, for reporting a chunk's byte size.
        private const Double BYTES_PER_KILOBYTE = 1024d;

        // Bytes in a megabyte, for reporting retained memory.
        private const Double BYTES_PER_MEGABYTE = 1024d * 1024d;

        // Complete measurements taken per arm, of which the fastest is reported. The arms of this sweep are
        // structurally near-identical, so a single measurement disturbed by tiered recompilation or a background
        // collection would invert the comparison it exists to make.
        private const Int32 MEASUREMENT_ATTEMPTS = 3;
        #endregion

        #region Private Members
        // Explicit chunk row counts to sweep, smallest first. Powers of two, as every chunk size must be.
        private readonly Int32[] chunkRowCounts;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates the benchmark with the default sweep of chunk sizes.
        /// </summary>
        public ChunkSizeBenchmark()
        {
            this.chunkRowCounts = new Int32[] { 128, 512, 2048, 8192, 32768 };
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Builds the benchmark table at each swept chunk size and reports what each one costs.
        /// </summary>
        /// <param name="rowCount">Number of rows to build.</param>
        /// <param name="reporter">Where to write the results.</param>
        public void Run(Int32 rowCount, BenchmarkReporter reporter)
        {
            if (reporter == null) { throw new ArgumentNullException(nameof(reporter)); }
            reporter.WriteHeading("Storage chunk size (ColumnStore.Data only)");
            Int32 defaultRowCount = ColumnStore.Data.Storage.ChunkSizing.DefaultRowCount;
            reporter.WriteLine($"Library default is {defaultRowCount:N0} rows per chunk. Int32 column chunk = {defaultRowCount * 4 / BYTES_PER_KILOBYTE:N2} KB, Decimal = {defaultRowCount * 16 / BYTES_PER_KILOBYTE:N2} KB.");
            WarmUpEveryArm(rowCount);
            reporter.WriteLine("  chunk rows   Int32 chunk    retained MB      chunks/col       insert us     read Mcells/s");
            for (Int32 index = 0; index < this.chunkRowCounts.Length; index++)
            {
                Int32 chunkRowCount = this.chunkRowCounts[index];
                MeasureChunkSize(rowCount, chunkRowCount, defaultRowCount, reporter);
            }
        }
        #endregion

        #region Private Methods
        // Builds and reads a table at every swept chunk size once, discarding the results, before any arm is
        // measured. Without this the first arm measured pays for tiering the build and read loops and every later
        // arm runs on already-optimised code, which shows up as the first arm looking slower than it is - an
        // ordering bias, not a chunk-size effect.
        private void WarmUpEveryArm(Int32 rowCount)
        {
            String[] namePool = BenchmarkTableFactory.BuildNamePool();
            for (Int32 index = 0; index < this.chunkRowCounts.Length; index++)
            {
                Int32 chunkRowCount = this.chunkRowCounts[index];
                DataTable table = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool, chunkRowCount);
                Int64 checksum = SumEveryCell(table);
                if (checksum == Int64.MinValue) { throw new InvalidOperationException("Unreachable; keeps the warm-up from being elided."); }
            }
        }

        // Builds and measures the table at one chunk size, printing a row of the sweep table.
        private static void MeasureChunkSize(Int32 rowCount, Int32 chunkRowCount, Int32 defaultRowCount, BenchmarkReporter reporter)
        {
            String[] namePool = BenchmarkTableFactory.BuildNamePool();
            Int64 retainedBytes = MeasureRetainedBytes(rowCount, chunkRowCount, namePool);
            Action insertWork = () =>
            {
                DataTable built = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool, chunkRowCount);
                GC.KeepAlive(built);
            };
            Double insertMicroseconds = TimedMeasurement.BestMicrosecondsPerIteration(null, insertWork, MEASUREMENT_ATTEMPTS);
            DataTable table = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool, chunkRowCount);
            Int64 checksum = 0;
            Action readWork = () =>
            {
                checksum = SumEveryCell(table);
            };
            Double readMicroseconds = TimedMeasurement.BestMicrosecondsPerIteration(null, readWork, MEASUREMENT_ATTEMPTS);
            if (checksum == Int64.MinValue) { throw new InvalidOperationException("Unreachable; keeps the read loop from being elided."); }
            Int64 cellCount = (Int64)rowCount * BenchmarkTableFactory.ColumnCount;
            Double readRate = TimedMeasurement.ToMillionItemsPerSecond(readMicroseconds, cellCount);
            Double chunksPerColumn = Math.Ceiling(rowCount / (Double)chunkRowCount);
            Double int32ChunkKilobytes = chunkRowCount * 4 / BYTES_PER_KILOBYTE;
            String defaultMarker = chunkRowCount == defaultRowCount ? " <- default" : String.Empty;
            reporter.WriteLine($"  {chunkRowCount,10:N0}   {int32ChunkKilobytes,8:N2} KB   {retainedBytes / BYTES_PER_MEGABYTE,12:N3}   {chunksPerColumn,13:N0}   {insertMicroseconds,13:N1}   {readRate,15:N1}{defaultMarker}");
        }

        // Measures what one chunk size's table retains, with a settled heap either side of the build - the same
        // method MemoryBenchmark uses, so the two sections' figures are comparable.
        private static Int64 MeasureRetainedBytes(Int32 rowCount, Int32 chunkRowCount, String[] namePool)
        {
            Int64 settledBefore = SettleHeapAndMeasure();
            DataTable table = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool, chunkRowCount);
            Int64 settledAfter = SettleHeapAndMeasure();
            GC.KeepAlive(table);
            return settledAfter - settledBefore;
        }

        // Forces the heap into a settled state and returns the bytes it holds.
        private static Int64 SettleHeapAndMeasure()
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            return GC.GetTotalMemory(true);
        }

        // Reads every cell of every row through typed column handles resolved once - the pattern whose locality the
        // chunk size most directly affects.
        private static Int64 SumEveryCell(DataTable table)
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
        #endregion
    }
}
