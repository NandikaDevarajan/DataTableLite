///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Entry point for the benchmark suite. Runs the memory, reader-load, throughput and deletion scenarios
//   against both ColumnStore.Data and System.Data.DataTable, for each row count requested, and prints a comparison.
// Assumptions: Run in Release configuration - a Debug run measures unoptimised code and its numbers mean nothing.
//   Row counts are passed as command-line arguments; with none supplied the suite sweeps 100, 1,000, 10,000, 100,000
//   and 1,000,000 rows, which shows where the columnar layout wins, where it breaks even, and where it loses.
// Design Considerations: This is a purpose-built harness rather than a BenchmarkDotNet suite, for one reason: the
//   headline requirement (NFR-4) is about RETAINED memory, and BenchmarkDotNet measures allocation volume and time,
//   not what a structure still holds after a settled collection. Timing stability at small row counts is handled by
//   TimedMeasurement, which repeats each measured operation until a meaningful duration has accumulated.
//   A SWEEP RATHER THAN A SINGLE SIZE, because the answer genuinely changes with size. Chunked columnar storage has a
//   floor - one chunk per column - so a small table pays for storage it does not use, while a large table amortises
//   that floor to nothing. How big the floor is depends entirely on the chunk size, which is why the chunk-size
//   scenario runs at every row count too. Reporting only the million-row figure would hide a result a caller with
//   many small tables needs to know.
//   The numbers are reported, never asserted. NFR-4 is explicit that the memory target is to be validated
//   empirically rather than treated as a contractual figure, so a benchmark that failed a build would be
//   overstepping what the requirement asks for. The test suite is where contracts are enforced.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Command-line entry point for the benchmark suite.
    /// </summary>
    public static class Program
    {
        #region Private Constants
        // Smallest row count worth measuring. Below a hundred rows every figure is fixed cost.
        private const Int32 MINIMUM_ROW_COUNT = 10;

        // Row count below which the columnar storage floor - one chunk per column - dominates the memory figures, and
        // the report says so rather than leaving the reader to wonder.
        private const Int32 STORAGE_FLOOR_ROW_COUNT = 32_768;
        #endregion

        #region Public Methods
        /// <summary>
        /// Runs the whole suite once per requested row count and prints a report for each.
        /// </summary>
        /// <param name="args">Row counts to measure. Defaults to a sweep from 100 to 1,000,000.</param>
        public static async Task Main(String[] args)
        {
            // Offline analysis of the production table shapes, with no database in the measurement. Run as
            // "dotnet run -c Release -- shapes". Returns before the interactive database harness below.
            Boolean runShapeAnalysis = args != null && args.Length > 0 && String.Equals(args[0], "shapes", StringComparison.OrdinalIgnoreCase);
            if (runShapeAnalysis)
            {
                List<Int32> shapeRowCounts = new List<Int32> { 1_000, 10_000, 50_000 };
                TableShapeLoadBenchmark shapeBenchmark = new TableShapeLoadBenchmark();
                shapeBenchmark.Run(shapeRowCounts);
                return;
            }
            // The consolidated measurement run behind the public write-up. Run as "dotnet run -c Release -- article".
            Boolean runArticleMeasurements = args != null && args.Length > 0 && String.Equals(args[0], "article", StringComparison.OrdinalIgnoreCase);
            if (runArticleMeasurements)
            {
                ArticleBenchmark articleBenchmark = new ArticleBenchmark();
                articleBenchmark.Run(new BenchmarkReporter());
                return;
            }
            // Prices runtime code generation against the shipped loader. Run as "dotnet run -c Release -- codegen".
            Boolean runCodegenAnalysis = args != null && args.Length > 0 && String.Equals(args[0], "codegen", StringComparison.OrdinalIgnoreCase);
            if (runCodegenAnalysis)
            {
                List<Int32> codegenRowCounts = new List<Int32> { 100, 1_000, 10_000, 50_000 };
                CompiledLoaderBenchmark codegenBenchmark = new CompiledLoaderBenchmark();
                codegenBenchmark.Run(codegenRowCounts, new BenchmarkReporter());
                return;
            }


            /*List<Int32> rowCounts = ResolveRowCounts(args);
            WriteEnvironment(rowCounts);
            for (Int32 index = 0; index < rowCounts.Count; index++)
            {
                Int32 rowCount = rowCounts[index];
                RunSuiteForRowCount(rowCount);
            }
            WriteFooter();*/
        }
        #endregion

        #region Private Methods
        // Runs every scenario for one row count, into its own reporter, so each size gets its own self-contained
        // summary table.
        private static void RunSuiteForRowCount(Int32 rowCount)
        {
            BenchmarkReporter reporter = new BenchmarkReporter();
            String banner = $"ROW COUNT: {rowCount:N0}  x  {BenchmarkTableFactory.ColumnCount} columns";
            reporter.WriteBanner(banner);
            MemoryBenchmark memoryBenchmark = new MemoryBenchmark();
            memoryBenchmark.Run(rowCount, reporter);
            ChunkSizeBenchmark chunkSizeBenchmark = new ChunkSizeBenchmark();
            chunkSizeBenchmark.Run(rowCount, reporter);
            LoadBenchmark loadBenchmark = new LoadBenchmark();
            loadBenchmark.Run(rowCount, reporter);
            ThroughputBenchmark throughputBenchmark = new ThroughputBenchmark();
            throughputBenchmark.Run(rowCount, reporter);
            DeletionBenchmark deletionBenchmark = new DeletionBenchmark();
            deletionBenchmark.Run(rowCount, reporter);
            reporter.WriteReport();
            WriteRowCountCaveats(reporter, rowCount);
        }

        // Reads the row counts from the command line, falling back to the default sweep and refusing values too small
        // to measure at all.
        private static List<Int32> ResolveRowCounts(String[] args)
        {
            List<Int32> rowCounts = new List<Int32>();
            if (args == null || args.Length == 0)
            {
                rowCounts.Add(100);
                rowCounts.Add(1_000);
                rowCounts.Add(10_000);
                rowCounts.Add(100_000);
                rowCounts.Add(1_000_000);
                return rowCounts;
            }
            for (Int32 index = 0; index < args.Length; index++)
            {
                Int32 requestedRowCount = 0;
                Boolean parsed = Int32.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out requestedRowCount);
                if (parsed == false) { throw new ArgumentException($"'{args[index]}' is not a row count. Pass one or more integers, or no argument at all for the default sweep."); }
                if (requestedRowCount < MINIMUM_ROW_COUNT) { throw new ArgumentException($"A row count below {MINIMUM_ROW_COUNT:N0} is entirely fixed cost and measures nothing."); }
                rowCounts.Add(requestedRowCount);
            }
            return rowCounts;
        }

        // Prints what was measured and on what, so a saved report can be interpreted later.
        private static void WriteEnvironment(List<Int32> rowCounts)
        {
            BenchmarkReporter reporter = new BenchmarkReporter();
            reporter.WriteBanner("ColumnStore.Data benchmarks");
            reporter.WriteLine($"Runtime          : {Environment.Version}");
            reporter.WriteLine($"OS               : {Environment.OSVersion}");
            reporter.WriteLine($"Processors       : {Environment.ProcessorCount}");
            reporter.WriteLine($"64-bit process   : {Environment.Is64BitProcess}");
            reporter.WriteLine($"Server GC        : {System.Runtime.GCSettings.IsServerGC}");
            reporter.WriteLine($"Columns          : {BenchmarkTableFactory.ColumnCount} (Int32, Int64, Decimal, Double, DateTime, Boolean, String, Guid, Int16, Byte)");
            String rowCountList = String.Join(", ", rowCounts.ConvertAll(count => count.ToString("N0", CultureInfo.InvariantCulture)));
            reporter.WriteLine($"Row counts       : {rowCountList}");
        }

        // Prints the caveats specific to the row count just measured. Small tables are where the storage chunk size
        // decides the memory figure, so the report points at the chunk-size section rather than leaving a reader to
        // wonder why a hundred-row table's bytes-per-row looks the way it does.
        private static void WriteRowCountCaveats(BenchmarkReporter reporter, Int32 rowCount)
        {
            if (rowCount >= STORAGE_FLOOR_ROW_COUNT) { return; }
            Int32 defaultRowCount = ColumnStore.Data.Storage.ChunkSizing.DefaultRowCount;
            reporter.WriteLine(String.Empty);
            reporter.WriteLine($"  Note: at {rowCount:N0} rows the memory figure is decided by the storage chunk size, because every");
            reporter.WriteLine($"  non-Boolean column allocates its first chunk in full. At the current default of {defaultRowCount:N0} rows per");
            reporter.WriteLine("  chunk that floor is small; at the 16-64 KB size NFR-3 describes it would dominate. The chunk-size");
            reporter.WriteLine("  section above quantifies the trade at this row count. Timing figures are unaffected either way and");
            reporter.WriteLine("  are averaged over many repetitions.");
        }

        // Prints the caveats a reader needs in order to interpret the ratios without over-reading them.
        private static void WriteFooter()
        {
            BenchmarkReporter reporter = new BenchmarkReporter();
            reporter.WriteBanner("Notes");
            reporter.WriteLine("  * Ratios are expressed as the factor by which ColumnStore.Data is better: cost rows (MB, us)");
            reporter.WriteLine("    read 'uses this many times less', rate rows (per second) read 'does this many times more'.");
            reporter.WriteLine("  * Every timing is the average of as many repetitions as fit into a quarter-second of measured");
            reporter.WriteLine("    work, after a warm-up. Single-shot Stopwatch readings would be timer noise at small sizes.");
            reporter.WriteLine("  * 'Load from IDataReader' is the apples-to-apples comparison: both sides are handed the same");
            reporter.WriteLine("    System.Data.DataTableReader and asked to fill a table through their own public API");
            reporter.WriteLine("    (DataTableLoader.Load against System.Data.DataTable.Load).");
            reporter.WriteLine("  * The 'Insert' row instead uses each side's fastest hand-written population path: typed column");
            reporter.WriteLine("    handles for ColumnStore.Data, BeginLoadData with positional Object arrays for System.Data.");
            reporter.WriteLine("  * Both tables are populated from a shared 128-string pool, so the string payload is identical");
            reporter.WriteLine("    on both sides and the memory difference that remains is structural.");
            reporter.WriteLine("  * 'Sequential read (Object indexer)' is the number an unmodified source-level port gets on day");
            reporter.WriteLine("    one; 'fastest path' is what adopting typed column handles buys.");
            reporter.WriteLine("  * Front-removal is capped at 100,000 rows, because physically shifting a larger table one row");
            reporter.WriteLine("    at a time takes minutes per repetition on the framework side.");
            reporter.WriteLine("  * Numbers are reported, never asserted. Contracts live in the test suite.");
            reporter.WriteLine("  * The chunk-size section compares ColumnStore.Data against itself. The library default is a fixed");
            reporter.WriteLine("    128 rows per chunk, a deliberate deviation from NFR-3's 16-64 KB band; that band's figure is");
            reporter.WriteLine("    still computed by ChunkSizing.LohSafeRowCount<T>() and can be passed per column to");
            reporter.WriteLine("    Columns.Add<T>(name, allowNull, chunkRowCount).");
        }
        #endregion
    }
}
