///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies both of ChunkSizing's answers - the fixed default row count storage uses when it is not told
//   otherwise, and the LOH-safe rule that NFR-2 and NFR-3 describe: a power-of-two row count, a chunk size in the
//   16 KB - 64 KB band, and never anywhere near the ~85,000 byte Large Object Heap threshold.
// Assumptions: Reference-typed columns are sized against the reference width (IntPtr.Size), not the size of whatever
//   the reference points at - the referenced object lives elsewhere on the heap and is not part of the chunk.
// Design Considerations: The element types checked are exactly those named in 03_Design.md section 6.1, plus a sweep
//   over every element size from 1 to 4096 bytes. The sweep matters because the power-of-two rounding is where a
//   sizing rule most easily slips below the band, and it is much cheaper to prove over all sizes than to argue about
//   which ones are representative.
//   The default row count is tested separately from the band, because it deliberately no longer satisfies the band -
//   see ChunkSizing's own header for that trade. What the default still has to satisfy is being a positive power of
//   two, since every index calculation in TypedColumnStorage depends on it, and that is what is asserted here.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using ColumnStore.Data.Storage;
using Xunit;

namespace ColumnStore.Data.Tests.Storage
{
    /// <summary>
    /// Tests for <see cref="ChunkSizing"/>.
    /// </summary>
    public sealed class ChunkSizingTests
    {
        #region Private Constants
        // Lower bound of the target band from NFR-3, in bytes.
        private const Int32 MINIMUM_TARGET_BYTES = 16 * 1024;

        // Exclusive upper bound of the target band from NFR-3, in bytes.
        private const Int32 MAXIMUM_TARGET_BYTES = 64 * 1024;

        // The Large Object Heap threshold a chunk must stay under.
        private const Int32 LOH_THRESHOLD_BYTES = 85000;
        #endregion

        #region Public Methods
        /// <summary>
        /// Every element type named in the design's test plan produces a power-of-two row count whose chunk lands in
        /// the target band and well below the LOH threshold.
        /// </summary>
        [Fact]
        public void LohSafeRowCountForDesignatedTypesIsPowerOfTwoWithinTargetBand()
        {
            AssertLohSafeSizing<Byte>(sizeof(Byte));
            AssertLohSafeSizing<Int32>(sizeof(Int32));
            AssertLohSafeSizing<Int64>(sizeof(Int64));
            AssertLohSafeSizing<Decimal>(sizeof(Decimal));
            AssertLohSafeSizing<Guid>(16);
            AssertLohSafeSizing<DateTime>(sizeof(Int64));
            AssertLohSafeSizing<String>(IntPtr.Size);
        }

        /// <summary>
        /// The sizing rule holds for every element size a realistic column could have, not only the sizes of the
        /// types the library special-cases.
        /// </summary>
        [Fact]
        public void LohSafeRowCountForElementSizeHoldsTheBandAcrossEverySize()
        {
            for (Int32 elementSize = 1; elementSize <= 4096; elementSize++)
            {
                Int32 chunkRows = ChunkSizing.LohSafeRowCountForElementSize(elementSize);
                Boolean isPowerOfTwo = IsPowerOfTwo(chunkRows);
                Assert.True(isPowerOfTwo, $"Element size {elementSize} produced {chunkRows} rows, which is not a power of two.");
                Int64 chunkBytes = (Int64)chunkRows * elementSize;
                Assert.True(chunkBytes >= MINIMUM_TARGET_BYTES, $"Element size {elementSize} produced a {chunkBytes} byte chunk, below the {MINIMUM_TARGET_BYTES} byte floor.");
                Assert.True(chunkBytes < MAXIMUM_TARGET_BYTES, $"Element size {elementSize} produced a {chunkBytes} byte chunk, at or above the {MAXIMUM_TARGET_BYTES} byte ceiling.");
                Assert.True(chunkBytes < LOH_THRESHOLD_BYTES, $"Element size {elementSize} produced a {chunkBytes} byte chunk, which would land on the Large Object Heap.");
            }
        }

        /// <summary>
        /// An element larger than the whole target band cannot satisfy it; the rule degrades to a single row per
        /// chunk rather than producing a nonsensical zero.
        /// </summary>
        [Fact]
        public void LohSafeRowCountForElementSizeLargerThanTheBandFallsBackToOneRow()
        {
            Int32 chunkRows = ChunkSizing.LohSafeRowCountForElementSize(MAXIMUM_TARGET_BYTES * 2);
            Assert.Equal(1, chunkRows);
        }

        /// <summary>
        /// A non-positive element size is a caller error, not something to guess at.
        /// </summary>
        [Fact]
        public void LohSafeRowCountForElementSizeNonPositiveThrows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ChunkSizing.LohSafeRowCountForElementSize(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ChunkSizing.LohSafeRowCountForElementSize(-8));
        }
        /// <summary>
        /// The default row count is a positive power of two, whatever value it is set to - every index calculation in
        /// the storage layer depends on that and on nothing else about it.
        /// </summary>
        [Fact]
        public void DefaultRowCountIsAPositivePowerOfTwo()
        {
            Int32 defaultRowCount = ChunkSizing.DefaultRowCount;
            Assert.True(defaultRowCount > 0, $"The default row count is {defaultRowCount}, which is not positive.");
            Boolean isPowerOfTwo = IsPowerOfTwo(defaultRowCount);
            Assert.True(isPowerOfTwo, $"The default row count is {defaultRowCount}, which is not a power of two.");
        }

        /// <summary>
        /// Recommended is the default, for every element type - it is deliberately size-independent, unlike the
        /// LOH-safe rule.
        /// </summary>
        [Fact]
        public void RecommendedReturnsTheDefaultRowCountForEveryType()
        {
            Int32 expected = ChunkSizing.DefaultRowCount;
            Assert.Equal(expected, ChunkSizing.Recommended<Byte>());
            Assert.Equal(expected, ChunkSizing.Recommended<Int32>());
            Assert.Equal(expected, ChunkSizing.Recommended<Decimal>());
            Assert.Equal(expected, ChunkSizing.Recommended<Guid>());
            Assert.Equal(expected, ChunkSizing.Recommended<String>());
        }
        #endregion

        #region Private Methods
        // A power-of-two test written out here rather than taken from the framework, so that this file validates the
        // sizing rule against an independent check - and so that it needs nothing that .NET Framework lacks.
        private static Boolean IsPowerOfTwo(Int32 value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        // Asserts the full sizing contract for one element type, reporting the type name on failure so a regression
        // points straight at the offending instantiation.
        private static void AssertLohSafeSizing<T>(Int32 expectedElementSize)
        {
            Int32 chunkRows = ChunkSizing.LohSafeRowCount<T>();
            Boolean isPowerOfTwo = IsPowerOfTwo(chunkRows);
            Assert.True(isPowerOfTwo, $"{typeof(T).Name} produced {chunkRows} rows, which is not a power of two.");
            Int64 chunkBytes = (Int64)chunkRows * expectedElementSize;
            Assert.True(chunkBytes >= MINIMUM_TARGET_BYTES, $"{typeof(T).Name} produced a {chunkBytes} byte chunk, below the target band.");
            Assert.True(chunkBytes < MAXIMUM_TARGET_BYTES, $"{typeof(T).Name} produced a {chunkBytes} byte chunk, above the target band.");
            Assert.True(chunkBytes < LOH_THRESHOLD_BYTES, $"{typeof(T).Name} produced a {chunkBytes} byte chunk, which would land on the Large Object Heap.");
        }
        #endregion
    }
}
