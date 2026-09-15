///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Measures the headline claim of NFR-4: how much memory a representative large table actually retains,
//   column-oriented versus row-oriented, plus how much garbage each one produces while being built.
// Assumptions: The workstation, non-concurrent GC is configured in the project file, so a forced collection really
//   does settle the heap before each measurement. Each table is measured in its own process phase and released
//   before the next, so the two numbers do not contend for the same heap.
// Design Considerations: Two numbers are reported rather than one, because they answer different questions. RETAINED
//   memory is what the table costs you for as long as you hold it - the number that decides whether a million rows
//   fits in a service's budget. TOTAL ALLOCATED is the garbage produced while loading - the number that decides how
//   much GC pressure the load puts on everything else running in the process. A row-oriented table is worse on both,
//   for different reasons: a per-row object and an Object[] per row for the first, plus a box per value-typed cell
//   for the second.
//   GC.GetTotalMemory(true) is used rather than the process working set, because working set includes the runtime,
//   the JIT's own allocations and pages the GC has not returned, none of which the table is responsible for.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Retained-memory and allocation-volume comparison between the two table implementations.
    /// </summary>
    public sealed class MemoryBenchmark
    {
        #region Private Constants
        // Bytes in a megabyte, for reporting.
        private const Double BYTES_PER_MEGABYTE = 1024d * 1024d;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates the benchmark.
        /// </summary>
        public MemoryBenchmark()
        {
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Builds the benchmark table in both implementations and reports retained memory and total allocation.
        /// </summary>
        /// <param name="rowCount">Number of rows to build.</param>
        /// <param name="reporter">Where to record the results.</param>
        public void Run(Int32 rowCount, BenchmarkReporter reporter)
        {
            if (reporter == null) { throw new ArgumentNullException(nameof(reporter)); }
            reporter.WriteHeading("Memory");
            reporter.WriteLine($"Building {rowCount:N0} rows x {BenchmarkTableFactory.ColumnCount} columns in each implementation.");
            String[] namePool = BenchmarkTableFactory.BuildNamePool();
            MeasureLite(rowCount, namePool, reporter);
            MeasureFramework(rowCount, namePool, reporter);
        }
        #endregion

        #region Private Methods
        // Measures the columnar table, reporting retained bytes and the bytes allocated while building it.
        private static void MeasureLite(Int32 rowCount, String[] namePool, BenchmarkReporter reporter)
        {
            Int64 settledBefore = SettleHeapAndMeasure();
            Int64 allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            DataTable table = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool);
            Int64 allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            Int64 settledAfter = SettleHeapAndMeasure();
            Int64 retainedBytes = settledAfter - settledBefore;
            Int64 allocatedBytes = allocatedAfter - allocatedBefore;
            reporter.WriteLine($"  ColumnStore.Data   retained {ToMegabytes(retainedBytes):N1} MB, allocated {ToMegabytes(allocatedBytes):N1} MB, {retainedBytes / (Double)rowCount:N1} bytes/row");
            reporter.Record(new BenchmarkResult("Retained memory", BenchmarkReporter.LiteImplementation, ToMegabytes(retainedBytes), "MB"));
            reporter.Record(new BenchmarkResult("Allocated while building", BenchmarkReporter.LiteImplementation, ToMegabytes(allocatedBytes), "MB"));
            reporter.Record(new BenchmarkResult("Retained bytes per row", BenchmarkReporter.LiteImplementation, retainedBytes / (Double)rowCount, "bytes/row"));
            GC.KeepAlive(table);
        }

        // Measures the framework table the same way. Recorded second so the reporter pairs it with the columnar
        // result for the same scenario.
        private static void MeasureFramework(Int32 rowCount, String[] namePool, BenchmarkReporter reporter)
        {
            Int64 settledBefore = SettleHeapAndMeasure();
            Int64 allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            System.Data.DataTable table = BenchmarkTableFactory.BuildFrameworkTable(rowCount, namePool);
            Int64 allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            Int64 settledAfter = SettleHeapAndMeasure();
            Int64 retainedBytes = settledAfter - settledBefore;
            Int64 allocatedBytes = allocatedAfter - allocatedBefore;
            reporter.WriteLine($"  System.Data     retained {ToMegabytes(retainedBytes):N1} MB, allocated {ToMegabytes(allocatedBytes):N1} MB, {retainedBytes / (Double)rowCount:N1} bytes/row");
            reporter.Record(new BenchmarkResult("Retained memory", BenchmarkReporter.FrameworkImplementation, ToMegabytes(retainedBytes), "MB"));
            reporter.Record(new BenchmarkResult("Allocated while building", BenchmarkReporter.FrameworkImplementation, ToMegabytes(allocatedBytes), "MB"));
            reporter.Record(new BenchmarkResult("Retained bytes per row", BenchmarkReporter.FrameworkImplementation, retainedBytes / (Double)rowCount, "bytes/row"));
            GC.KeepAlive(table);
        }

        // Forces the heap into a settled state and returns the bytes it holds. Two collections with finalisers drained
        // in between, because a single pass can leave finalisable objects uncollected and report memory that is about
        // to be released.
        private static Int64 SettleHeapAndMeasure()
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            return GC.GetTotalMemory(true);
        }

        // Converts a byte count to megabytes for reporting.
        private static Double ToMegabytes(Int64 byteCount)
        {
            return byteCount / BYTES_PER_MEGABYTE;
        }
        #endregion
    }
}
