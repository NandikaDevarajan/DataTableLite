///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Measures how many bytes a piece of work allocates on the calling thread, so the "zero boxing on the hot
//   path" requirements (NFR-5, NFR-7 and the acceptance criteria in 01_Requirements section 6) can be asserted
//   directly rather than inferred from timings.
// Assumptions: The measured work runs entirely on the calling thread - GC.GetAllocatedBytesForCurrentThread only sees
//   this thread's allocations, which is exactly what makes the measurement stable in a parallel test run.
// Design Considerations: The delegate is invoked a number of times before measuring. That warm-up matters more than
//   it looks: the first call to a method triggers tiered JIT compilation, and any lazily initialised storage chunk,
//   bitmap word or dictionary bucket allocates on first touch. Without warm-up a genuinely allocation-free loop
//   measures as allocating, and the test becomes flaky rather than informative.
//   The probe returns bytes rather than asserting, so each test can choose its own tolerance: a struct enumerator
//   must allocate exactly zero, while a path that legitimately allocates one array up front is allowed that one
//   allocation and nothing per row.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Tests.Support
{
    /// <summary>
    /// Allocation-delta measurement helper for the zero-boxing tests.
    /// </summary>
    internal static class AllocationProbe
    {
        #region Private Constants
        // Times the work is run before measuring, to force tiered JIT compilation and any first-touch lazy
        // allocation to happen outside the measured window.
        private const Int32 DEFAULT_WARMUP_ITERATIONS = 5;
        #endregion

        #region Public Methods
        /// <summary>
        /// Runs the work once for measurement, after warming it up, and returns the bytes it allocated on this thread.
        /// </summary>
        /// <param name="work">The work to measure. Must be self-contained and repeatable.</param>
        /// <returns>Bytes allocated by the measured invocation.</returns>
        internal static Int64 Measure(Action work)
        {
            return Measure(work, DEFAULT_WARMUP_ITERATIONS);
        }

        /// <summary>
        /// Runs the work once for measurement, after the given number of warm-up runs, and returns the bytes it
        /// allocated on this thread.
        /// </summary>
        /// <param name="work">The work to measure. Must be self-contained and repeatable.</param>
        /// <param name="warmupIterations">Number of unmeasured warm-up runs.</param>
        /// <returns>Bytes allocated by the measured invocation.</returns>
        internal static Int64 Measure(Action work, Int32 warmupIterations)
        {
            if (work == null) { throw new ArgumentNullException(nameof(work)); }
            for (Int32 iteration = 0; iteration < warmupIterations; iteration++)
            {
                work();
            }
            Int64 allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            work();
            Int64 allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            return allocatedAfter - allocatedBefore;
        }
        #endregion
    }
}
