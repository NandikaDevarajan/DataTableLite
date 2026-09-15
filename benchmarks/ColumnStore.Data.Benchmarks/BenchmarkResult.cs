///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: One measured number, with enough context around it to be reported and compared: what was measured, which
//   implementation produced it, and in what unit.
// Assumptions: A result is produced once and never mutated - the reporter only reads.
// Design Considerations: Kept as a small immutable class rather than a tuple so the reporter can lay results out in
//   columns and pair a "lite" result with its "framework" counterpart by scenario name, which is the comparison the
//   whole benchmark exists to show. NFR-4 explicitly says the memory numbers are to be validated empirically and not
//   treated as a contractual figure, so nothing here asserts or fails - it reports.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// A single measurement produced by one benchmark scenario against one implementation.
    /// </summary>
    public sealed class BenchmarkResult
    {
        #region Private Members
        // What was measured, e.g. "Retained memory" or "Sequential read".
        private readonly String scenario;

        // Which implementation produced the number.
        private readonly String implementation;

        // The measured value, in the unit named by unitName.
        private readonly Double value;

        // The unit the value is expressed in, e.g. "MB" or "million cells/s".
        private readonly String unitName;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a result.
        /// </summary>
        /// <param name="scenario">What was measured.</param>
        /// <param name="implementation">Which implementation produced it.</param>
        /// <param name="value">The measured value.</param>
        /// <param name="unitName">The unit the value is expressed in.</param>
        public BenchmarkResult(String scenario, String implementation, Double value, String unitName)
        {
            if (scenario == null) { throw new ArgumentNullException(nameof(scenario)); }
            if (implementation == null) { throw new ArgumentNullException(nameof(implementation)); }
            if (unitName == null) { throw new ArgumentNullException(nameof(unitName)); }
            this.scenario = scenario;
            this.implementation = implementation;
            this.value = value;
            this.unitName = unitName;
        }
        #endregion

        #region Public Properties
        /// <summary>What was measured.</summary>
        public String Scenario
        {
            get { return this.scenario; }
        }

        /// <summary>Which implementation produced the number.</summary>
        public String Implementation
        {
            get { return this.implementation; }
        }

        /// <summary>The measured value.</summary>
        public Double Value
        {
            get { return this.value; }
        }

        /// <summary>The unit the value is expressed in.</summary>
        public String UnitName
        {
            get { return this.unitName; }
        }
        #endregion
    }
}
