///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Measures the operation most real code actually performs - filling a table from an ADO.NET IDataReader -
//   comparing DataTableLoader.Load against System.Data.DataTable.Load on identical input.
// Assumptions: The source data is served by System.Data.DataTableReader over a pre-built table. Both sides consume
//   exactly the same reader type over exactly the same rows, so whatever that reader costs, it costs both of them
//   equally and cancels out of the comparison.
// Design Considerations: WHY THIS SCENARIO NEEDED ITS OWN BENCHMARK. The insert scenario in ThroughputBenchmark
//   measures each implementation's best hand-written population path, which flatters the columnar side because it
//   gets to use typed column handles. Loading from a reader is the honest apples-to-apples comparison: both sides are
//   handed the same IDataReader and asked to do the same job through their own public API, with no help.
//   Two variants are measured, because they exercise different amounts of work. "New table" includes schema creation
//   from the reader's metadata - which for DataTableLoader means reading GetSchemaTable and building columns, paid
//   once per load. "Existing schema" is the repeat-load path FR-18 requires, where the columns already exist and only
//   rows are appended; it isolates the per-row cost from the per-load setup, and is what a caller paging through a
//   large result set into one table actually pays.
//   System.Data.DataTable.Load is the fairest counterpart to compare against: it is the framework's own bulk reader
//   ingestion API, it suspends index maintenance internally, and it is what a developer would reach for. A manual
//   while(reader.Read()) loop with per-cell GetValue calls would be slower still, and beating that would prove less.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;

using ColumnStore.Data.SqlServer;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// IDataReader ingestion comparison: <see cref="DataTableLoader"/> against
    /// <see cref="System.Data.DataTable.Load(IDataReader)"/>.
    /// </summary>
    public sealed class LoadBenchmark
    {
        #region Constructor / Destructor
        /// <summary>
        /// Creates the benchmark.
        /// </summary>
        public LoadBenchmark()
        {
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Loads the same reader into each implementation, into a fresh table and then into a pre-schema'd one, and
        /// reports the cost per load and the resulting cell throughput.
        /// </summary>
        /// <param name="rowCount">Number of rows the source reader serves.</param>
        /// <param name="reporter">Where to record the results.</param>
        public void Run(Int32 rowCount, BenchmarkReporter reporter)
        {
            if (reporter == null) { throw new ArgumentNullException(nameof(reporter)); }
            reporter.WriteHeading("Load from IDataReader");
            String[] namePool = BenchmarkTableFactory.BuildNamePool();
            System.Data.DataTable sourceTable = BenchmarkTableFactory.BuildFrameworkTable(rowCount, namePool);
            VerifyLoadsAgree(sourceTable, rowCount);
            reporter.WriteLine($"Source: a System.Data.DataTableReader over {rowCount:N0} rows x {BenchmarkTableFactory.ColumnCount} columns, identical for both.");
            MeasureLoadIntoNewTable(sourceTable, rowCount, reporter);
            MeasureLoadIntoExistingSchema(sourceTable, rowCount, reporter);
        }
        #endregion

        #region Private Methods
        // Times a load into a table with no schema, so each iteration includes reading the reader's metadata and
        // creating columns as well as appending rows.
        private static void MeasureLoadIntoNewTable(System.Data.DataTable sourceTable, Int32 rowCount, BenchmarkReporter reporter)
        {
            DataTableLoader loader = new DataTableLoader();
            Action liteWork = () =>
            {
                IDataReader reader = sourceTable.CreateDataReader();
                DataTable table = new DataTable("Loaded");
                loader.Load(reader, true, table);
                reader.Dispose();
            };
            Double liteMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteWork);
            Action frameworkWork = () =>
            {
                IDataReader reader = sourceTable.CreateDataReader();
                System.Data.DataTable table = new System.Data.DataTable("Loaded");
                table.Load(reader);
                reader.Dispose();
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkWork);
            ReportLoadPair("Load into new table", liteMicroseconds, frameworkMicroseconds, rowCount, reporter);
        }

        // Times a load into a table whose columns already exist - the repeat-load path of FR-18. The table is cleared
        // before each timed iteration, and that clearing is excluded from the timing.
        private static void MeasureLoadIntoExistingSchema(System.Data.DataTable sourceTable, Int32 rowCount, BenchmarkReporter reporter)
        {
            DataTableLoader loader = new DataTableLoader();
            DataTable liteTable = BuildEmptyLiteSchema();
            Action liteSetup = () =>
            {
                liteTable.Clear();
            };
            Action liteWork = () =>
            {
                IDataReader reader = sourceTable.CreateDataReader();
                loader.Load(reader, false, liteTable);
                reader.Dispose();
            };
            Double liteMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteSetup, liteWork);
            System.Data.DataTable frameworkTable = BuildEmptyFrameworkSchema();
            Action frameworkSetup = () =>
            {
                frameworkTable.Clear();
            };
            Action frameworkWork = () =>
            {
                IDataReader reader = sourceTable.CreateDataReader();
                frameworkTable.Load(reader);
                reader.Dispose();
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkSetup, frameworkWork);
            ReportLoadPair("Load into existing schema", liteMicroseconds, frameworkMicroseconds, rowCount, reporter);
        }

        // Records one scenario's pair of measurements as both a per-load cost and a cell throughput, so the same
        // measurement can be read either way.
        private static void ReportLoadPair(String scenario, Double liteMicroseconds, Double frameworkMicroseconds, Int32 rowCount, BenchmarkReporter reporter)
        {
            Int64 cellsPerLoad = (Int64)rowCount * BenchmarkTableFactory.ColumnCount;
            Double liteRate = TimedMeasurement.ToMillionItemsPerSecond(liteMicroseconds, cellsPerLoad);
            Double frameworkRate = TimedMeasurement.ToMillionItemsPerSecond(frameworkMicroseconds, cellsPerLoad);
            reporter.WriteLine($"  {scenario,-26} ColumnStore.Data {liteMicroseconds:N2} us vs System.Data {frameworkMicroseconds:N2} us per load");
            reporter.Record(new BenchmarkResult(scenario, BenchmarkReporter.LiteImplementation, liteMicroseconds, "us/load"));
            reporter.Record(new BenchmarkResult(scenario, BenchmarkReporter.FrameworkImplementation, frameworkMicroseconds, "us/load"));
            reporter.Record(new BenchmarkResult(scenario + " throughput", BenchmarkReporter.LiteImplementation, liteRate, "million cells/s"));
            reporter.Record(new BenchmarkResult(scenario + " throughput", BenchmarkReporter.FrameworkImplementation, frameworkRate, "million cells/s"));
        }

        // Declares the columnar table's schema without any rows, matching the source reader's fields.
        private static DataTable BuildEmptyLiteSchema()
        {
            DataTable table = new DataTable("Loaded");
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

        // Declares the framework table's schema without any rows, matching the source reader's fields.
        private static System.Data.DataTable BuildEmptyFrameworkSchema()
        {
            System.Data.DataTable table = BenchmarkTableFactory.BuildFrameworkTable(0, BenchmarkTableFactory.BuildNamePool());
            return table;
        }

        // Confirms both implementations actually ingested the same data before any timing is reported. A load
        // benchmark that quietly dropped a column or a row would otherwise report a very impressive number.
        private static void VerifyLoadsAgree(System.Data.DataTable sourceTable, Int32 rowCount)
        {
            DataTableLoader loader = new DataTableLoader();
            DataTable liteTable = new DataTable("Verify");
            using (IDataReader liteReader = sourceTable.CreateDataReader())
            {
                loader.Load(liteReader, true, liteTable);
            }
            System.Data.DataTable frameworkTable = new System.Data.DataTable("Verify");
            using (IDataReader frameworkReader = sourceTable.CreateDataReader())
            {
                frameworkTable.Load(frameworkReader);
            }
            if (liteTable.Rows.Count != rowCount) { throw new InvalidOperationException($"ColumnStore.Data loaded {liteTable.Rows.Count} rows, expected {rowCount}."); }
            if (frameworkTable.Rows.Count != rowCount) { throw new InvalidOperationException($"System.Data loaded {frameworkTable.Rows.Count} rows, expected {rowCount}."); }
            if (liteTable.Columns.Count != frameworkTable.Columns.Count) { throw new InvalidOperationException($"The two loads produced different column counts ({liteTable.Columns.Count} and {frameworkTable.Columns.Count})."); }
            Int64 liteChecksum = ChecksumLite(liteTable);
            Int64 frameworkChecksum = ChecksumFramework(frameworkTable);
            if (liteChecksum != frameworkChecksum) { throw new InvalidOperationException($"The two loads produced different data ({liteChecksum} and {frameworkChecksum}); the benchmark is not comparing like with like."); }
        }

        // Checksums the columnar table's loaded contents, touching every column so a mis-bound field is detected.
        private static Int64 ChecksumLite(DataTable table)
        {
            DataColumn<Int32> id = table.Columns.GetColumn<Int32>("Id");
            DataColumn<Int64> amount = table.Columns.GetColumn<Int64>("Amount");
            DataColumn<Decimal> price = table.Columns.GetColumn<Decimal>("Price");
            DataColumn<Boolean> active = table.Columns.GetColumn<Boolean>("Active");
            DataColumn<String> name = table.Columns.GetColumn<String>("Name");
            DataColumn<Int16> category = table.Columns.GetColumn<Int16>("Category");
            DataColumn<Byte> status = table.Columns.GetColumn<Byte>("Status");
            Int64 checksum = 0;
            foreach (DataRow row in table.Rows)
            {
                Int32 rowIndex = row.RowIndex;
                checksum = checksum + id.Get(rowIndex);
                checksum = checksum + amount.Get(rowIndex);
                checksum = checksum + (Int64)price.Get(rowIndex);
                if (active.Get(rowIndex)) { checksum = checksum + 1; }
                checksum = checksum + name.Get(rowIndex).Length;
                checksum = checksum + category.Get(rowIndex);
                checksum = checksum + status.Get(rowIndex);
            }
            return checksum;
        }

        // Checksums the framework table's loaded contents the same way, so the two are directly comparable.
        private static Int64 ChecksumFramework(System.Data.DataTable table)
        {
            Int64 checksum = 0;
            foreach (System.Data.DataRow row in table.Rows)
            {
                checksum = checksum + (Int32)row[0];
                checksum = checksum + (Int64)row[1];
                checksum = checksum + (Int64)(Decimal)row[2];
                if ((Boolean)row[5]) { checksum = checksum + 1; }
                checksum = checksum + ((String)row[6]).Length;
                checksum = checksum + (Int16)row[8];
                checksum = checksum + (Byte)row[9];
            }
            return checksum;
        }
        #endregion
    }
}
