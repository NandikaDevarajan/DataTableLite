///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Chunked, strongly typed column storage. Backs every DataColumn<T> except DataColumn<Boolean>, which uses
//   BitmapColumnStorage so a bool costs one bit rather than one byte per row.
// Assumptions: Single-writer access; not thread safe. Row indices are physical and always >= 0. Chunk row count is a
//   power of two - enforced in the constructor, relied upon by every index calculation afterwards.
// Design Considerations: Growth appends whole chunks instead of doubling one contiguous array. That bounds any single
//   allocation below the Large Object Heap threshold (~85,000 bytes, NFR-2/NFR-3), removes the periodic O(n) copy a
//   doubling array pays, and means a sparse or out-of-order write pattern only allocates the chunks it actually
//   touches. Index math is a shift and a mask, never a division, because it runs on every cell access.
//   Get/Set carry only the two tests they cannot do without: the negative-index guard demanded by NFR-11, and the
//   chunk-count guard that turns a read past the end into IndexOutOfRangeException instead of the List<T> indexer's
//   ArgumentOutOfRangeException. The offset inside a chunk needs no check at all - masking with chunkMask makes it
//   in-range by construction. There are deliberately no Contract.Assert calls in Get/Set: this is the hottest path in
//   the library. See 03_Design.md section 1.2.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ColumnStore.Data.Storage
{
    /// <summary>
    /// Stores one column's values in a list of fixed-size, power-of-two-length chunks.
    /// </summary>
    /// <typeparam name="T">The column's element type.</typeparam>
    public sealed class TypedColumnStorage<T> : IDataColumnStorage<T>
    {
        #region Private Members
        // Backing chunks, in row order. Appended one at a time, never reallocated as a whole, and only allocated for
        // chunks a write has actually reached.
        private readonly List<T[]> storageChunks;

        // Number of rows in one chunk. Always a power of two.
        private readonly Int32 chunkRowCount;

        // log2(chunkRowCount). Turns rowIndex into a chunk index with a single right shift.
        private readonly Int32 chunkShift;

        // chunkRowCount - 1. Turns rowIndex into an offset inside its chunk with a single bitwise AND.
        private readonly Int32 chunkMask;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates empty storage using the chunk row count recommended for <typeparamref name="T"/> by
        /// <see cref="ChunkSizing"/>.
        /// </summary>
        public TypedColumnStorage()
            : this(ChunkSizing.Recommended<T>())
        {
        }

        /// <summary>
        /// Creates empty storage with an explicit chunk row count. Intended for unusual access patterns and for tests
        /// that need to force chunk-boundary conditions at small row counts.
        /// </summary>
        /// <param name="chunkRowCount">Rows per chunk. Must be a positive power of two.</param>
        /// <exception cref="ArgumentOutOfRangeException">The row count is not a positive power of two.</exception>
        public TypedColumnStorage(Int32 chunkRowCount)
        {
            if (chunkRowCount <= 0) { throw new ArgumentOutOfRangeException(nameof(chunkRowCount), chunkRowCount, "Chunk row count must be positive."); }
            Boolean isPowerOfTwo = Bits.IsPow2(chunkRowCount);
            if (isPowerOfTwo == false) { throw new ArgumentOutOfRangeException(nameof(chunkRowCount), chunkRowCount, "Chunk row count must be a power of two so index math can use shift and mask."); }
            this.storageChunks = new List<T[]>();
            this.chunkRowCount = chunkRowCount;
            this.chunkShift = Bits.Log2((UInt32)chunkRowCount);
            this.chunkMask = chunkRowCount - 1;
        }
        #endregion

        #region Public Properties
        /// <summary>The CLR type this storage instance holds.</summary>
        public Type DataType
        {
            get { return typeof(T); }
        }

        /// <summary>Number of rows held by one chunk. Always a power of two.</summary>
        public Int32 ChunkRowCount
        {
            get { return this.chunkRowCount; }
        }

        /// <summary>Number of chunks currently allocated.</summary>
        public Int32 ChunkCount
        {
            get { return this.storageChunks.Count; }
        }

        /// <summary>
        /// Number of rows the currently allocated chunks can hold without further growth. Rows below this bound that
        /// have never been written read back as <c>default(T)</c>.
        /// </summary>
        public Int32 AllocatedRowCapacity
        {
            get { return this.storageChunks.Count * this.chunkRowCount; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Returns the value at <paramref name="rowIndex"/>. Rows inside an allocated chunk that were never written
        /// read back as <c>default(T)</c>.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The stored value.</returns>
        /// <exception cref="IndexOutOfRangeException">The index is negative, or beyond every allocated chunk.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get(Int32 rowIndex)
        {
            if (rowIndex < 0) { throw new IndexOutOfRangeException($"Row Index: {rowIndex}"); }
            Int32 chunkIndex = rowIndex >> this.chunkShift;
            if (chunkIndex >= this.storageChunks.Count) { throw new IndexOutOfRangeException($"Row Index: {rowIndex}"); }
            T[] chunk = this.storageChunks[chunkIndex];
            return chunk[rowIndex & this.chunkMask];
        }

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="rowIndex"/>, allocating chunks up to and including the
        /// target chunk if they do not exist yet.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The value to store.</param>
        /// <exception cref="IndexOutOfRangeException">The index is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(Int32 rowIndex, T value)
        {
            if (rowIndex < 0) { throw new IndexOutOfRangeException($"Row Index: {rowIndex}"); }
            Int32 chunkIndex = rowIndex >> this.chunkShift;
            if (chunkIndex >= this.storageChunks.Count) { GrowToChunk(chunkIndex); }
            T[] chunk = this.storageChunks[chunkIndex];
            chunk[rowIndex & this.chunkMask] = value;
        }

        /// <summary>
        /// Drops every chunk. The instance returns to its initial state, so a subsequent <see cref="Get"/> on any row
        /// throws until that row is written again.
        /// </summary>
        public void Clear()
        {
            this.storageChunks.Clear();
        }
        #endregion

        #region Private Methods
        // Appends empty chunks until targetChunkIndex is addressable. Kept out of Set so the common in-range write
        // stays small enough for the JIT to inline. Chunks between the current end and the target are allocated too,
        // so an intentionally sparse write leaves readable default(T) rows behind it rather than a hole.
        private void GrowToChunk(Int32 targetChunkIndex)
        {
            while (this.storageChunks.Count <= targetChunkIndex)
            {
                T[] newChunk = new T[this.chunkRowCount];
                this.storageChunks.Add(newChunk);
            }
        }
        #endregion
    }
}
