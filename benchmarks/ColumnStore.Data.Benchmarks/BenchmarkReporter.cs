///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Prints benchmark results as a readable console table, pairing each scenario's column-oriented result with
//   its row-oriented counterpart and showing the ratio between them.
// Assumptions: Results arrive with exactly two implementations per scenario, in the order they were measured.
// Design Considerations: The ratio column is the point of the whole report. An absolute megabyte figure means little
//   on its own - it depends on the machine, the runtime version and the GC mode - whereas "uses a third of the
//   memory" and "reads eleven times faster" are the claims that survive a change of machine, which is exactly the
//   framing NFR-4 asks for when it says the memory target is to be validated empirically rather than treated as a
//   contractual number.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Formats and prints <see cref="BenchmarkResult"/> values.
    /// </summary>
    public sealed class BenchmarkReporter
    {
        #region Private Constants
        // Width of the scenario column, wide enough for the longest scenario name in the suite.
        private const Int32 SCENARIO_COLUMN_WIDTH = 40;

        // Width of each measured-value column.
        private const Int32 VALUE_COLUMN_WIDTH = 16;

        // Width of the rule printed above and below a banner.
        private const Int32 BANNER_WIDTH = 100;

        // Suffix that marks a unit as a rate, where a larger number is better. Every other unit in the suite is a
        // cost - megabytes retained, microseconds per operation - where a smaller number is better, and the ratio
        // column has to be inverted accordingly.
        private const String RATE_UNIT_SUFFIX = "/s";
        #endregion

        #region Private Members
        // Results in the order they were recorded.
        private readonly List<BenchmarkResult> results;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates an empty reporter.
        /// </summary>
        public BenchmarkReporter()
        {
            this.results = new List<BenchmarkResult>();
        }
        #endregion

        #region Public Properties
        /// <summary>
        /// Implementation name the benchmarks record for the column-oriented table. Results are paired by scenario
        /// and implementation rather than by recording order, so a scenario that records its two measurements in a
        /// different order - as the memory benchmark does, taking both of one table's numbers before the other's -
        /// still lines up correctly.
        /// </summary>
        public static String LiteImplementation
        {
            get { return "ColumnStore.Data"; }
        }

        /// <summary>Implementation name the benchmarks record for the framework table.</summary>
        public static String FrameworkImplementation
        {
            get { return "System.Data"; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Records one measurement.
        /// </summary>
        /// <param name="result">The measurement to record.</param>
        /// <exception cref="ArgumentNullException">The result is null.</exception>
        public void Record(BenchmarkResult result)
        {
            if (result == null) { throw new ArgumentNullException(nameof(result)); }
            this.results.Add(result);
        }

        /// <summary>
        /// Prints a prominent banner, used to separate one row count's whole report from the next.
        /// </summary>
        /// <param name="title">The banner text.</param>
        public void WriteBanner(String title)
        {
            String rule = new String('=', BANNER_WIDTH);
            Console.WriteLine();
            Console.WriteLine(rule);
            Console.WriteLine(title);
            Console.WriteLine(rule);
        }

        /// <summary>
        /// Prints a section heading.
        /// </summary>
        /// <param name="title">The heading text.</param>
        public void WriteHeading(String title)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            String underline = new String('=', title.Length);
            Console.WriteLine(underline);
        }

        /// <summary>
        /// Prints one line of free-form context, such as the row count a run used.
        /// </summary>
        /// <param name="line">The text to print.</param>
        public void WriteLine(String line)
        {
            Console.WriteLine(line);
        }

        /// <summary>
        /// Prints every recorded result, grouped by scenario, with the ratio between the two implementations.
        /// </summary>
        public void WriteReport()
        {
            WriteHeading("Summary");
            WriteReportHeader();
            List<String> reportedScenarios = new List<String>();
            for (Int32 resultIndex = 0; resultIndex < this.results.Count; resultIndex++)
            {
                String scenario = this.results[resultIndex].Scenario;
                Boolean alreadyReported = reportedScenarios.Contains(scenario);
                if (alreadyReported) { continue; }
                reportedScenarios.Add(scenario);
                BenchmarkResult liteResult = FindResult(scenario, LiteImplementation);
                BenchmarkResult frameworkResult = FindResult(scenario, FrameworkImplementation);
                if (liteResult == null || frameworkResult == null) { continue; }
                WriteReportRow(liteResult, frameworkResult);
            }
        }
        #endregion

        #region Private Methods
        // Locates one scenario's measurement for one implementation, or null when it was not recorded.
        private BenchmarkResult FindResult(String scenario, String implementation)
        {
            for (Int32 resultIndex = 0; resultIndex < this.results.Count; resultIndex++)
            {
                BenchmarkResult candidate = this.results[resultIndex];
                Boolean matches = candidate.Scenario == scenario && candidate.Implementation == implementation;
                if (matches) { return candidate; }
            }
            return null;
        }

        // Prints the column headings and a rule beneath them.
        private static void WriteReportHeader()
        {
            String scenarioHeading = "Scenario".PadRight(SCENARIO_COLUMN_WIDTH);
            String firstHeading = "ColumnStore.Data".PadLeft(VALUE_COLUMN_WIDTH);
            String secondHeading = "System.Data".PadLeft(VALUE_COLUMN_WIDTH);
            String ratioHeading = "Ratio".PadLeft(12);
            Console.WriteLine($"{scenarioHeading}{firstHeading}{secondHeading}{ratioHeading}  Unit");
            String rule = new String('-', SCENARIO_COLUMN_WIDTH + VALUE_COLUMN_WIDTH + VALUE_COLUMN_WIDTH + 12 + 24);
            Console.WriteLine(rule);
        }

        // Prints one scenario's pair of results and the ratio between them. The ratio is always expressed as the
        // improvement factor in the direction that matters for that scenario: lower-is-better quantities like memory
        // are reported as "how many times less", higher-is-better ones as "how many times more".
        private static void WriteReportRow(BenchmarkResult first, BenchmarkResult second)
        {
            String scenarioText = first.Scenario.PadRight(SCENARIO_COLUMN_WIDTH);
            String firstText = FormatValue(first.Value).PadLeft(VALUE_COLUMN_WIDTH);
            String secondText = FormatValue(second.Value).PadLeft(VALUE_COLUMN_WIDTH);
            String ratioText = FormatRatio(first, second).PadLeft(12);
            Console.WriteLine($"{scenarioText}{firstText}{secondText}{ratioText}  {first.UnitName}");
        }

        // Formats a measured value with a fixed number of decimals, so columns line up and small differences stay
        // visible.
        private static String FormatValue(Double value)
        {
            return value.ToString("N2", CultureInfo.InvariantCulture);
        }

        // Works out the improvement factor between the two implementations. Which way round that is depends on
        // whether the unit is a cost (megabytes, microseconds) or a rate (throughput), so the unit name decides: a
        // unit ending in "/s" is a rate, everything else is a cost.
        private static String FormatRatio(BenchmarkResult first, BenchmarkResult second)
        {
            Boolean isRate = first.UnitName.EndsWith(RATE_UNIT_SUFFIX, StringComparison.Ordinal);
            Double numerator = isRate ? first.Value : second.Value;
            Double denominator = isRate ? second.Value : first.Value;
            if (denominator <= 0) { return "n/a"; }
            Double ratio = numerator / denominator;
            String formattedRatio = ratio.ToString("N2", CultureInfo.InvariantCulture);
            return formattedRatio + "x";
        }
        #endregion
    }
}
