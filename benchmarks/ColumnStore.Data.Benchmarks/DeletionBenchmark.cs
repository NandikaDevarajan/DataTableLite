///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Measures what FR-13 is actually worth: how long it takes to remove a tenth of a table's rows, and how fast
//   the survivors iterate afterwards.
// Assumptions: Both implementations are asked to remove the same logical rows, so the comparison is of the deletion
//   mechanism rather than of which rows were chosen.
// Design Considerations: This is the scenario where the two designs diverge most sharply, and the reason the design
//   insists on logical deletion. System.Data.DataTable's Rows.RemoveAt physically removes the row from its
//   collection, so removing rows from anywhere but the very end shifts everything after it - O(rows) per call, and
//   O(rows squared) for a batch. Tombstoning marks a bit and updates a Fenwick node: O(log rows) per call. The
//   removals here are therefore deliberately taken from the FRONT of the table, which is the worst case for a
//   physically shifting collection and the case that shows the difference in kind rather than in degree.
//   Deletion mutates the table it measures, so each timed iteration needs a freshly built table. That rebuild is
//   passed to TimedMeasurement as a setup delegate and excluded from the timing - otherwise the measurement would be
//   dominated by construction rather than by deletion.
//   Iteration after deletion is measured too, because a tombstoned table has to skip its holes and that cost is the
//   fair price of the cheap delete. It should be near-invisible, since whole 64-row words of deleted rows are skipped
//   in a single step.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Deletion-cost and post-deletion-iteration comparison between the two table implementations.
    /// </summary>
    public sealed class DeletionBenchmark
    {
        #region Private Constants
        // Fraction of the table removed by the scenario, as a divisor: 10 means one row in ten.
        private const Int32 DELETION_DIVISOR = 10;

        // Row count above which the front-removal scenario is skipped for both sides. Physically shifting a
        // multi-hundred-thousand-row table one row at a time takes minutes per iteration, and the point is made long
        // before that.
        private const Int32 FRONT_REMOVAL_ROW_LIMIT = 100_000;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates the benchmark.
        /// </summary>
        public DeletionBenchmark()
        {
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Removes one row in ten from the front of each table and reports the cost, then reports how fast the
        /// remaining rows iterate.
        /// </summary>
        /// <param name="rowCount">Number of rows to build before deleting.</param>
        /// <param name="reporter">Where to record the results.</param>
        public void Run(Int32 rowCount, BenchmarkReporter reporter)
        {
            if (reporter == null) { throw new ArgumentNullException(nameof(reporter)); }
            reporter.WriteHeading("Deletion");
            String[] namePool = BenchmarkTableFactory.BuildNamePool();
            Int32 frontRemovalRowCount = Math.Min(rowCount, FRONT_REMOVAL_ROW_LIMIT);
            reporter.WriteLine($"Removing {frontRemovalRowCount / DELETION_DIVISOR:N0} rows from the FRONT of a {frontRemovalRowCount:N0} row table (worst case for a shifting collection).");
            MeasureFrontRemoval(frontRemovalRowCount, namePool, reporter);
            reporter.WriteLine($"Then iterating a {rowCount:N0} row table from which every tenth row has been removed.");
            MeasureIterationAfterDeletion(rowCount, namePool, reporter);
        }
        #endregion

        #region Private Methods
        // Times removing the first tenth of the rows, one at a time, in both implementations. The table is rebuilt
        // before each timed iteration and the rebuild is excluded from the timing.
        private static void MeasureFrontRemoval(Int32 rowCount, String[] namePool, BenchmarkReporter reporter)
        {
            Int32 deletionCount = rowCount / DELETION_DIVISOR;
            DataTable liteTable = null;
            Action liteSetup = () =>
            {
                liteTable = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool);
            };
            Action liteWork = () =>
            {
                for (Int32 deletion = 0; deletion < deletionCount; deletion++)
                {
                    liteTable.Rows.RemoveAt(0);
                }
            };
            Double liteMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteSetup, liteWork);
            System.Data.DataTable frameworkTable = null;
            Action frameworkSetup = () =>
            {
                frameworkTable = BenchmarkTableFactory.BuildFrameworkTable(rowCount, namePool);
            };
            Action frameworkWork = () =>
            {
                for (Int32 deletion = 0; deletion < deletionCount; deletion++)
                {
                    frameworkTable.Rows.RemoveAt(0);
                }
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkSetup, frameworkWork);
            VerifyRowCounts(liteTable.Rows.Count, frameworkTable.Rows.Count);
            reporter.WriteLine($"  {"Remove first tenth of rows",-34} ColumnStore.Data {liteMicroseconds,10:N1} vs System.Data {frameworkMicroseconds,10:N1} us per batch");
            reporter.Record(new BenchmarkResult("Remove first tenth of rows", BenchmarkReporter.LiteImplementation, liteMicroseconds, "us/batch"));
            reporter.Record(new BenchmarkResult("Remove first tenth of rows", BenchmarkReporter.FrameworkImplementation, frameworkMicroseconds, "us/batch"));
        }

        // Times a full iteration of each table after every tenth row has been removed, to price the cost of skipping
        // tombstones against iterating a physically compacted collection.
        private static void MeasureIterationAfterDeletion(Int32 rowCount, String[] namePool, BenchmarkReporter reporter)
        {
            DataTable liteTable = BenchmarkTableFactory.BuildLiteTable(rowCount, namePool);
            DeleteEveryTenthRow(liteTable);
            DataColumn<Int32> liteId = liteTable.Columns.GetColumn<Int32>("Id");
            Int64 liteChecksum = 0;
            Action liteWork = () =>
            {
                Int64 total = 0;
                foreach (DataRow row in liteTable.Rows)
                {
                    total = total + liteId.Get(row.RowIndex);
                }
                liteChecksum = total;
            };
            Double liteMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteWork);
            System.Data.DataTable frameworkTable = BenchmarkTableFactory.BuildFrameworkTable(rowCount, namePool);
            DeleteEveryTenthFrameworkRow(frameworkTable);
            Int64 frameworkChecksum = 0;
            Action frameworkWork = () =>
            {
                Int64 total = 0;
                foreach (System.Data.DataRow row in frameworkTable.Rows)
                {
                    total = total + (Int32)row[0];
                }
                frameworkChecksum = total;
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkWork);
            VerifyChecksums(liteChecksum, frameworkChecksum);
            Int32 survivingRowCount = liteTable.Rows.Count;
            Double liteRate = TimedMeasurement.ToMillionItemsPerSecond(liteMicroseconds, survivingRowCount);
            Double frameworkRate = TimedMeasurement.ToMillionItemsPerSecond(frameworkMicroseconds, survivingRowCount);
            reporter.WriteLine($"  {"Iterate after deletions",-34} ColumnStore.Data {liteRate,10:N1} vs System.Data {frameworkRate,10:N1} million rows/s");
            reporter.Record(new BenchmarkResult("Iterate after deletions", BenchmarkReporter.LiteImplementation, liteRate, "million rows/s"));
            reporter.Record(new BenchmarkResult("Iterate after deletions", BenchmarkReporter.FrameworkImplementation, frameworkRate, "million rows/s"));
        }

        // Deletes every tenth row of the columnar table, walking from the end so the logical positions of the rows
        // still to be deleted do not shift underneath the loop.
        private static void DeleteEveryTenthRow(DataTable table)
        {
            for (Int32 logicalIndex = table.Rows.Count - 1; logicalIndex >= 0; logicalIndex = logicalIndex - DELETION_DIVISOR)
            {
                table.Rows.RemoveAt(logicalIndex);
            }
        }

        // Deletes every tenth row of the framework table, from the end for the same reason - and, incidentally, the
        // cheapest direction for a physically shifting collection.
        private static void DeleteEveryTenthFrameworkRow(System.Data.DataTable table)
        {
            for (Int32 rowIndex = table.Rows.Count - 1; rowIndex >= 0; rowIndex = rowIndex - DELETION_DIVISOR)
            {
                table.Rows.RemoveAt(rowIndex);
            }
        }

        // Confirms both implementations were left holding the same number of rows.
        private static void VerifyRowCounts(Int32 liteRowCount, Int32 frameworkRowCount)
        {
            if (liteRowCount != frameworkRowCount) { throw new InvalidOperationException($"The two implementations were left with different row counts ({liteRowCount} and {frameworkRowCount}); the benchmark is not comparing like with like."); }
        }

        // Confirms both implementations iterated the same surviving rows.
        private static void VerifyChecksums(Int64 liteChecksum, Int64 frameworkChecksum)
        {
            if (liteChecksum != frameworkChecksum) { throw new InvalidOperationException($"The two implementations iterated different rows ({liteChecksum} and {frameworkChecksum}); the benchmark is not comparing like with like."); }
        }
        #endregion
    }
}
