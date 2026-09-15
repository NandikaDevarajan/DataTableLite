///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Bit-packed storage, 64 rows per UInt64 word. Serves two jobs through one mechanism: it is the storage
//   behind every DataColumn<Boolean> (1 bit per row instead of the 1 byte a Boolean[] would cost), and it is the
//   null-tracking side channel behind every nullable DataColumn<T> of any type (1 bit per row instead of a
//   Nullable<T> wrapper, a boxed Object, or an in-band sentinel value stolen from the column's value range).
// Assumptions: Single-writer access; not thread safe. Row indices are physical and always >= 0.
// Design Considerations: Reading a row beyond the allocated words returns false instead of throwing. That is required
//   rather than lenient: the null side channel is legitimately probed for rows whose value chunk exists but whose null
//   word has never been written, and an untouched row is "not null" / false by definition. This is the one place the
//   storage layer deliberately diverges from TypedColumnStorage's throw-on-read-past-end behaviour, and the reason it
//   is safe is that the authoritative row count lives in the row layer, not here.
//   This type knows nothing about row deletion - tombstones are RowDeletionTracker's job, in a separate type with a
//   separate instance, so that "is this bool cell true" and "is this row deleted" can never be conflated.
//   See 03_Design.md section 1.3.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ColumnStore.Data.Storage
{
    /// <summary>
    /// Stores one bit per row, packed 64 rows to a word. Used both as Boolean column storage and as per-column null
    /// tracking.
    /// </summary>
    public sealed class BitmapColumnStorage : IDataColumnStorage<Boolean>
    {
        #region Private Constants
        // Rows packed into a single UInt64 word.
        private const Int32 BITS_PER_WORD = 64;

        // log2(BITS_PER_WORD). Turns a row index into a word index with one right shift.
        private const Int32 WORD_SHIFT = 6;

        // BITS_PER_WORD - 1. Turns a row index into a bit position inside its word with one bitwise AND.
        private const Int32 BIT_MASK = 63;
        #endregion

        #region Private Members
        // Bit words in row order. Grown lazily, one word at a time, only up to the highest row actually written.
        private readonly List<UInt64> bitWords;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates empty bitmap storage. No words are allocated until the first write.
        /// </summary>
        public BitmapColumnStorage()
        {
            this.bitWords = new List<UInt64>();
        }
        #endregion

        #region Public Properties
        /// <summary>The CLR type this storage instance holds.</summary>
        public Type DataType
        {
            get { return typeof(Boolean); }
        }

        /// <summary>Number of 64-row words currently allocated.</summary>
        public Int32 WordCount
        {
            get { return this.bitWords.Count; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Returns the bit stored for <paramref name="rowIndex"/>. Rows beyond the highest word ever written return
        /// false rather than throwing - see this file's header for why that is by design.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The stored bit.</returns>
        /// <exception cref="IndexOutOfRangeException">The index is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Boolean Get(Int32 rowIndex)
        {
            if (rowIndex < 0) { throw new IndexOutOfRangeException($"Row Index: {rowIndex}"); }
            Int32 wordIndex = rowIndex >> WORD_SHIFT;
            if (wordIndex >= this.bitWords.Count) { return false; }
            UInt64 word = this.bitWords[wordIndex];
            UInt64 bit = 1UL << (rowIndex & BIT_MASK);
            return (word & bit) != 0UL;
        }

        /// <summary>
        /// Stores <paramref name="value"/> for <paramref name="rowIndex"/>, growing the word list if required.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The bit to store.</param>
        /// <exception cref="IndexOutOfRangeException">The index is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(Int32 rowIndex, Boolean value)
        {
            if (rowIndex < 0) { throw new IndexOutOfRangeException($"Row Index: {rowIndex}"); }
            Int32 wordIndex = rowIndex >> WORD_SHIFT;
            if (wordIndex >= this.bitWords.Count) { GrowToWord(wordIndex); }
            UInt64 bit = 1UL << (rowIndex & BIT_MASK);
            UInt64 word = this.bitWords[wordIndex];
            if (value) { this.bitWords[wordIndex] = word | bit; }
            else { this.bitWords[wordIndex] = word & ~bit; }
        }

        /// <summary>
        /// Counts the set bits across the whole bitmap. Diagnostic and test helper; not used on any hot path.
        /// </summary>
        /// <returns>The number of rows whose bit is set.</returns>
        public Int32 CountSetBits()
        {
            Int32 total = 0;
            for (Int32 wordIndex = 0; wordIndex < this.bitWords.Count; wordIndex++)
            {
                UInt64 word = this.bitWords[wordIndex];
                total += Bits.PopCount(word);
            }
            return total;
        }

        /// <summary>
        /// Drops every word. All rows read back as false afterwards.
        /// </summary>
        public void Clear()
        {
            this.bitWords.Clear();
        }
        #endregion

        #region Private Methods
        // Appends zeroed words until targetWordIndex is addressable. Kept out of Set so the common in-range write
        // stays inlineable.
        private void GrowToWord(Int32 targetWordIndex)
        {
            while (this.bitWords.Count <= targetWordIndex)
            {
                this.bitWords.Add(0UL);
            }
        }
        #endregion
    }
}
