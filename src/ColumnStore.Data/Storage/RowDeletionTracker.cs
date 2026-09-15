///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Owns row lifetime at the storage level: how many physical slots exist, which of them are tombstoned, and
//   the translation between a logical (visible, post-deletion) row position and its physical storage slot.
// Assumptions: Single-writer access; not thread safe. Physical slots are appended in order, are never reused, and are
//   never physically removed - deleting a row shifts nothing, which is the entire point (FR-13). Tombstone semantics
//   are fixed: a set bit means DELETED (03_Design.md open question 1).
// Design Considerations: The alternative to tombstones is physically compacting every column on every delete, which
//   is O(rows x columns) per call and invalidates every row index a caller may be holding. Tombstoning makes a delete
//   O(log words) and keeps "physical index is permanent" true, at the price of needing index translation on the read
//   path - which AliveRowPrefixIndex reduces to O(log words), and which short-circuits to the identity while nothing
//   has been deleted, so the overwhelmingly common case costs a single Boolean test.
//   MarkDeleted is idempotent: deleting an already-deleted row is a no-op rather than an error. That choice is
//   documented in DataRowCollection.Delete and covered by tests; it makes predicate-driven cleanup loops over stale
//   row handles safe, and unlike the row-commit protocol there is no state a repeated delete can corrupt.
//   A DETACHED row (created by NewRow, not yet part of the table) is appended as an already-tombstoned slot, and
//   joins the table when MarkAlive clears that tombstone in place. Reusing the tombstone this way is what lets any
//   number of detached rows exist at once without a second, separate "not a row yet" concept that every translation
//   would then have to know about - and because the slot never moves, a row handle taken before the row was added
//   still addresses it afterwards. A detached row that is never added simply keeps its tombstone forever, which is
//   exactly the representation of a discarded row.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

#if !NETFRAMEWORK
using System.Runtime.Intrinsics.X86;
#endif

namespace ColumnStore.Data.Storage
{
    /// <summary>
    /// Tombstone bitmap plus logical/physical row index translation.
    /// </summary>
    internal sealed class RowDeletionTracker
    {
        #region Private Constants
        // Rows packed into a single tombstone word.
        private const Int32 ROWS_PER_WORD = 64;

        // log2(ROWS_PER_WORD). Turns a physical row index into a word index with one right shift.
        private const Int32 WORD_SHIFT = 6;

        // ROWS_PER_WORD - 1. Turns a physical row index into a bit position inside its word with one bitwise AND.
        private const Int32 BIT_MASK = 63;
        #endregion

        #region Private Members
        // Tombstone bits in physical row order, 1 = deleted. Grown one word at a time as physical slots are appended,
        // so a word always exists for every physical slot.
        private readonly List<UInt64> tombstoneWords;

        // Per-word alive counts with a Fenwick tree over them. This is the "running count per word" of the design,
        // stored as alive rather than deleted rows and indexed for O(log n) prefix queries.
        private readonly AliveRowPrefixIndex alivePrefixIndex;

        // Number of physical slots appended. Physical slots are never reused, so this only ever grows until Clear.
        private Int32 physicalCount;

        // Number of tombstoned slots. Maintained incrementally - never recomputed by rescanning the bitmap.
        private Int32 deletedCount;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a tracker with no rows.
        /// </summary>
        public RowDeletionTracker()
        {
            this.tombstoneWords = new List<UInt64>();
            this.alivePrefixIndex = new AliveRowPrefixIndex();
            this.physicalCount = 0;
            this.deletedCount = 0;
        }
        #endregion

        #region Public Properties
        /// <summary>Number of physical storage slots appended so far, deleted ones included.</summary>
        public Int32 PhysicalCount
        {
            get { return this.physicalCount; }
        }

        /// <summary>Number of tombstoned slots.</summary>
        public Int32 DeletedCount
        {
            get { return this.deletedCount; }
        }

        /// <summary>Number of visible rows: physical slots minus tombstoned ones.</summary>
        public Int32 LogicalCount
        {
            get { return this.physicalCount - this.deletedCount; }
        }

        /// <summary>
        /// The physical slot the next appended row will occupy. Used by the row layer to hand out a slot for a
        /// reserved-but-uncommitted row before deciding whether it becomes visible.
        /// </summary>
        public Int32 NextPhysicalIndex
        {
            get { return this.physicalCount; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Appends one visible physical slot.
        /// </summary>
        /// <returns>The physical index of the appended slot.</returns>
        public Int32 AppendAliveRow()
        {
            Int32 appendedIndex = this.physicalCount;
            EnsureWordExistsFor(appendedIndex);
            this.physicalCount = this.physicalCount + 1;
            return appendedIndex;
        }

        /// <summary>
        /// Appends one physical slot that is tombstoned from the outset - the representation of a DETACHED row: one
        /// created by NewRow, whose field writes land in column storage immediately but which is not part of the
        /// table until <see cref="MarkAlive"/> makes it visible. Left tombstoned forever, it is the representation of
        /// a discarded row instead. Either way the slot is consumed and never reused.
        /// </summary>
        /// <returns>The physical index of the appended slot.</returns>
        public Int32 AppendDetachedRow()
        {
            Int32 appendedIndex = AppendAliveRow();
            MarkDeleted(appendedIndex);
            return appendedIndex;
        }

        /// <summary>
        /// Tombstones a physical slot. Idempotent - marking an already-deleted slot does nothing.
        /// </summary>
        /// <param name="physicalRowIndex">The slot to tombstone.</param>
        public void MarkDeleted(Int32 physicalRowIndex)
        {
            Contract.Assert(physicalRowIndex >= 0 && physicalRowIndex < this.physicalCount, "physicalRowIndex must reference an appended slot.");
            Int32 wordIndex = physicalRowIndex >> WORD_SHIFT;
            UInt64 bit = 1UL << (physicalRowIndex & BIT_MASK);
            UInt64 word = this.tombstoneWords[wordIndex];
            if ((word & bit) != 0UL) { return; }
            this.tombstoneWords[wordIndex] = word | bit;
            this.deletedCount = this.deletedCount + 1;
            this.alivePrefixIndex.RegisterDeletion(wordIndex);
        }

        /// <summary>
        /// Clears a slot's tombstone, making it visible and counted. Idempotent - reviving an already-visible slot
        /// does nothing. This is how a detached row joins the table: its slot was appended tombstoned by
        /// <see cref="AppendDetachedRow"/> and becomes visible IN PLACE, so nothing is copied and no row handle a
        /// caller is holding goes stale.
        /// </summary>
        /// <param name="physicalRowIndex">The slot to revive.</param>
        public void MarkAlive(Int32 physicalRowIndex)
        {
            Contract.Assert(physicalRowIndex >= 0 && physicalRowIndex < this.physicalCount, "physicalRowIndex must reference an appended slot.");
            Int32 wordIndex = physicalRowIndex >> WORD_SHIFT;
            UInt64 bit = 1UL << (physicalRowIndex & BIT_MASK);
            UInt64 word = this.tombstoneWords[wordIndex];
            if ((word & bit) == 0UL) { return; }
            this.tombstoneWords[wordIndex] = word & ~bit;
            this.deletedCount = this.deletedCount - 1;
            this.alivePrefixIndex.RegisterRevival(wordIndex);
        }

        /// <summary>
        /// True when the given physical slot has been tombstoned.
        /// </summary>
        /// <param name="physicalRowIndex">The slot to test.</param>
        /// <returns>True when deleted.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Boolean IsDeleted(Int32 physicalRowIndex)
        {
            if (physicalRowIndex < 0 || physicalRowIndex >= this.physicalCount) { return false; }
            Int32 wordIndex = physicalRowIndex >> WORD_SHIFT;
            UInt64 bit = 1UL << (physicalRowIndex & BIT_MASK);
            UInt64 word = this.tombstoneWords[wordIndex];
            return (word & bit) != 0UL;
        }

        /// <summary>
        /// Translates a logical (visible) row position to its physical storage slot.
        /// </summary>
        /// <param name="logicalIndex">Zero-based position among visible rows.</param>
        /// <returns>The physical slot holding that row.</returns>
        public Int32 ToPhysicalIndex(Int32 logicalIndex)
        {
            Contract.Assert(logicalIndex >= 0 && logicalIndex < LogicalCount, "logicalIndex must reference a visible row.");
            // Nothing deleted means the two numbering schemes are identical. This is the common case and it is worth
            // one test to make it free.
            if (this.deletedCount == 0) { return logicalIndex; }
            Int32 aliveBeforeWord = 0;
            Int32 wordIndex = this.alivePrefixIndex.FindWordContainingAliveRow(logicalIndex, out aliveBeforeWord);
            Int32 offsetWithinWord = logicalIndex - aliveBeforeWord;
            UInt64 word = this.tombstoneWords[wordIndex];
            Int32 bitPosition = FindNthAliveBit(word, offsetWithinWord);
            return (wordIndex << WORD_SHIFT) + bitPosition;
        }

        /// <summary>
        /// Translates a physical storage slot to its logical (visible) row position. For a tombstoned slot the result
        /// is the logical position of the next visible row - test <see cref="IsDeleted"/> first when that distinction
        /// matters.
        /// </summary>
        /// <param name="physicalIndex">The physical slot.</param>
        /// <returns>The number of visible rows before that slot.</returns>
        public Int32 ToLogicalIndex(Int32 physicalIndex)
        {
            Contract.Assert(physicalIndex >= 0 && physicalIndex < this.physicalCount, "physicalIndex must reference an appended slot.");
            if (this.deletedCount == 0) { return physicalIndex; }
            Int32 wordIndex = physicalIndex >> WORD_SHIFT;
            Int32 aliveBeforeWord = this.alivePrefixIndex.PrefixAlive(wordIndex);
            Int32 bitPosition = physicalIndex & BIT_MASK;
            UInt64 word = this.tombstoneWords[wordIndex];
            // Bits strictly below bitPosition, then count the alive (zero) ones among them.
            UInt64 precedingBitsMask = (1UL << bitPosition) - 1UL;
            UInt64 aliveBitsBefore = ~word & precedingBitsMask;
            Int32 aliveWithinWord = Bits.PopCount(aliveBitsBefore);
            return aliveBeforeWord + aliveWithinWord;
        }

        /// <summary>
        /// Finds the first visible physical slot at or after <paramref name="startPhysicalIndex"/>. This is the
        /// enumeration primitive: walking physical slots and skipping tombstones is materially cheaper than
        /// translating every logical position, and whole words of deleted rows are skipped in one step.
        /// </summary>
        /// <param name="startPhysicalIndex">Physical slot to start searching from.</param>
        /// <param name="alivePhysicalIndex">Receives the found slot, or -1 when there is none.</param>
        /// <returns>True when a visible slot was found.</returns>
        public Boolean TryGetNextAlivePhysicalIndex(Int32 startPhysicalIndex, out Int32 alivePhysicalIndex)
        {
            Int32 searchIndex = startPhysicalIndex;
            if (searchIndex < 0) { searchIndex = 0; }
            if (this.deletedCount == 0)
            {
                if (searchIndex < this.physicalCount)
                {
                    alivePhysicalIndex = searchIndex;
                    return true;
                }
                alivePhysicalIndex = -1;
                return false;
            }
            while (searchIndex < this.physicalCount)
            {
                Int32 wordIndex = searchIndex >> WORD_SHIFT;
                Int32 bitPosition = searchIndex & BIT_MASK;
                UInt64 word = this.tombstoneWords[wordIndex];
                UInt64 aliveBitsAtOrAfter = ~word & (UInt64.MaxValue << bitPosition);
                if (aliveBitsAtOrAfter != 0UL)
                {
                    Int32 foundBit = Bits.TrailingZeroCount(aliveBitsAtOrAfter);
                    Int32 foundIndex = (wordIndex << WORD_SHIFT) + foundBit;
                    if (foundIndex < this.physicalCount)
                    {
                        alivePhysicalIndex = foundIndex;
                        return true;
                    }
                    alivePhysicalIndex = -1;
                    return false;
                }
                searchIndex = (wordIndex + 1) << WORD_SHIFT;
            }
            alivePhysicalIndex = -1;
            return false;
        }

        /// <summary>
        /// Drops every slot and tombstone, returning the tracker to its initial state.
        /// </summary>
        public void Clear()
        {
            this.tombstoneWords.Clear();
            this.alivePrefixIndex.Clear();
            this.physicalCount = 0;
            this.deletedCount = 0;
        }
        #endregion

        #region Private Methods
        // Appends tombstone words (and matching prefix-index words) until the given physical slot has a word. Called
        // once per appended row; the body runs only on the one row in 64 that opens a new word.
        private void EnsureWordExistsFor(Int32 physicalRowIndex)
        {
            Int32 wordIndex = physicalRowIndex >> WORD_SHIFT;
            while (this.tombstoneWords.Count <= wordIndex)
            {
                this.tombstoneWords.Add(0UL);
                this.alivePrefixIndex.AppendWord();
            }
        }

        // Returns the bit position of the (n+1)-th alive (zero) bit of a tombstone word, counting from bit 0 upward.
        // PDEP does this in a single instruction by depositing one set bit into the n-th zero position of the word;
        // the portable fallback clears the n lowest alive bits and reads off the next one.
        private static Int32 FindNthAliveBit(UInt64 tombstoneWord, Int32 aliveOffset)
        {
            Contract.Assert(aliveOffset >= 0 && aliveOffset < ROWS_PER_WORD, "aliveOffset must be a bit position within one word.");
            UInt64 aliveBits = ~tombstoneWord;
#if !NETFRAMEWORK
            // System.Runtime.Intrinsics does not exist on .NET Framework, so this whole arm compiles away there and
            // the portable loop below is the only implementation. It is the same answer, reached by counting rather
            // than by depositing a bit.
            if (Bmi2.X64.IsSupported)
            {
                UInt64 selectedBit = Bmi2.X64.ParallelBitDeposit(1UL << aliveOffset, aliveBits);
                return Bits.TrailingZeroCount(selectedBit);
            }
#endif
            UInt64 remainingAliveBits = aliveBits;
            for (Int32 skipped = 0; skipped < aliveOffset; skipped++)
            {
                remainingAliveBits = remainingAliveBits & (remainingAliveBits - 1UL);
            }
            return Bits.TrailingZeroCount(remainingAliveBits);
        }
        #endregion
    }
}
