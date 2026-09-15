///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Times a piece of work by repeating it until the accumulated duration is long enough to mean something, and
//   reports the cost of one iteration.
// Assumptions: The work is repeatable and self-contained. Where an iteration mutates state it cannot re-use, a setup
//   delegate rebuilds that state and is excluded from the timing.
// Design Considerations: A SINGLE Stopwatch READING IS WORTHLESS AT SMALL SIZES. Stopwatch resolution on Windows is
//   around 100 nanoseconds, and reading a hundred-row table takes less than a microsecond - so a one-shot measurement
//   at that size is dominated by timer granularity and by whichever branch the CPU happened to mispredict. Repeating
//   until a target duration has accumulated turns that into a stable per-iteration figure.
//   Setup is timed separately from work, because the deletion and insert scenarios have to rebuild their table for
//   every iteration and including that rebuild would measure the wrong thing entirely.
//   Three guards bound a run: the target work duration, a wall-clock ceiling (so a scenario whose setup dwarfs its
//   work cannot run for minutes), and an iteration ceiling (so a nanosecond-scale operation does not spin tens of
//   millions of times). Whichever is reached first ends the measurement.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Diagnostics;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Repetition-based timing helper, so small workloads produce numbers that are not timer noise.
    /// </summary>
    public static class TimedMeasurement
    {
        #region Private Constants
        // Maximum unmeasured iterations run before timing starts, to force tiered JIT recompilation and first-touch
        // allocation to happen outside the measured window.
        private const Int32 MAXIMUM_WARMUP_ITERATIONS = 5;

        // Wall-clock budget for the warm-up. Cheap work gets the full iteration count; work that already takes
        // longer than this on its first run gets one warm-up pass and no more, because a scenario measured in
        // seconds is past the point where tiered recompilation matters and five free passes would cost a minute.
        private const Double WARMUP_BUDGET_MILLISECONDS = 100d;

        // Accumulated work duration a measurement aims for. Long enough that timer granularity is irrelevant, short
        // enough that a full sweep of row counts stays interactive.
        private const Double TARGET_WORK_MILLISECONDS = 250d;

        // Wall-clock ceiling for one measurement, including the setup that timing excludes. Stops a scenario whose
        // per-iteration setup costs far more than its work from running for minutes.
        private const Double MAXIMUM_WALL_CLOCK_MILLISECONDS = 3000d;

        // Iteration ceiling. Keeps a sub-microsecond operation from spinning tens of millions of times to fill the
        // target duration.
        private const Int64 MAXIMUM_ITERATIONS = 2_000_000;

        // Microseconds in a millisecond, for converting the accumulated duration.
        private const Double MICROSECONDS_PER_MILLISECOND = 1000d;
        #endregion

        #region Public Methods
        /// <summary>
        /// Times repeated invocations of <paramref name="work"/> and returns the cost of one iteration in
        /// microseconds.
        /// </summary>
        /// <param name="work">The work to time. Must be repeatable.</param>
        /// <returns>Microseconds per iteration.</returns>
        /// <exception cref="ArgumentNullException">The work is null.</exception>
        public static Double MicrosecondsPerIteration(Action work)
        {
            return MicrosecondsPerIteration(null, work);
        }

        /// <summary>
        /// Times repeated invocations of <paramref name="work"/>, running <paramref name="setup"/> before each one
        /// and excluding it from the timing. Use this when an iteration destroys state it cannot re-use.
        /// </summary>
        /// <param name="setup">Per-iteration setup, excluded from the timing. May be null.</param>
        /// <param name="work">The work to time.</param>
        /// <returns>Microseconds per iteration.</returns>
        /// <exception cref="ArgumentNullException">The work is null.</exception>
        public static Double MicrosecondsPerIteration(Action setup, Action work)
        {
            if (work == null) { throw new ArgumentNullException(nameof(work)); }
            RunWarmup(setup, work);
            Stopwatch workClock = new Stopwatch();
            Stopwatch wallClock = Stopwatch.StartNew();
            Int64 iterations = 0;
            while (ShouldContinue(workClock, wallClock, iterations))
            {
                if (setup != null) { setup(); }
                workClock.Start();
                work();
                workClock.Stop();
                iterations = iterations + 1;
            }
            wallClock.Stop();
            if (iterations == 0) { return 0d; }
            Double totalMicroseconds = workClock.Elapsed.TotalMilliseconds * MICROSECONDS_PER_MILLISECOND;
            return totalMicroseconds / iterations;
        }

        /// <summary>
        /// Runs the whole measurement several times and returns the FASTEST result. Use this where several
        /// near-identical variants are being compared against each other and a single disturbed measurement would
        /// invert the comparison: tiered recompilation finishing mid-run, a background GC, or another core stealing
        /// the frequency budget all inflate a measurement but none of them can deflate one, so the minimum is the
        /// closest estimate of the work's real cost.
        /// </summary>
        /// <param name="setup">Per-iteration setup, excluded from the timing. May be null.</param>
        /// <param name="work">The work to time.</param>
        /// <param name="attempts">How many complete measurements to take. Must be positive.</param>
        /// <returns>The lowest microseconds-per-iteration seen.</returns>
        /// <exception cref="ArgumentNullException">The work is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The attempt count is not positive.</exception>
        public static Double BestMicrosecondsPerIteration(Action setup, Action work, Int32 attempts)
        {
            if (work == null) { throw new ArgumentNullException(nameof(work)); }
            if (attempts <= 0) { throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "At least one attempt is required."); }
            Double best = Double.MaxValue;
            for (Int32 attempt = 0; attempt < attempts; attempt++)
            {
                Double measured = MicrosecondsPerIteration(setup, work);
                if (measured > 0d && measured < best) { best = measured; }
            }
            if (best == Double.MaxValue) { return 0d; }
            return best;
        }

        /// <summary>
        /// Converts a per-iteration cost in microseconds into throughput in millions of items per second, for a given
        /// number of items processed per iteration.
        /// </summary>
        /// <param name="microsecondsPerIteration">Cost of one iteration.</param>
        /// <param name="itemsPerIteration">Items processed by one iteration.</param>
        /// <returns>Millions of items per second, or zero when the cost is not positive.</returns>
        public static Double ToMillionItemsPerSecond(Double microsecondsPerIteration, Int64 itemsPerIteration)
        {
            if (microsecondsPerIteration <= 0d) { return 0d; }
            // items per microsecond is already items per million-seconds-worth, so the figure needs no further scaling.
            return itemsPerIteration / microsecondsPerIteration;
        }
        #endregion

        #region Private Methods
        // Runs the work a few times without timing it, so the measured iterations are not paying first-call costs.
        // The pass count adapts to how expensive the work turns out to be: always at least one, then more only while
        // the warm-up budget lasts.
        private static void RunWarmup(Action setup, Action work)
        {
            Stopwatch warmupClock = Stopwatch.StartNew();
            Int32 iteration = 0;
            while (iteration < MAXIMUM_WARMUP_ITERATIONS)
            {
                if (setup != null) { setup(); }
                work();
                iteration = iteration + 1;
                if (warmupClock.Elapsed.TotalMilliseconds >= WARMUP_BUDGET_MILLISECONDS) { break; }
            }
            warmupClock.Stop();
        }

        // True while none of the three budgets - accumulated work time, wall-clock time, iteration count - has been
        // exhausted.
        private static Boolean ShouldContinue(Stopwatch workClock, Stopwatch wallClock, Int64 iterations)
        {
            if (iterations >= MAXIMUM_ITERATIONS) { return false; }
            if (workClock.Elapsed.TotalMilliseconds >= TARGET_WORK_MILLISECONDS) { return false; }
            if (wallClock.Elapsed.TotalMilliseconds >= MAXIMUM_WALL_CLOCK_MILLISECONDS) { return false; }
            return true;
        }
        #endregion
    }
}
