///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Reproduces the column shapes of three real production tables - Audit, Investigation and Attachment - and
//   measures loading them from an IDataReader with no database in the way, so that library cost can be separated from
//   SQL Server cost.
// Assumptions: The source rows are served by System.Data.DataTableReader over a pre-built table, identical for both
//   implementations. String values come from a shared pool so the string payload is the same on both sides and does
//   not swamp the structural difference being measured.
// Design Considerations: WHY THIS EXISTS. A measurement that wraps a Stopwatch around ExecuteReaderAsync plus the row
//   fetch is measuring SQL Server, the network and the TDS parser far more than it is measuring the table. At roughly
//   280 microseconds per row for a 25-column table, well over 99% of the elapsed time is the database. Any difference
//   between two table implementations is then a rounding error hidden inside run-to-run variance of the server. This
//   scenario removes the database entirely.
//   It also prices the loader's per-cell reader access pattern. DataTableLoader calls IsDBNull(i) and then GetXxx(i)
//   for every cell - two calls into the reader per cell - while System.Data.DataTable.Load calls GetValues(Object[])
//   once per ROW. On an 85-column table that is 170 reader calls per row against one, which looked like the obvious
//   suspect for the columnar loader not being decisively faster. MEASURED, IT IS NOT: against DataTableReader the
//   per-cell pattern comes out around 0.6x the cost of the per-row one, because GetValues boxes every column
//   including the nulls and stores through an Object[] with a covariance check, while the per-cell path skips the
//   read entirely for a null cell. The hypothesis is recorded here because it was wrong and the measurement is the
//   reason to believe otherwise.
//   The caveat that remains: DataTableReader is not SqlDataReader. SqlDataReader's per-call overhead is materially
//   higher - each accessor re-validates metadata and sequential-access state - so the two-calls-per-cell pattern may
//   still cost more there. That cannot be measured without a database, and it is the one thing worth measuring in a
//   real environment.
//   The nullable column counts matter and are reproduced faithfully: a nullable column costs a second write per cell
//   (the has-value bit) on the columnar side, and 78 of Investigation's 85 columns are nullable.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Data;

using ColumnStore.Data.SqlServer;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Loads production-shaped wide tables from an in-memory reader, comparing ColumnStore.Data against System.Data.
    /// </summary>
    public sealed class TableShapeLoadBenchmark
    {
        #region Private Constants
        // Distinct strings cycled through every String column. Shared references, so the character payload is
        // identical on both sides and constant regardless of row count.
        private const Int32 NAME_POOL_SIZE = 128;

        // Characters per pooled string. Representative of a short business field rather than a document blob - the
        // point is to measure structure, and a large payload would be identical on both sides anyway.
        private const Int32 STRING_LENGTH = 24;

        // Bytes in a kilobyte, for reporting.
        private const Double BYTES_PER_KILOBYTE = 1024d;

        // Null rate used by the main comparison: one cell in seven of every nullable column is null.
        private const Int32 NULL_EVERY_NTH_CELL = 7;

        // Rows used by the null-rate comparison.
        private const Int32 NULL_RATE_ROW_COUNT = 50000;
        #endregion

        #region Private Members
        // Shared string pool used to populate every String column.
        private readonly String[] namePool;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates the benchmark and its shared string pool.
        /// </summary>
        public TableShapeLoadBenchmark()
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
        /// Runs the shape comparison for every table shape at every requested row count.
        /// </summary>
        /// <param name="rowCounts">Row counts to measure.</param>
        public void Run(List<Int32> rowCounts)
        {
            if (rowCounts == null) { throw new ArgumentNullException(nameof(rowCounts)); }
            BenchmarkReporter reporter = new BenchmarkReporter();
            reporter.WriteBanner("PRODUCTION TABLE SHAPES - loaded from an in-memory reader, no database");
            reporter.WriteLine("Times are table population only. The database, the network and the TDS parser are not in this measurement.");
            List<TableShape> shapes = BuildShapes();
            for (Int32 shapeIndex = 0; shapeIndex < shapes.Count; shapeIndex++)
            {
                TableShape shape = shapes[shapeIndex];
                RunShape(shape, rowCounts, reporter);
            }
            MeasureReaderAccessPattern(shapes, reporter);
            MeasureChunkSizeOnWideShapes(shapes, reporter);
            MeasureNullRate(reporter);
        }
        #endregion

        #region Private Methods
        // Declares the three production shapes, with their column type mix and nullable counts as reported.
        private static List<TableShape> BuildShapes()
        {
            return BuildShapes(NULL_EVERY_NTH_CELL);
        }

        // Declares the three production shapes at a chosen null rate, so the same shapes can be measured with nulls
        // present and with nullable columns that are always populated.
        private static List<TableShape> BuildShapes(Int32 nullEveryNthCell)
        {
            List<TableShape> shapes = new List<TableShape>();
            TableShape audit = new TableShape("Audit", 17, nullEveryNthCell);
            audit.Add(typeof(Int32), 9);
            audit.Add(typeof(String), 8);
            audit.Add(typeof(Boolean), 2);
            audit.Add(typeof(DateTime), 2);
            audit.Add(typeof(Guid), 2);
            audit.Add(typeof(Byte[]), 1);
            shapes.Add(audit);
            TableShape investigation = new TableShape("Investigation", 78, nullEveryNthCell);
            investigation.Add(typeof(String), 38);
            investigation.Add(typeof(Int32), 24);
            investigation.Add(typeof(Boolean), 8);
            investigation.Add(typeof(Decimal), 7);
            investigation.Add(typeof(DateTime), 5);
            investigation.Add(typeof(Guid), 2);
            investigation.Add(typeof(Byte[]), 1);
            shapes.Add(investigation);
            TableShape attachment = new TableShape("Attachment", 59, nullEveryNthCell);
            attachment.Add(typeof(String), 34);
            attachment.Add(typeof(Int32), 15);
            attachment.Add(typeof(Boolean), 9);
            attachment.Add(typeof(DateTime), 2);
            attachment.Add(typeof(Decimal), 2);
            attachment.Add(typeof(Guid), 2);
            attachment.Add(typeof(Int64), 1);
            attachment.Add(typeof(Byte[]), 1);
            shapes.Add(attachment);
            return shapes;
        }

        // Measures one shape at every row count, reporting load time and retained memory for both implementations.
        private void RunShape(TableShape shape, List<Int32> rowCounts, BenchmarkReporter reporter)
        {
            reporter.WriteHeading($"{shape.Name} - {shape.ColumnCount} columns, {shape.NullableColumnCount} nullable");
            reporter.WriteLine("     rows |   System.Data      Lite infer     Lite noinfer |   SysData KB      Lite KB   saved |  us/row SD  us/row Lite");
            for (Int32 index = 0; index < rowCounts.Count; index++)
            {
                Int32 rowCount = rowCounts[index];
                MeasureShapeAtRowCount(shape, rowCount, reporter);
            }
        }

        // Measures one shape at one row count. Three load variants are timed: the framework's own Load, the columnar
        // loader with nullability inference on (which calls GetSchemaTable), and the columnar loader with it off and
        // the schema pre-declared - which is how a production caller who knows their schema should use it.
        private void MeasureShapeAtRowCount(TableShape shape, Int32 rowCount, BenchmarkReporter reporter)
        {
            System.Data.DataTable sourceTable = shape.BuildSourceTable(rowCount, this.namePool);
            DataTableLoader loader = new DataTableLoader();
            Action frameworkWork = () =>
            {
                using (IDataReader reader = sourceTable.CreateDataReader())
                {
                    System.Data.DataTable target = new System.Data.DataTable();
                    target.Load(reader);
                    GC.KeepAlive(target);
                }
            };
            Double frameworkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(frameworkWork);
            Action liteInferWork = () =>
            {
                using (IDataReader reader = sourceTable.CreateDataReader())
                {
                    DataTable target = new DataTable();
                    loader.Load(reader, true, target);
                    GC.KeepAlive(target);
                }
            };
            Double liteInferMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteInferWork);
            Action liteNoInferWork = () =>
            {
                using (IDataReader reader = sourceTable.CreateDataReader())
                {
                    DataTable target = shape.BuildLiteSchema();
                    loader.Load(reader, false, target);
                    GC.KeepAlive(target);
                }
            };
            Double liteNoInferMicroseconds = TimedMeasurement.MicrosecondsPerIteration(liteNoInferWork);
            Int64 frameworkRetained = MeasureFrameworkRetained(sourceTable);
            Int64 liteRetained = MeasureLiteRetained(sourceTable, shape, loader);
            Double savedPercent = frameworkRetained > 0 ? (frameworkRetained - liteRetained) * 100d / frameworkRetained : 0d;
            reporter.WriteLine($"  {rowCount,7:N0} | {frameworkMicroseconds,13:N0} {liteInferMicroseconds,15:N0} {liteNoInferMicroseconds,16:N0} | {frameworkRetained / BYTES_PER_KILOBYTE,12:N0} {liteRetained / BYTES_PER_KILOBYTE,12:N0} {savedPercent,7:N1}% | {frameworkMicroseconds / rowCount,11:N2} {liteNoInferMicroseconds / rowCount,12:N2}");
        }

        // Retained bytes of a framework table loaded from the source, measured on a settled heap.
        private static Int64 MeasureFrameworkRetained(System.Data.DataTable sourceTable)
        {
            Int64 before = SettleHeapAndMeasure();
            System.Data.DataTable target = new System.Data.DataTable();
            using (IDataReader reader = sourceTable.CreateDataReader())
            {
                target.Load(reader);
            }
            Int64 after = SettleHeapAndMeasure();
            GC.KeepAlive(target);
            return after - before;
        }

        // Retained bytes of a columnar table loaded from the same source, measured the same way.
        private static Int64 MeasureLiteRetained(System.Data.DataTable sourceTable, TableShape shape, DataTableLoader loader)
        {
            Int64 before = SettleHeapAndMeasure();
            DataTable target = shape.BuildLiteSchema();
            using (IDataReader reader = sourceTable.CreateDataReader())
            {
                loader.Load(reader, false, target);
            }
            Int64 after = SettleHeapAndMeasure();
            GC.KeepAlive(target);
            return after - before;
        }

        // Prices the loader's per-cell reader access pattern against the framework's per-row one, on identical data.
        // This is the measurement that says how much of the columnar loader's cost is its own work and how much is
        // simply calling the reader twice per cell instead of once per row.
        private void MeasureReaderAccessPattern(List<TableShape> shapes, BenchmarkReporter reporter)
        {
            reporter.WriteHeading("Reader access pattern - the cost of HOW the reader is called, not of storing anything");
            reporter.WriteLine("  Per row: GetValues fills the whole row in one call; IsDBNull+GetXxx is two calls per cell.");
            reporter.WriteLine("     shape |  cols |  GetValues us/row | IsDBNull+Get us/row |  ratio");
            Int32 rowCount = 20000;
            for (Int32 shapeIndex = 0; shapeIndex < shapes.Count; shapeIndex++)
            {
                TableShape shape = shapes[shapeIndex];
                System.Data.DataTable sourceTable = shape.BuildSourceTable(rowCount, this.namePool);
                Int32 fieldCount = shape.ColumnCount;
                Object[] buffer = new Object[fieldCount];
                Action bulkWork = () =>
                {
                    using (IDataReader reader = sourceTable.CreateDataReader())
                    {
                        while (reader.Read())
                        {
                            reader.GetValues(buffer);
                        }
                    }
                };
                Double bulkMicroseconds = TimedMeasurement.MicrosecondsPerIteration(bulkWork);
                Action perCellWork = () =>
                {
                    using (IDataReader reader = sourceTable.CreateDataReader())
                    {
                        while (reader.Read())
                        {
                            for (Int32 ordinal = 0; ordinal < fieldCount; ordinal++)
                            {
                                Boolean isNull = reader.IsDBNull(ordinal);
                                if (isNull == false) { Object ignored = reader.GetValue(ordinal); }
                            }
                        }
                    }
                };
                Double perCellMicroseconds = TimedMeasurement.MicrosecondsPerIteration(perCellWork);
                Double bulkPerRow = bulkMicroseconds / rowCount;
                Double perCellPerRow = perCellMicroseconds / rowCount;
                Double ratio = bulkPerRow > 0 ? perCellPerRow / bulkPerRow : 0d;
                reporter.WriteLine($"  {shape.Name,9} | {fieldCount,5} | {bulkPerRow,17:N3} | {perCellPerRow,19:N3} | {ratio,6:N2}x");
            }
        }

        // Prices the storage chunk size on these wide shapes specifically. A wide table is the worst case for small
        // chunks: a load writes one cell in each of N columns per row, so it touches N separate chunk arrays per row,
        // and at 128 rows per chunk an 85-column, 50,000-row table holds well over thirty thousand of them.
        private void MeasureChunkSizeOnWideShapes(List<TableShape> shapes, BenchmarkReporter reporter)
        {
            reporter.WriteHeading("Chunk size on these shapes - 50,000 rows, schema pre-declared");
            reporter.WriteLine("     shape |  cols | chunk rows |   load us |  retained KB |  chunks total");
            Int32 rowCount = 50000;
            Int32[] chunkRowCounts = new Int32[] { 128, 512, 2048, 8192 };
            DataTableLoader loader = new DataTableLoader();
            for (Int32 shapeIndex = 0; shapeIndex < shapes.Count; shapeIndex++)
            {
                TableShape shape = shapes[shapeIndex];
                System.Data.DataTable sourceTable = shape.BuildSourceTable(rowCount, this.namePool);
                for (Int32 chunkIndex = 0; chunkIndex < chunkRowCounts.Length; chunkIndex++)
                {
                    Int32 chunkRowCount = chunkRowCounts[chunkIndex];
                    Action work = () =>
                    {
                        using (IDataReader reader = sourceTable.CreateDataReader())
                        {
                            DataTable target = shape.BuildLiteSchema(chunkRowCount);
                            loader.Load(reader, false, target);
                            GC.KeepAlive(target);
                        }
                    };
                    Double loadMicroseconds = TimedMeasurement.MicrosecondsPerIteration(work);
                    Int64 retained = MeasureRetainedForChunkSize(sourceTable, shape, loader, chunkRowCount);
                    Double chunksTotal = Math.Ceiling(rowCount / (Double)chunkRowCount) * shape.ColumnCount;
                    reporter.WriteLine($"  {shape.Name,9} | {shape.ColumnCount,5} | {chunkRowCount,10:N0} | {loadMicroseconds,9:N0} | {retained / BYTES_PER_KILOBYTE,12:N0} | {chunksTotal,13:N0}");
                }
            }
        }

        // Retained bytes of one chunk-size arm. Kept in its own method on purpose: the loaded table must become
        // unreachable when the method returns, so the NEXT arm's baseline reading is not taken while this arm's table
        // is still alive. Measuring it inline with a reused local produced negative deltas, because the previous
        // arm's table was still reachable at the baseline reading and was collected before the final one.
        private static Int64 MeasureRetainedForChunkSize(System.Data.DataTable sourceTable, TableShape shape, DataTableLoader loader, Int32 chunkRowCount)
        {
            Int64 before = SettleHeapAndMeasure();
            DataTable target = shape.BuildLiteSchema(chunkRowCount);
            using (IDataReader reader = sourceTable.CreateDataReader())
            {
                loader.Load(reader, false, target);
            }
            Int64 after = SettleHeapAndMeasure();
            GC.KeepAlive(target);
            return after - before;
        }

        // Prices what the is-null bitmap sense actually buys. Null tracking sets a bit only when a cell IS null, so a
        // nullable column that happens always to be populated never grows its bitmap at all: no words allocated, and
        // no store on the value path. A column that really does carry nulls allocates the words either way, so the
        // saving is a property of the DATA, not of the schema - which is why both rates are measured.
        private void MeasureNullRate(BenchmarkReporter reporter)
        {
            reporter.WriteHeading($"Null rate - what the is-null bitmap saves, {NULL_RATE_ROW_COUNT:N0} rows, schema pre-declared");
            reporter.WriteLine("     shape |  nullable cols |          null rate |   load us |  retained KB");
            DataTableLoader loader = new DataTableLoader();
            List<TableShape> withNulls = BuildShapes(NULL_EVERY_NTH_CELL);
            List<TableShape> withoutNulls = BuildShapes(TableShape.NO_NULLS);
            for (Int32 shapeIndex = 0; shapeIndex < withNulls.Count; shapeIndex++)
            {
                MeasureOneNullRate(withNulls[shapeIndex], "1 cell in 7 null", loader, reporter);
                MeasureOneNullRate(withoutNulls[shapeIndex], "no nulls at all", loader, reporter);
            }
        }

        // Measures load time and retained memory for one shape at one null rate.
        private void MeasureOneNullRate(TableShape shape, String nullRateLabel, DataTableLoader loader, BenchmarkReporter reporter)
        {
            System.Data.DataTable sourceTable = shape.BuildSourceTable(NULL_RATE_ROW_COUNT, this.namePool);
            Action work = () =>
            {
                using (IDataReader reader = sourceTable.CreateDataReader())
                {
                    DataTable target = shape.BuildLiteSchema();
                    loader.Load(reader, false, target);
                    GC.KeepAlive(target);
                }
            };
            Double loadMicroseconds = TimedMeasurement.MicrosecondsPerIteration(work);
            Int64 retained = MeasureLiteRetained(sourceTable, shape, loader);
            reporter.WriteLine($"  {shape.Name,9} | {shape.NullableColumnCount,14} | {nullRateLabel,18} | {loadMicroseconds,9:N0} | {retained / BYTES_PER_KILOBYTE,12:N0}");
        }

        // Forces the heap into a settled state and returns the bytes it holds.
        private static Int64 SettleHeapAndMeasure()
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            return GC.GetTotalMemory(true);
        }
        #endregion
    }
}
