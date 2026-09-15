///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Keeps a running count of non-deleted ("alive") rows per 64-row tombstone word, and answers prefix queries
//   over those counts in logarithmic time. This is the accelerator that makes RowDeletionTracker's logical/physical
//   index translation cheap; it holds no tombstone bits itself, only per-word alive counts.
// Assumptions: Words are appended in order and never removed (physical slots are permanent). A word's alive count
//   starts at 64 and moves by one at a time: down on a deletion, up on a revival. Single-writer access; not thread
//   safe.
// Design Considerations: 03_Design.md section 1.5 specifies a per-word deleted count plus a linear walk over words,
//   and explicitly invites replacing the walk with something better once correctness is established. The walk is
//   O(rows / 64) per translation - 15,625 iterations on a million-row table - which would make an indexed read after
//   any deletion painfully slow. A Fenwick (binary indexed) tree over the per-word counts turns that into O(log n)
//   for both recording a deletion and translating an index, with no rebuild and no dirty-state bookkeeping, at a cost
//   of eight bytes per 64 rows (0.125 bytes per row).
//   Alive counts are stored rather than deleted counts because the Fenwick "find the element containing the k-th unit"
//   descent works directly on the quantity being searched, which is exactly the alive count. The trailing bits of the
//   final word are counted as alive even when no row occupies them; that over-count is harmless because every caller
//   validates its logical index against the tracker's LogicalCount before descending, so a query can never reach
//   those phantom rows.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Storage
{
    /// <summary>
    /// Fenwick tree over per-word alive-row counts. Supports appending a word, recording one deletion or one
    /// revival, prefix sums, and locating the word holding the k-th alive row.
    /// </summary>
    internal sealed class AliveRowPrefixIndex
    {
        #region Private Constants
        // Rows represented by one tombstone word, and therefore the alive count a freshly appended word starts with.
        private const Int32 ROWS_PER_WORD = 64;

        // Initial number of word slots. A power of two, as the Fenwick descent requires, and small enough that a
        // table of a handful of rows does not over-allocate.
        private const Int32 INITIAL_WORD_CAPACITY = 8;
        #endregion

        #region Private Members
        // Alive-row count per word, indexed by word. Authoritative; the tree is derived from it and is rebuilt from it
        // whenever capacity grows.
        private Int32[] wordAliveCounts;

        // Fenwick tree over wordAliveCounts, one-based: node i covers the (i & -i) entries ending at i. Length is
        // wordCapacity + 1.
        private Int32[] fenwickNodes;

        // Number of word slots the arrays can hold. Always a power of two, which is what lets the descent start at a
        // single step of wordCapacity and halve it.
        private Int32 wordCapacity;

        // Number of words actually appended so far.
        private Int32 wordCount;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates an empty index with no words.
        /// </summary>
        public AliveRowPrefixIndex()
        {
            this.wordCapacity = INITIAL_WORD_CAPACITY;
            this.wordAliveCounts = new Int32[INITIAL_WORD_CAPACITY];
            this.fenwickNodes = new Int32[INITIAL_WORD_CAPACITY + 1];
            this.wordCount = 0;
        }
        #endregion

        #region Public Properties
        /// <summary>Number of words appended so far.</summary>
        public Int32 WordCount
        {
            get { return this.wordCount; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Appends one word, fully alive (64 rows). Called when a new physical row crosses into a new tombstone word.
        /// </summary>
        public void AppendWord()
        {
            if (this.wordCount == this.wordCapacity) { GrowCapacity(); }
            this.wordAliveCounts[this.wordCount] = ROWS_PER_WORD;
            AddToNode(this.wordCount, ROWS_PER_WORD);
            this.wordCount = this.wordCount + 1;
        }

        /// <summary>
        /// Records that one more row in <paramref name="wordIndex"/> has been deleted.
        /// </summary>
        /// <param name="wordIndex">Index of the tombstone word containing the deleted row.</param>
        public void RegisterDeletion(Int32 wordIndex)
        {
            Contract.Assert(wordIndex >= 0 && wordIndex < this.wordCount, "wordIndex must reference an appended word.");
            Contract.Assert(this.wordAliveCounts[wordIndex] > 0, "A word cannot have more deletions than rows.");
            this.wordAliveCounts[wordIndex] = this.wordAliveCounts[wordIndex] - 1;
            AddToNode(wordIndex, -1);
        }

        /// <summary>
        /// Records that one row in <paramref name="wordIndex"/> that was tombstoned is now alive. The exact mirror of
        /// <see cref="RegisterDeletion"/>: a Fenwick tree is as happy to add as to subtract, which is what makes a
        /// detached row cheap to make visible in place rather than having to be copied to a fresh slot.
        /// </summary>
        /// <param name="wordIndex">Index of the tombstone word containing the revived row.</param>
        public void RegisterRevival(Int32 wordIndex)
        {
            Contract.Assert(wordIndex >= 0 && wordIndex < this.wordCount, "wordIndex must reference an appended word.");
            Contract.Assert(this.wordAliveCounts[wordIndex] < ROWS_PER_WORD, "A word cannot hold more alive rows than it has bits.");
            this.wordAliveCounts[wordIndex] = this.wordAliveCounts[wordIndex] + 1;
            AddToNode(wordIndex, 1);
        }

        /// <summary>
        /// Returns the total number of alive rows in words [0, <paramref name="wordCountExclusive"/>).
        /// </summary>
        /// <param name="wordCountExclusive">Number of leading words to sum over.</param>
        /// <returns>The alive-row count across those words.</returns>
        public Int32 PrefixAlive(Int32 wordCountExclusive)
        {
            Contract.Assert(wordCountExclusive >= 0 && wordCountExclusive <= this.wordCount, "wordCountExclusive must be within the appended words.");
            Int32 total = 0;
            // Standard Fenwick prefix sum: walk from the node covering the last requested entry down to the root by
            // repeatedly stripping the lowest set bit of the index.
            for (Int32 nodeIndex = wordCountExclusive; nodeIndex > 0; nodeIndex = nodeIndex - (nodeIndex & -nodeIndex))
            {
                total = total + this.fenwickNodes[nodeIndex];
            }
            return total;
        }

        /// <summary>
        /// Finds the word holding the alive row at logical position <paramref name="logicalIndex"/>.
        /// </summary>
        /// <param name="logicalIndex">Zero-based position among alive rows. Must be less than the total alive count.</param>
        /// <param name="aliveBeforeWord">Receives the number of alive rows in all words before the returned one.</param>
        /// <returns>The index of the word containing that alive row.</returns>
        public Int32 FindWordContainingAliveRow(Int32 logicalIndex, out Int32 aliveBeforeWord)
        {
            Contract.Assert(logicalIndex >= 0, "logicalIndex must be non-negative.");
            // Fenwick descent: consume whole subtrees whose alive count still fits within the remaining target. What
            // is left in 'remaining' after the descent is the offset of the target row inside the word landed on, and
            // 'position' is that word's index.
            Int32 position = 0;
            Int32 remaining = logicalIndex;
            for (Int32 step = this.wordCapacity; step > 0; step = step >> 1)
            {
                Int32 candidateNode = position + step;
                if (candidateNode > this.wordCapacity) { continue; }
                Int32 candidateAlive = this.fenwickNodes[candidateNode];
                if (candidateAlive <= remaining)
                {
                    position = candidateNode;
                    remaining = remaining - candidateAlive;
                }
            }
            Contract.Assert(position < this.wordCount, "logicalIndex must reference an alive row inside the appended words.");
            aliveBeforeWord = logicalIndex - remaining;
            return position;
        }

        /// <summary>
        /// Drops every word and returns the index to its initial, empty state.
        /// </summary>
        public void Clear()
        {
            Array.Clear(this.wordAliveCounts, 0, this.wordAliveCounts.Length);
            Array.Clear(this.fenwickNodes, 0, this.fenwickNodes.Length);
            this.wordCount = 0;
        }
        #endregion

        #region Private Methods
        // Applies a delta to one entry and to every Fenwick node covering it, by repeatedly adding the lowest set bit
        // of the one-based index. O(log wordCapacity).
        private void AddToNode(Int32 wordIndex, Int32 delta)
        {
            for (Int32 nodeIndex = wordIndex + 1; nodeIndex <= this.wordCapacity; nodeIndex = nodeIndex + (nodeIndex & -nodeIndex))
            {
                this.fenwickNodes[nodeIndex] = this.fenwickNodes[nodeIndex] + delta;
            }
        }

        // Doubles the word capacity and rebuilds the tree from the per-word counts. Doubling keeps the capacity a
        // power of two for the descent, and makes the O(words) rebuild amortise to O(1) per appended word.
        private void GrowCapacity()
        {
            Int32 newCapacity = this.wordCapacity * 2;
            Array.Resize(ref this.wordAliveCounts, newCapacity);
            this.wordCapacity = newCapacity;
            this.fenwickNodes = new Int32[newCapacity + 1];
            RebuildTree();
        }

        // Rebuilds every Fenwick node from wordAliveCounts in O(words): seed each node with its own entry, then push
        // each node's total into its parent.
        private void RebuildTree()
        {
            for (Int32 wordIndex = 0; wordIndex < this.wordCount; wordIndex++)
            {
                this.fenwickNodes[wordIndex + 1] = this.wordAliveCounts[wordIndex];
            }
            for (Int32 nodeIndex = 1; nodeIndex <= this.wordCapacity; nodeIndex++)
            {
                Int32 parentIndex = nodeIndex + (nodeIndex & -nodeIndex);
                if (parentIndex <= this.wordCapacity)
                {
                    this.fenwickNodes[parentIndex] = this.fenwickNodes[parentIndex] + this.fenwickNodes[nodeIndex];
                }
            }
        }
        #endregion
    }
}
