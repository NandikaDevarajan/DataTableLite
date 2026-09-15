///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies chunked typed storage: round-trips, lazy growth, sparse writes, the bounds-checking contract of
//   NFR-11, and that Clear really releases everything.
// Assumptions: Tests use a small explicit chunk row count so chunk-boundary behaviour is reachable without
//   materialising thousands of rows. The production default comes from ChunkSizing and is verified there.
// Design Considerations: The growth tests assert on ChunkCount rather than on timing or memory, because the
//   requirement being protected (NFR-2: no single ever-doubling allocation) is structural. If a future change
//   replaced chunking with a doubling array, ChunkCount would stop tracking the row count and these tests would fail
//   for the right reason.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using ColumnStore.Data.Storage;
using Xunit;

namespace ColumnStore.Data.Tests.Storage
{
    /// <summary>
    /// Tests for <see cref="TypedColumnStorage{T}"/>.
    /// </summary>
    public sealed class TypedColumnStorageTests
    {
        #region Private Constants
        // Small power-of-two chunk size used throughout, so a handful of rows crosses several chunk boundaries.
        private const Int32 TEST_CHUNK_ROWS = 8;
        #endregion

        #region Public Methods
        /// <summary>
        /// Values written inside one chunk read back unchanged.
        /// </summary>
        [Fact]
        public void SetAndGetWithinOneChunkRoundTrips()
        {
            TypedColumnStorage<Int32> storage = new TypedColumnStorage<Int32>(TEST_CHUNK_ROWS);
            for (Int32 rowIndex = 0; rowIndex < TEST_CHUNK_ROWS; rowIndex++)
            {
                storage.Set(rowIndex, rowIndex * 7);
            }
            for (Int32 rowIndex = 0; rowIndex < TEST_CHUNK_ROWS; rowIndex++)
            {
                Assert.Equal(rowIndex * 7, storage.Get(rowIndex));
            }
            Assert.Equal(1, storage.ChunkCount);
        }

        /// <summary>
        /// Values written across many chunk boundaries read back unchanged, and the chunk count grows in step with
        /// the rows rather than by doubling one allocation.
        /// </summary>
        [Fact]
        public void SetAndGetAcrossChunkBoundariesRoundTripsAndGrowsByChunk()
        {
            TypedColumnStorage<Int64> storage = new TypedColumnStorage<Int64>(TEST_CHUNK_ROWS);
            Int32 rowCount = TEST_CHUNK_ROWS * 5 + 3;
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                storage.Set(rowIndex, rowIndex * 1000L);
            }
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                Assert.Equal(rowIndex * 1000L, storage.Get(rowIndex));
            }
            Int32 expectedChunkCount = 6;
            Assert.Equal(expectedChunkCount, storage.ChunkCount);
            Assert.Equal(expectedChunkCount * TEST_CHUNK_ROWS, storage.AllocatedRowCapacity);
        }

        /// <summary>
        /// A write far beyond the current size allocates the chunks up to it and leaves the untouched rows readable as
        /// <c>default(T)</c> rather than as a hole that throws.
        /// </summary>
        [Fact]
        public void SetFarBeyondCurrentSizeGrowsLazilyAndIntermediateRowsReadDefault()
        {
            TypedColumnStorage<Int32> storage = new TypedColumnStorage<Int32>(TEST_CHUNK_ROWS);
            storage.Set(0, 11);
            storage.Set(100, 99);
            Assert.Equal(11, storage.Get(0));
            Assert.Equal(99, storage.Get(100));
            for (Int32 rowIndex = 1; rowIndex < 100; rowIndex++)
            {
                Assert.Equal(0, storage.Get(rowIndex));
            }
            Assert.Equal(13, storage.ChunkCount);
        }

        /// <summary>
        /// Reference-typed storage round-trips references and reads unwritten rows as null.
        /// </summary>
        [Fact]
        public void SetAndGetReferenceTypeRoundTripsAndDefaultsToNull()
        {
            TypedColumnStorage<String> storage = new TypedColumnStorage<String>(TEST_CHUNK_ROWS);
            storage.Set(3, "three");
            Assert.Equal("three", storage.Get(3));
            Assert.Null(storage.Get(2));
        }

        /// <summary>
        /// Reading past every allocated chunk throws <see cref="IndexOutOfRangeException"/> - the one exception type
        /// NFR-11 requires from every storage implementation - rather than the List indexer's own exception shape.
        /// </summary>
        [Fact]
        public void GetBeyondAllocatedChunksThrowsIndexOutOfRange()
        {
            TypedColumnStorage<Int32> storage = new TypedColumnStorage<Int32>(TEST_CHUNK_ROWS);
            Assert.Throws<IndexOutOfRangeException>(() => storage.Get(0));
            storage.Set(0, 1);
            Assert.Throws<IndexOutOfRangeException>(() => storage.Get(TEST_CHUNK_ROWS));
        }

        /// <summary>
        /// A negative index is rejected explicitly, on both read and write.
        /// </summary>
        [Fact]
        public void GetAndSetNegativeIndexThrowsIndexOutOfRange()
        {
            TypedColumnStorage<Int32> storage = new TypedColumnStorage<Int32>(TEST_CHUNK_ROWS);
            Assert.Throws<IndexOutOfRangeException>(() => storage.Get(-1));
            Assert.Throws<IndexOutOfRangeException>(() => storage.Set(-1, 5));
        }

        /// <summary>
        /// Clear drops every chunk, returning the instance to its initial state - so a read that used to succeed
        /// throws again.
        /// </summary>
        [Fact]
        public void ClearDropsEveryChunk()
        {
            TypedColumnStorage<Int32> storage = new TypedColumnStorage<Int32>(TEST_CHUNK_ROWS);
            storage.Set(20, 7);
            Assert.Equal(3, storage.ChunkCount);
            storage.Clear();
            Assert.Equal(0, storage.ChunkCount);
            Assert.Equal(0, storage.AllocatedRowCapacity);
            Assert.Throws<IndexOutOfRangeException>(() => storage.Get(0));
        }

        /// <summary>
        /// The chunk row count must be a positive power of two, because every index calculation is a shift and a mask.
        /// </summary>
        [Fact]
        public void ConstructorRejectsChunkSizesThatBreakTheIndexMath()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TypedColumnStorage<Int32>(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TypedColumnStorage<Int32>(-8));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TypedColumnStorage<Int32>(12));
        }

        /// <summary>
        /// The default constructor takes its chunk size from <see cref="ChunkSizing"/>, so a production column gets
        /// the library default without the caller having to ask for it.
        /// </summary>
        [Fact]
        public void DefaultConstructorUsesRecommendedChunkSize()
        {
            TypedColumnStorage<Int32> storage = new TypedColumnStorage<Int32>();
            Int32 expectedChunkRows = ChunkSizing.Recommended<Int32>();
            Assert.Equal(expectedChunkRows, storage.ChunkRowCount);
            Assert.Equal(typeof(Int32), storage.DataType);
        }
        #endregion
    }
}
