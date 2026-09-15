///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Decides how many rows one storage chunk holds. Offers two answers: the DEFAULT row count storage uses when
//   it is not told otherwise, and the LOH-SAFE row count - "given an element of this size, what power-of-two row
//   count keeps a chunk in the 16 KB - 64 KB band and safely below the Large Object Heap threshold?"
// Assumptions: Runs on a 32-bit or 64-bit CLR where a reference occupies IntPtr.Size bytes. Element size is taken from
//   Unsafe.SizeOf<T>(), which reports the size of T as stored in an array: the struct's own size for value types and
//   the reference width for reference types (the referenced object's payload lives elsewhere on the heap and is not
//   part of the column's array).
// Design Considerations: THE DEFAULT IS A FIXED 128 ROWS PER CHUNK, WHICH IS A DELIBERATE DEVIATION FROM NFR-3.
//   NFR-3 asks for a 16 KB - 64 KB chunk, and LohSafeRowCount<T>() still computes exactly that; the default no longer
//   uses it. The trade being made:
//     * WON: a small table no longer pays a full 16-64 KB chunk per column just to hold a handful of rows. A
//       hundred-row, ten-column table costs kilobytes rather than ~290 KB, which removes the small-table memory
//       penalty measured in Spec/05_DesignDecisions.md section 4.3.
//     * LOST: a large table now holds thousands of small arrays per column instead of dozens of large ones - 7,813
//       chunks per column at a million rows instead of 122 - which adds per-chunk object overhead, scatters a
//       column's data across the heap, and costs sequential-scan locality and GC survivor-copying work.
//   The number is one constant, and DataColumnCollection.Add<T>(name, allowNull, chunkRowCount) lets any individual
//   column override it, so the choice is easy to revisit per column or globally. Both settings are measured side by
//   side by the ChunkSizeBenchmark, whose output is the evidence for whichever value is finally chosen.
//   The row count is always a power of two, whichever route produced it, so TypedColumnStorage can turn a row index
//   into (chunkIndex, indexInChunk) with a shift and a mask rather than a division and a modulo - div/mod on the hot
//   path of every single cell access is measurable, shift/mask is not.
//   In the LOH-safe rule the upper bound is exclusive (a chunk is strictly under 64 KB) which, combined with rounding
//   the row count down to a power of two, guarantees a chunk is always over 32 KB as well - so the 16 KB lower bound
//   of NFR-3 is met without a second adjustment pass. See 03_Design.md section 1.2 and 01_Requirements.md
//   NFR-2/NFR-3.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Runtime.CompilerServices;

namespace ColumnStore.Data.Storage
{
    /// <summary>
    /// Calculates the number of rows a single <see cref="TypedColumnStorage{T}"/> chunk should hold.
    /// </summary>
    public static class ChunkSizing
    {
        #region Private Constants
        // Rows per chunk that storage uses when no explicit size is supplied. A power of two, as every chunk row
        // count must be. See this file's header for the trade this fixed value makes against NFR-3.
        private const Int32 DEFAULT_CHUNK_ROWS = 128;

        // Exclusive upper bound on the byte size of one chunk in the LOH-safe rule. 64 KB is the top of the band
        // NFR-3 asks for, and staying strictly below it keeps a chunk at roughly three quarters of the ~85,000 byte
        // LOH threshold even before the power-of-two rounding pulls it further down.
        private const Int32 MAXIMUM_CHUNK_BYTES = 64 * 1024;

        // The LOH threshold the LOH-safe rule must never reach. Not used as an input to the arithmetic - the 64 KB
        // cap already guarantees it - but asserted against, so a future change to the band cannot silently push
        // chunks onto the Large Object Heap.
        private const Int32 LOH_THRESHOLD_BYTES = 85000;

        // Smallest row count a chunk may have. Reached only by element types so large that a single element already
        // exceeds the 64 KB band, where no row count can satisfy the band and one row per chunk is the best available
        // answer.
        private const Int32 MINIMUM_CHUNK_ROWS = 1;
        #endregion

        #region Public Properties
        /// <summary>
        /// Rows per chunk that <see cref="TypedColumnStorage{T}"/> uses when constructed without an explicit size.
        /// Always a power of two.
        /// </summary>
        public static Int32 DefaultRowCount
        {
            get { return DEFAULT_CHUNK_ROWS; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Returns the number of rows per chunk to use for a column of <typeparamref name="T"/> when the caller has
        /// no particular access pattern in mind. This is <see cref="DefaultRowCount"/> - a fixed 128 rows,
        /// independent of element size. For the 16-64 KB, Large-Object-Heap-avoiding figure that NFR-3 describes,
        /// use <see cref="LohSafeRowCount{T}"/>.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <returns>A power-of-two row count, at least 1.</returns>
        public static Int32 Recommended<T>()
        {
            return DEFAULT_CHUNK_ROWS;
        }

        /// <summary>
        /// Returns the number of rows per chunk that keeps one chunk of <typeparamref name="T"/> under 64 KB - and
        /// therefore well under the Large Object Heap threshold - for every element size that permits it. This is the
        /// rule NFR-2 and NFR-3 describe; pass the result to
        /// <see cref="ColumnStore.Data.DataColumnCollection.Add{T}(String, Boolean, Int32)"/> for a column that will
        /// hold a large number of rows.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <returns>A power-of-two row count, at least 1.</returns>
        public static Int32 LohSafeRowCount<T>()
        {
            Int32 elementSizeInBytes = Unsafe.SizeOf<T>();
            Int32 lohSafeRows = LohSafeRowCountForElementSize(elementSizeInBytes);
            return lohSafeRows;
        }

        /// <summary>
        /// Returns the LOH-safe rows-per-chunk figure for an element of the given size. Exposed separately from
        /// <see cref="LohSafeRowCount{T}"/> so the sizing rule can be exercised directly, for every element size,
        /// without needing a real type of that size.
        /// </summary>
        /// <param name="elementSizeInBytes">Size of one stored element, in bytes. Must be positive.</param>
        /// <returns>A power-of-two row count, at least 1.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The element size is not positive.</exception>
        public static Int32 LohSafeRowCountForElementSize(Int32 elementSizeInBytes)
        {
            if (elementSizeInBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(elementSizeInBytes), elementSizeInBytes, "Element size must be positive."); }
            Int32 chunkRows = MINIMUM_CHUNK_ROWS;
            // Double the row count while the next doubling would still fit strictly under the 64 KB cap. Stopping at
            // the last power of two that fits means the chosen chunk is always more than half the cap - i.e. over
            // 32 KB - whenever the element size leaves any room to double at all.
            while (IsWithinChunkBudget(chunkRows << 1, elementSizeInBytes))
            {
                chunkRows = chunkRows << 1;
            }
            Int64 chunkBytes = (Int64)chunkRows * elementSizeInBytes;
            Contract.Assert(chunkBytes < LOH_THRESHOLD_BYTES || elementSizeInBytes >= LOH_THRESHOLD_BYTES, "A chunk may only reach the LOH threshold when a single element already does.");
            return chunkRows;
        }
        #endregion

        #region Private Methods
        // True when a chunk of the given row count and element size stays strictly below the 64 KB cap. The
        // multiplication is widened to Int64 so a large row count multiplied by a large element size cannot overflow
        // into a small positive number and wrongly report that it fits.
        private static Boolean IsWithinChunkBudget(Int32 chunkRows, Int32 elementSizeInBytes)
        {
            Int64 chunkBytes = (Int64)chunkRows * elementSizeInBytes;
            return chunkBytes < MAXIMUM_CHUNK_BYTES;
        }
        #endregion
    }
}
