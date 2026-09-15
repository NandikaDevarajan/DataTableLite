///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies the logical/physical index translation - the most algorithmically subtle piece in the library
//   (02_Architecture section 6) - against every deletion pattern the design's test plan calls out, and against a
//   deliberately naive reference model over randomised patterns.
// Assumptions: Tombstone semantics are 1 = deleted. Physical slots are appended in order and never reused.
// Design Considerations: The interesting tests here are the cross-checks. The production translation uses a Fenwick
//   tree over per-word alive counts plus a bit-selection step, which is fast but not obviously correct by reading;
//   the reference model is a linear scan that is obviously correct and far too slow to ship. Agreeing with it over
//   thousands of randomised deletion patterns, including patterns that straddle word boundaries and force the tree to
//   grow, is much stronger evidence than any number of hand-picked cases.
//   Row counts are chosen to push past the prefix index's initial capacity (8 words, 512 rows), because a rebuild on
//   growth is exactly the kind of step that silently corrupts a derived index.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

using ColumnStore.Data.Storage;
using Xunit;

namespace ColumnStore.Data.Tests.Storage
{
    /// <summary>
    /// Tests for <see cref="RowDeletionTracker"/>.
    /// </summary>
    public sealed class RowDeletionTrackerTests
    {
        #region Public Methods
        /// <summary>
        /// With nothing deleted, logical and physical numbering are the same thing.
        /// </summary>
        [Fact]
        public void TranslationWithNoDeletionsIsIdentity()
        {
            RowDeletionTracker tracker = BuildTracker(200);
            Assert.Equal(200, tracker.LogicalCount);
            Assert.Equal(200, tracker.PhysicalCount);
            Assert.Equal(0, tracker.DeletedCount);
            for (Int32 index = 0; index < 200; index++)
            {
                Assert.Equal(index, tracker.ToPhysicalIndex(index));
                Assert.Equal(index, tracker.ToLogicalIndex(index));
            }
        }

        /// <summary>
        /// A single deletion at the start, in the middle and at the end each renumber the remaining rows correctly.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(63)]
        [InlineData(64)]
        [InlineData(99)]
        [InlineData(199)]
        public void TranslationWithOneDeletionSkipsThatSlot(Int32 deletedPhysicalIndex)
        {
            RowDeletionTracker tracker = BuildTracker(200);
            tracker.MarkDeleted(deletedPhysicalIndex);
            Assert.Equal(199, tracker.LogicalCount);
            Assert.Equal(200, tracker.PhysicalCount);
            Assert.True(tracker.IsDeleted(deletedPhysicalIndex));
            List<Int32> expectedPhysicalIndices = BuildAlivePhysicalIndices(200, new HashSet<Int32> { deletedPhysicalIndex });
            AssertTranslationMatches(tracker, expectedPhysicalIndices);
        }

        /// <summary>
        /// Consecutive deletions straddling a word boundary - the case a per-word count is most likely to get wrong.
        /// </summary>
        [Fact]
        public void TranslationWithConsecutiveDeletionsAcrossAWordBoundaryStaysCorrect()
        {
            RowDeletionTracker tracker = BuildTracker(200);
            HashSet<Int32> deleted = new HashSet<Int32>();
            for (Int32 physicalIndex = 60; physicalIndex <= 67; physicalIndex++)
            {
                tracker.MarkDeleted(physicalIndex);
                deleted.Add(physicalIndex);
            }
            Assert.Equal(192, tracker.LogicalCount);
            List<Int32> expectedPhysicalIndices = BuildAlivePhysicalIndices(200, deleted);
            AssertTranslationMatches(tracker, expectedPhysicalIndices);
        }

        /// <summary>
        /// An entire word of deleted rows is skipped correctly - the word-skipping shortcut in the enumeration
        /// primitive must not step over the wrong number of rows.
        /// </summary>
        [Fact]
        public void TranslationWithAWholeWordDeletedSkipsTheWord()
        {
            RowDeletionTracker tracker = BuildTracker(200);
            HashSet<Int32> deleted = new HashSet<Int32>();
            for (Int32 physicalIndex = 64; physicalIndex < 128; physicalIndex++)
            {
                tracker.MarkDeleted(physicalIndex);
                deleted.Add(physicalIndex);
            }
            Assert.Equal(136, tracker.LogicalCount);
            List<Int32> expectedPhysicalIndices = BuildAlivePhysicalIndices(200, deleted);
            AssertTranslationMatches(tracker, expectedPhysicalIndices);
            Assert.Equal(63, tracker.ToPhysicalIndex(63));
            Assert.Equal(128, tracker.ToPhysicalIndex(64));
        }

        /// <summary>
        /// Randomised deletion patterns over row counts large enough to grow the prefix index agree with a naive
        /// reference model, in both directions and for every index.
        /// </summary>
        [Theory]
        [InlineData(1, 64, 3)]
        [InlineData(2, 200, 40)]
        [InlineData(3, 513, 100)]
        [InlineData(4, 1024, 512)]
        [InlineData(5, 5000, 2500)]
        [InlineData(6, 5000, 4999)]
        public void TranslationAgainstNaiveReferenceModelAgrees(Int32 randomSeed, Int32 rowCount, Int32 deletionCount)
        {
            RowDeletionTracker tracker = BuildTracker(rowCount);
            Random random = new Random(randomSeed);
            HashSet<Int32> deleted = new HashSet<Int32>();
            while (deleted.Count < deletionCount)
            {
                Int32 candidate = random.Next(rowCount);
                Boolean added = deleted.Add(candidate);
                if (added) { tracker.MarkDeleted(candidate); }
            }
            Assert.Equal(rowCount - deletionCount, tracker.LogicalCount);
            List<Int32> expectedPhysicalIndices = BuildAlivePhysicalIndices(rowCount, deleted);
            AssertTranslationMatches(tracker, expectedPhysicalIndices);
            AssertEnumerationMatches(tracker, expectedPhysicalIndices);
        }

        /// <summary>
        /// Deleting a slot twice is a no-op, not a double decrement of the visible row count. This is the documented
        /// resolution of the design's open behaviour for repeated deletion.
        /// </summary>
        [Fact]
        public void MarkDeletedTwiceIsIdempotent()
        {
            RowDeletionTracker tracker = BuildTracker(10);
            tracker.MarkDeleted(4);
            tracker.MarkDeleted(4);
            tracker.MarkDeleted(4);
            Assert.Equal(9, tracker.LogicalCount);
            Assert.Equal(1, tracker.DeletedCount);
        }

        /// <summary>
        /// A discarded row consumes its slot and is invisible from the moment it is appended.
        /// </summary>
        [Fact]
        public void AppendDetachedRowConsumesTheSlotAndStaysInvisible()
        {
            RowDeletionTracker tracker = new RowDeletionTracker();
            tracker.AppendAliveRow();
            Int32 discardedIndex = tracker.AppendDetachedRow();
            Int32 nextIndex = tracker.AppendAliveRow();
            Assert.Equal(1, discardedIndex);
            Assert.Equal(2, nextIndex);
            Assert.Equal(3, tracker.PhysicalCount);
            Assert.Equal(2, tracker.LogicalCount);
            Assert.True(tracker.IsDeleted(discardedIndex));
            Assert.Equal(0, tracker.ToPhysicalIndex(0));
            Assert.Equal(2, tracker.ToPhysicalIndex(1));
        }

        /// <summary>
        /// New rows always take a fresh physical slot, never a deleted one (03_Design section 1.5).
        /// </summary>
        [Fact]
        public void AppendAliveRowAfterDeletionsNeverReusesASlot()
        {
            RowDeletionTracker tracker = BuildTracker(5);
            tracker.MarkDeleted(1);
            tracker.MarkDeleted(3);
            Int32 appendedIndex = tracker.AppendAliveRow();
            Assert.Equal(5, appendedIndex);
            Assert.Equal(6, tracker.PhysicalCount);
            Assert.Equal(4, tracker.LogicalCount);
        }

        /// <summary>
        /// IsDeleted treats slots outside the table as not deleted rather than throwing, so callers holding a stale
        /// index get a usable answer.
        /// </summary>
        [Fact]
        public void IsDeletedOutsideTheTableReturnsFalse()
        {
            RowDeletionTracker tracker = BuildTracker(4);
            Assert.False(tracker.IsDeleted(-1));
            Assert.False(tracker.IsDeleted(4));
            Assert.False(tracker.IsDeleted(1_000_000));
        }

        /// <summary>
        /// The enumeration primitive reports exhaustion at the end rather than running off the last word.
        /// </summary>
        [Fact]
        public void TryGetNextAlivePhysicalIndexAtTheEndReportsExhaustion()
        {
            RowDeletionTracker tracker = BuildTracker(70);
            tracker.MarkDeleted(69);
            Int32 foundIndex = 0;
            Boolean found = tracker.TryGetNextAlivePhysicalIndex(69, out foundIndex);
            Assert.False(found);
            Assert.Equal(-1, foundIndex);
            found = tracker.TryGetNextAlivePhysicalIndex(68, out foundIndex);
            Assert.True(found);
            Assert.Equal(68, foundIndex);
        }

        /// <summary>
        /// Clear returns the tracker to its initial state, including the derived prefix index - a stale prefix would
        /// mistranslate every subsequent row.
        /// </summary>
        [Fact]
        public void ClearResetsEverythingIncludingTheDerivedIndex()
        {
            RowDeletionTracker tracker = BuildTracker(1000);
            for (Int32 physicalIndex = 0; physicalIndex < 1000; physicalIndex = physicalIndex + 3)
            {
                tracker.MarkDeleted(physicalIndex);
            }
            tracker.Clear();
            Assert.Equal(0, tracker.PhysicalCount);
            Assert.Equal(0, tracker.LogicalCount);
            Assert.Equal(0, tracker.DeletedCount);
            for (Int32 rowIndex = 0; rowIndex < 700; rowIndex++)
            {
                tracker.AppendAliveRow();
            }
            Assert.Equal(700, tracker.LogicalCount);
            for (Int32 rowIndex = 0; rowIndex < 700; rowIndex++)
            {
                Assert.Equal(rowIndex, tracker.ToPhysicalIndex(rowIndex));
            }
        }
        #endregion

        #region Private Methods
        // Builds a tracker holding the given number of visible rows and nothing deleted.
        private static RowDeletionTracker BuildTracker(Int32 rowCount)
        {
            RowDeletionTracker tracker = new RowDeletionTracker();
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                tracker.AppendAliveRow();
            }
            return tracker;
        }

        // The naive reference model: the physical slots that remain visible, in order, worked out by simple scanning.
        // Obviously correct, far too slow to ship, and therefore the ideal thing to compare the fast path against.
        private static List<Int32> BuildAlivePhysicalIndices(Int32 rowCount, HashSet<Int32> deletedPhysicalIndices)
        {
            List<Int32> alive = new List<Int32>();
            for (Int32 physicalIndex = 0; physicalIndex < rowCount; physicalIndex++)
            {
                Boolean isDeleted = deletedPhysicalIndices.Contains(physicalIndex);
                if (isDeleted == false) { alive.Add(physicalIndex); }
            }
            return alive;
        }

        // Asserts that the tracker's translation agrees with the reference model in both directions, for every
        // logical position.
        private static void AssertTranslationMatches(RowDeletionTracker tracker, List<Int32> expectedPhysicalIndices)
        {
            Assert.Equal(expectedPhysicalIndices.Count, tracker.LogicalCount);
            for (Int32 logicalIndex = 0; logicalIndex < expectedPhysicalIndices.Count; logicalIndex++)
            {
                Int32 expectedPhysicalIndex = expectedPhysicalIndices[logicalIndex];
                Int32 actualPhysicalIndex = tracker.ToPhysicalIndex(logicalIndex);
                Assert.Equal(expectedPhysicalIndex, actualPhysicalIndex);
                Int32 roundTrippedLogicalIndex = tracker.ToLogicalIndex(actualPhysicalIndex);
                Assert.Equal(logicalIndex, roundTrippedLogicalIndex);
            }
        }

        // Asserts that walking physical slots yields exactly the visible rows, in order - the path the row
        // enumerator uses.
        private static void AssertEnumerationMatches(RowDeletionTracker tracker, List<Int32> expectedPhysicalIndices)
        {
            Int32 searchIndex = 0;
            for (Int32 position = 0; position < expectedPhysicalIndices.Count; position++)
            {
                Int32 foundIndex = 0;
                Boolean found = tracker.TryGetNextAlivePhysicalIndex(searchIndex, out foundIndex);
                Assert.True(found, $"Enumeration stopped early at visible row {position}.");
                Assert.Equal(expectedPhysicalIndices[position], foundIndex);
                searchIndex = foundIndex + 1;
            }
            Int32 pastTheEnd = 0;
            Boolean anyLeft = tracker.TryGetNextAlivePhysicalIndex(searchIndex, out pastTheEnd);
            Assert.False(anyLeft);
        }
        #endregion
    }
}
