///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies bit-packed storage across the two boundaries where bit arithmetic goes wrong - the top of a word
//   (bits 63/64) and the growth of the word list - plus the deliberate read-past-the-end-returns-false contract.
// Assumptions: One word holds 64 rows. The tests hard-code that, because the shift and mask constants encode it and a
//   change to either would need these tests to be re-read rather than silently pass.
// Design Considerations: The "untouched high row reads false without throwing" test is protecting a contract, not
//   leniency: nullable columns probe null bits for rows whose value chunk exists but whose null word was never
//   written, and that read must answer "no value bit set" rather than throw. See BitmapColumnStorage's header.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using ColumnStore.Data.Storage;
using Xunit;

namespace ColumnStore.Data.Tests.Storage
{
    /// <summary>
    /// Tests for <see cref="BitmapColumnStorage"/>.
    /// </summary>
    public sealed class BitmapColumnStorageTests
    {
        #region Public Methods
        /// <summary>
        /// Bits round-trip across the 63/64 boundary, which is where a mis-sized shift or mask first shows up.
        /// </summary>
        [Fact]
        public void SetAndGetAcrossWordBoundaryRoundTrips()
        {
            BitmapColumnStorage storage = new BitmapColumnStorage();
            storage.Set(62, true);
            storage.Set(63, true);
            storage.Set(64, true);
            storage.Set(65, false);
            Assert.True(storage.Get(62));
            Assert.True(storage.Get(63));
            Assert.True(storage.Get(64));
            Assert.False(storage.Get(65));
            Assert.Equal(2, storage.WordCount);
        }

        /// <summary>
        /// Clearing a bit leaves its neighbours alone - the classic mask-inversion bug.
        /// </summary>
        [Fact]
        public void SetClearingOneBitLeavesNeighboursIntact()
        {
            BitmapColumnStorage storage = new BitmapColumnStorage();
            for (Int32 rowIndex = 0; rowIndex < 130; rowIndex++)
            {
                storage.Set(rowIndex, true);
            }
            storage.Set(64, false);
            Assert.True(storage.Get(63));
            Assert.False(storage.Get(64));
            Assert.True(storage.Get(65));
            Assert.Equal(129, storage.CountSetBits());
        }

        /// <summary>
        /// A write far past the current end grows the word list and leaves the words between it and the previous end
        /// readable as false.
        /// </summary>
        [Fact]
        public void SetFarBeyondCurrentSizeGrowsWordListLazily()
        {
            BitmapColumnStorage storage = new BitmapColumnStorage();
            storage.Set(0, true);
            Assert.Equal(1, storage.WordCount);
            storage.Set(1000, true);
            Assert.Equal(16, storage.WordCount);
            Assert.True(storage.Get(1000));
            Assert.False(storage.Get(999));
            Assert.False(storage.Get(500));
            Assert.Equal(2, storage.CountSetBits());
        }

        /// <summary>
        /// Reading a row beyond every allocated word returns false rather than throwing - required by the null side
        /// channel, not merely tolerated.
        /// </summary>
        [Fact]
        public void GetBeyondAllocatedWordsReturnsFalseWithoutThrowing()
        {
            BitmapColumnStorage storage = new BitmapColumnStorage();
            Assert.False(storage.Get(0));
            Assert.False(storage.Get(1_000_000));
            storage.Set(5, true);
            Assert.False(storage.Get(64));
        }

        /// <summary>
        /// A negative index is rejected explicitly, on both read and write, with the same exception type the chunked
        /// storage uses (NFR-11).
        /// </summary>
        [Fact]
        public void GetAndSetNegativeIndexThrowsIndexOutOfRange()
        {
            BitmapColumnStorage storage = new BitmapColumnStorage();
            Assert.Throws<IndexOutOfRangeException>(() => storage.Get(-1));
            Assert.Throws<IndexOutOfRangeException>(() => storage.Set(-1, true));
        }

        /// <summary>
        /// Clear drops every word, so all rows read false again.
        /// </summary>
        [Fact]
        public void ClearDropsEveryWord()
        {
            BitmapColumnStorage storage = new BitmapColumnStorage();
            storage.Set(200, true);
            Assert.Equal(4, storage.WordCount);
            storage.Clear();
            Assert.Equal(0, storage.WordCount);
            Assert.False(storage.Get(200));
            Assert.Equal(0, storage.CountSetBits());
        }

        /// <summary>
        /// Bit-packed storage really is the <see cref="IDataColumnStorage{T}"/> implementation used for Boolean, so
        /// the column layer can substitute it without a special case of its own.
        /// </summary>
        [Fact]
        public void BitmapStorageImplementsBooleanStorageContract()
        {
            IDataColumnStorage<Boolean> storage = new BitmapColumnStorage();
            storage.Set(7, true);
            Assert.True(storage.Get(7));
            storage.Clear();
            Assert.False(storage.Get(7));
        }
        #endregion
    }
}
