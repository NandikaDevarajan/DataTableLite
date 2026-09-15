///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Prices runtime code generation against the shipped DataTableLoader: is one compiled delegate per LOAD
//   faster than one pre-bound delegate per COLUMN, and if so, by enough to pay for Expression.Compile?
// Assumptions: Both arms read the same BufferedColumnReader snapshot, so the reader contributes identically to each
//   and contributes as little as a reader can - see that class's header for why that matters.
// Design Considerations: The two costs are reported separately on purpose, because they answer different questions.
//   Per-row throughput says whether the TECHNIQUE is better. One-off setup cost says whether it is better in a
//   PROGRAM, and the break-even row count is where those two meet. Reporting only the first would recommend a change
//   that loses on every load a normal application actually performs; reporting only the second would hide a real
//   throughput win that a cache could unlock. Setup is therefore hoisted out of the timed region and measured on its
//   own, and the break-even is computed rather than left to the reader to work out.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;

using ColumnStore.Data.SqlServer;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Compares the shipped loader against <see cref="CompiledRowLoaderPrototype"/>.
    /// </summary>
    public sealed class CompiledLoaderBenchmark
    {
        #region Private Constants
        // How many complete measurements to take per arm; the fastest is kept.
        private const Int32 ATTEMPTS = 5;

        // How many compiles to average when pricing Expression.Compile.
        private const Int32 COMPILE_SAMPLES = 20;

        // Length of the pooled strings the String columns are filled from.
        private const Int32 STRING_LENGTH = 24;

        // How many distinct strings the String columns cycle through.
        private const Int32 NAME_POOL_SIZE = 64;

        // One cell in this many is null, in every nullable column.
        private const Int32 NULL_EVERY_NTH_CELL = 7;

        // Microseconds in a millisecond, for reporting.
        private const Double MICROSECONDS_PER_MILLISECOND = 1000d;
        #endregion

        #region Private Members
        // Shared string pool, so String columns measure the loader rather than string allocation.
        private readonly String[] namePool;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates the benchmark and its shared string pool.
        /// </summary>
        public CompiledLoaderBenchmark()
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
        /// Runs the comparison over the three production shapes at each of the given row counts.
        /// </summary>
        /// <param name="rowCounts">Row counts to measure.</param>
        /// <param name="reporter">Where the results are written.</param>
        /// <exception cref="ArgumentNullException">The row counts or the reporter are null.</exception>
        public void Run(IReadOnlyList<Int32> rowCounts, BenchmarkReporter reporter)
        {
            if (rowCounts == null) { throw new ArgumentNullException(nameof(rowCounts)); }
            if (reporter == null) { throw new ArgumentNullException(nameof(reporter)); }
            reporter.WriteHeading("Runtime code generation - one compiled delegate per load vs one bound delegate per column");
            reporter.WriteLine("Row loop only; the setup each arm needs is hoisted out and priced separately below.");
            reporter.WriteLine("        shape |   rows |   bound delegates us |   compiled loader us |   change");
            List<TableShape> shapes = BuildShapes();
            for (Int32 shapeIndex = 0; shapeIndex < shapes.Count; shapeIndex++)
            {
                TableShape shape = shapes[shapeIndex];
                for (Int32 rowCountIndex = 0; rowCountIndex < rowCounts.Count; rowCountIndex++)
                {
                    MeasureThroughput(shape, rowCounts[rowCountIndex], reporter);
                }
            }
            ReportSetupCost(shapes, reporter);
        }
        #endregion

        #region Private Methods
        // Times both arms over the same snapshot, with every allocation and every binding built before the clock
        // starts. Rows.Clear between iterations keeps the bindings valid while emptying the table.
        private void MeasureThroughput(TableShape shape, Int32 rowCount, BenchmarkReporter reporter)
        {
            System.Data.DataTable sourceTable = shape.BuildSourceTable(rowCount, this.namePool);
            BufferedColumnReader reader = new BufferedColumnReader(sourceTable);
            Double boundMicroseconds = MeasureBound(shape, reader);
            Double compiledMicroseconds = MeasureCompiled(shape, reader);
            Double change = (compiledMicroseconds / boundMicroseconds - 1d) * 100d;
            reporter.WriteLine($"  {shape.Name,11} | {rowCount,6:N0} | {boundMicroseconds,20:N0} | {compiledMicroseconds,20:N0} | {change,7:+0.0;-0.0}%");
        }

        // Times the shipped loader. Its bindings are rebuilt on every Load by design, but that cost is microseconds
        // and is priced separately, so the timed region is the row loop alone.
        private Double MeasureBound(TableShape shape, BufferedColumnReader reader)
        {
            DataTable target = shape.BuildLiteSchema();
            DataTableLoader loader = new DataTableLoader();
            loader.Load(reader, false, target);
            Action setup = () =>
            {
                reader.Reset();
                target.Rows.Clear();
            };
            Action work = () =>
            {
                loader.Load(reader, false, target);
            };
            return TimedMeasurement.BestMicrosecondsPerIteration(setup, work, ATTEMPTS);
        }

        // Times the compiled loader, with the compile hoisted out of the measurement - which is the optimistic case,
        // and the only one a schema-keyed cache could ever deliver.
        private Double MeasureCompiled(TableShape shape, BufferedColumnReader reader)
        {
            DataTable target = shape.BuildLiteSchema();
            Func<IDataReader, DataTable, Int32> compiled = CompiledRowLoaderPrototype.Build(reader, target, true);
            reader.Reset();
            compiled(reader, target);
            Action setup = () =>
            {
                reader.Reset();
                target.Rows.Clear();
            };
            Action work = () =>
            {
                compiled(reader, target);
            };
            return TimedMeasurement.BestMicrosecondsPerIteration(setup, work, ATTEMPTS);
        }

        // Prices what each arm must do before its first row, and turns that into the row count at which the compiled
        // arm starts to pay for itself when the compile is NOT cached.
        private void ReportSetupCost(List<TableShape> shapes, BenchmarkReporter reporter)
        {
            reporter.WriteHeading("What each arm costs before its first row");
            reporter.WriteLine("        shape | cols |   bind columns us |   compile loader us | break-even rows, uncached");
            for (Int32 shapeIndex = 0; shapeIndex < shapes.Count; shapeIndex++)
            {
                TableShape shape = shapes[shapeIndex];
                System.Data.DataTable sourceTable = shape.BuildSourceTable(1, this.namePool);
                BufferedColumnReader reader = new BufferedColumnReader(sourceTable);
                Double bindMicroseconds = MeasureBindCost(shape, reader);
                Double compileMicroseconds = MeasureCompileCost(shape, reader);
                Double breakEvenRows = BreakEvenRows(shape, compileMicroseconds - bindMicroseconds);
                reporter.WriteLine($"  {shape.Name,11} | {shape.ColumnCount,4} | {bindMicroseconds,17:N1} | {compileMicroseconds,19:N1} | {breakEvenRows,25:N0}");
            }
            reporter.WriteLine("");
            reporter.WriteLine("Break-even is where the per-row saving repays Expression.Compile within a SINGLE load. Below it the");
            reporter.WriteLine("compiled arm loses outright; above it it wins. A schema-keyed cache would move the cost off every load");
            reporter.WriteLine("after the first, at the price of a bounded cache, a non-codegen fallback, and a slower first load.");
        }

        // Times building the per-column bindings, by timing a whole load of a one-row reader - which is that build
        // plus one row - and accepting the one row as noise against the thing being measured.
        private Double MeasureBindCost(TableShape shape, BufferedColumnReader reader)
        {
            DataTableLoader loader = new DataTableLoader();
            Action work = () =>
            {
                reader.Reset();
                DataTable target = shape.BuildLiteSchema();
                loader.Load(reader, false, target);
                GC.KeepAlive(target);
            };
            Double withSchema = TimedMeasurement.BestMicrosecondsPerIteration(null, work, ATTEMPTS);
            Action schemaOnly = () =>
            {
                GC.KeepAlive(shape.BuildLiteSchema());
            };
            Double schemaCost = TimedMeasurement.BestMicrosecondsPerIteration(null, schemaOnly, ATTEMPTS);
            Double difference = withSchema - schemaCost;
            return difference > 0d ? difference : 0d;
        }

        // Times Expression.Compile. Deliberately a plain repetition count rather than the adaptive timer: each
        // iteration is milliseconds, and every one must compile a brand new dynamic method to be honest.
        private Double MeasureCompileCost(TableShape shape, BufferedColumnReader reader)
        {
            DataTable warmUpTarget = shape.BuildLiteSchema();
            CompiledRowLoaderPrototype.Build(reader, warmUpTarget, true);
            Stopwatch clock = Stopwatch.StartNew();
            for (Int32 sample = 0; sample < COMPILE_SAMPLES; sample++)
            {
                DataTable target = shape.BuildLiteSchema();
                GC.KeepAlive(CompiledRowLoaderPrototype.Build(reader, target, true));
            }
            clock.Stop();
            return clock.Elapsed.TotalMilliseconds * MICROSECONDS_PER_MILLISECOND / COMPILE_SAMPLES;
        }

        // Converts an extra setup cost into the number of rows needed to repay it, using the per-row saving this
        // shape showed at the largest row count measured.
        private Double BreakEvenRows(TableShape shape, Double extraSetupMicroseconds)
        {
            const Int32 REFERENCE_ROWS = 50000;
            System.Data.DataTable sourceTable = shape.BuildSourceTable(REFERENCE_ROWS, this.namePool);
            BufferedColumnReader reader = new BufferedColumnReader(sourceTable);
            Double bound = MeasureBound(shape, reader);
            Double compiled = MeasureCompiled(shape, reader);
            Double savingPerRow = (bound - compiled) / REFERENCE_ROWS;
            if (savingPerRow <= 0d) { return Double.PositiveInfinity; }
            return extraSetupMicroseconds / savingPerRow;
        }

        // The three production shapes, at the same null rate the rest of the shape analysis uses.
        private static List<TableShape> BuildShapes()
        {
            List<TableShape> shapes = new List<TableShape>();
            TableShape audit = new TableShape("Audit", 17, NULL_EVERY_NTH_CELL);
            audit.Add(typeof(Int32), 9);
            audit.Add(typeof(String), 8);
            audit.Add(typeof(Boolean), 2);
            audit.Add(typeof(DateTime), 2);
            audit.Add(typeof(Guid), 2);
            audit.Add(typeof(Int64), 1);
            shapes.Add(audit);
            TableShape investigation = new TableShape("Investigation", 78, NULL_EVERY_NTH_CELL);
            investigation.Add(typeof(String), 38);
            investigation.Add(typeof(Int32), 24);
            investigation.Add(typeof(Boolean), 8);
            investigation.Add(typeof(Decimal), 7);
            investigation.Add(typeof(DateTime), 5);
            investigation.Add(typeof(Guid), 2);
            investigation.Add(typeof(Int64), 1);
            shapes.Add(investigation);
            TableShape attachment = new TableShape("Attachment", 59, NULL_EVERY_NTH_CELL);
            attachment.Add(typeof(String), 34);
            attachment.Add(typeof(Int32), 15);
            attachment.Add(typeof(Boolean), 9);
            attachment.Add(typeof(DateTime), 2);
            attachment.Add(typeof(Decimal), 2);
            attachment.Add(typeof(Guid), 2);
            attachment.Add(typeof(Int64), 2);
            shapes.Add(attachment);
            return shapes;
        }
        #endregion
    }
}
