///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies logical deletion end to end through the public API (03_Design.md section 6.4): the count drops,
//   iteration and indexing renumber around the gap, other rows' data is untouched, and new rows never land on a
//   deleted slot.
// Assumptions: Deletion is logical. No caller-visible behaviour should depend on that - these tests are written the
//   way a consumer would write them, from the outside.
// Design Considerations: The deletion patterns are the ones named in the design plus a randomised cross-check that
//   compares the table against a plain List<T> model. A columnar store's characteristic deletion bug is a row whose
//   index survives but whose cell values shift by one, so every check verifies the payload as well as the count.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

using Xunit;

namespace ColumnStore.Data.Tests.Rows
{
    /// <summary>
    /// Deletion behaviour observed through <see cref="DataTable"/> and <see cref="DataRowCollection"/>.
    /// </summary>
    public sealed class RowDeletionIntegrationTests
    {
        #region Public Methods
        /// <summary>
        /// Deleting a row at the start, in the middle or at the end drops the count by one and renumbers the rest,
        /// with every surviving row's payload intact.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(63)]
        [InlineData(64)]
        [InlineData(99)]
        public void DeleteAtAnyPositionRenumbersAndPreservesPayload(Int32 deletedLogicalIndex)
        {
            DataTable table = BuildTable(100);
            List<Int32> expectedIds = BuildExpectedIds(100);
            DataRow doomed = table.Rows[deletedLogicalIndex];
            table.Rows.Delete(doomed);
            expectedIds.RemoveAt(deletedLogicalIndex);
            AssertTableMatches(table, expectedIds);
            Assert.True(table.Rows.IsDeleted(doomed.RowIndex));
            Assert.Equal(-1, table.Rows.IndexOf(doomed));
        }

        /// <summary>
        /// A run of consecutive deletions spanning an internal storage boundary leaves indexing and iteration
        /// correct - the case the design calls out explicitly.
        /// </summary>
        [Fact]
        public void DeleteConsecutiveRunAcrossAStorageBoundaryStaysCorrect()
        {
            DataTable table = BuildTable(200);
            List<Int32> expectedIds = BuildExpectedIds(200);
            for (Int32 id = 60; id <= 70; id++)
            {
                DataRow row = FindRowById(table, id);
                table.Rows.Delete(row);
                expectedIds.Remove(id);
            }
            AssertTableMatches(table, expectedIds);
        }

        /// <summary>
        /// Deleting every row leaves an empty but usable table.
        /// </summary>
        [Fact]
        public void DeleteEveryRowLeavesAnEmptyUsableTable()
        {
            DataTable table = BuildTable(70);
            while (table.Rows.Count > 0)
            {
                DataRow row = table.Rows[0];
                table.Rows.Delete(row);
            }
            Assert.Equal(0, table.Rows.Count);
            Assert.Equal(70, table.Rows.PhysicalCount);
            Assert.Equal(70, table.Rows.DeletedCount);
            List<Int32> visited = CollectIds(table);
            Assert.Empty(visited);
            DataRow added = table.Rows.Add(999, "after");
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(70, added.RowIndex);
            Assert.Equal(999, table.Rows[0].Get<Int32>("Id"));
        }

        /// <summary>
        /// Deleting the same row twice is a no-op, which is the documented resolution of the design's open question
        /// on repeated deletion.
        /// </summary>
        [Fact]
        public void DeleteTwiceIsANoOp()
        {
            DataTable table = BuildTable(5);
            DataRow row = table.Rows[2];
            table.Rows.Delete(row);
            table.Rows.Delete(row);
            table.Rows.Delete(row);
            Assert.Equal(4, table.Rows.Count);
            Assert.Equal(1, table.Rows.DeletedCount);
        }

        /// <summary>
        /// A row added after deletions takes a fresh physical slot, never a deleted one - so a stale handle to a
        /// deleted row can never point at a live one.
        /// </summary>
        [Fact]
        public void AddAfterDeletionsNeverReusesADeletedSlot()
        {
            DataTable table = BuildTable(10);
            DataRow deletedFirst = table.Rows[0];
            DataRow deletedMiddle = table.Rows[5];
            table.Rows.Delete(deletedFirst);
            table.Rows.Delete(deletedMiddle);
            DataRow appended = table.Rows.Add(100, "new");
            Assert.Equal(10, appended.RowIndex);
            Assert.NotEqual(deletedFirst.RowIndex, appended.RowIndex);
            Assert.NotEqual(deletedMiddle.RowIndex, appended.RowIndex);
            Assert.Equal(9, table.Rows.Count);
            Assert.Equal(100, table.Rows[8].Get<Int32>("Id"));
        }

        /// <summary>
        /// The compatibility aliases behave exactly like Delete.
        /// </summary>
        [Fact]
        public void RemoveAndRemoveAtBehaveLikeDelete()
        {
            DataTable table = BuildTable(5);
            DataRow second = table.Rows[1];
            table.Rows.Remove(second);
            Assert.Equal(4, table.Rows.Count);
            table.Rows.RemoveAt(0);
            Assert.Equal(3, table.Rows.Count);
            Assert.Equal(2, table.Rows[0].Get<Int32>("Id"));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Rows.RemoveAt(3));
        }

        /// <summary>
        /// Randomised deletion patterns leave the table agreeing with a plain list model, in count, in iteration
        /// order, and in every cell of every surviving row.
        /// </summary>
        [Theory]
        [InlineData(1, 100, 30)]
        [InlineData(2, 300, 200)]
        [InlineData(3, 600, 599)]
        [InlineData(4, 1000, 500)]
        public void DeleteRandomPatternsAgreeWithAListModel(Int32 randomSeed, Int32 rowCount, Int32 deletionCount)
        {
            DataTable table = BuildTable(rowCount);
            List<Int32> expectedIds = BuildExpectedIds(rowCount);
            Random random = new Random(randomSeed);
            for (Int32 deletion = 0; deletion < deletionCount; deletion++)
            {
                Int32 logicalIndex = random.Next(expectedIds.Count);
                DataRow row = table.Rows[logicalIndex];
                Assert.Equal(expectedIds[logicalIndex], row.Get<Int32>("Id"));
                table.Rows.Delete(row);
                expectedIds.RemoveAt(logicalIndex);
            }
            AssertTableMatches(table, expectedIds);
        }

        /// <summary>
        /// Interleaving deletions with new rows keeps both the visible order and the payloads correct - the pattern
        /// that would break a translation index cached without invalidation.
        /// </summary>
        [Fact]
        public void DeleteInterleavedWithAddsStaysCorrect()
        {
            DataTable table = BuildTable(50);
            List<Int32> expectedIds = BuildExpectedIds(50);
            Random random = new Random(7);
            for (Int32 step = 0; step < 200; step++)
            {
                Boolean shouldDelete = random.Next(2) == 0 && expectedIds.Count > 0;
                if (shouldDelete)
                {
                    Int32 logicalIndex = random.Next(expectedIds.Count);
                    DataRow row = table.Rows[logicalIndex];
                    table.Rows.Delete(row);
                    expectedIds.RemoveAt(logicalIndex);
                }
                else
                {
                    Int32 newId = 1000 + step;
                    table.Rows.Add(newId, "row" + newId);
                    expectedIds.Add(newId);
                }
            }
            AssertTableMatches(table, expectedIds);
        }

        /// <summary>
        /// Clear wipes tombstones along with the rows, so the table restarts from physical slot zero with no
        /// leftover deletion state.
        /// </summary>
        [Fact]
        public void ClearAfterDeletionsResetsTombstonesToo()
        {
            DataTable table = BuildTable(100);
            for (Int32 logicalIndex = 0; logicalIndex < 50; logicalIndex++)
            {
                DataRow row = table.Rows[0];
                table.Rows.Delete(row);
            }
            table.Clear();
            Assert.Equal(0, table.Rows.DeletedCount);
            Assert.Equal(0, table.Rows.PhysicalCount);
            for (Int32 id = 0; id < 100; id++)
            {
                table.Rows.Add(id, "row" + id);
            }
            List<Int32> expectedIds = BuildExpectedIds(100);
            AssertTableMatches(table, expectedIds);
        }
        #endregion

        #region Private Methods
        // Builds a two-column table of sequential rows.
        private static DataTable BuildTable(Int32 rowCount)
        {
            DataTable table = new DataTable("Rows");
            table.Columns.Add<Int32>("Id", false);
            table.Columns.Add<String>("Name");
            for (Int32 id = 0; id < rowCount; id++)
            {
                table.Rows.Add(id, "row" + id);
            }
            return table;
        }

        // The list model the table is compared against: the ids that should still be visible, in order.
        private static List<Int32> BuildExpectedIds(Int32 rowCount)
        {
            List<Int32> ids = new List<Int32>(rowCount);
            for (Int32 id = 0; id < rowCount; id++)
            {
                ids.Add(id);
            }
            return ids;
        }

        // Asserts the table and the model agree on count, on indexed access, on iteration order and on payload -
        // checking the name column too, so a value shifted by one row cannot pass.
        private static void AssertTableMatches(DataTable table, List<Int32> expectedIds)
        {
            Assert.Equal(expectedIds.Count, table.Rows.Count);
            Assert.Equal(expectedIds.Count, table.Count);
            for (Int32 logicalIndex = 0; logicalIndex < expectedIds.Count; logicalIndex++)
            {
                DataRow row = table.Rows[logicalIndex];
                Int32 expectedId = expectedIds[logicalIndex];
                Assert.Equal(expectedId, row.Get<Int32>("Id"));
                Assert.Equal("row" + expectedId, row.Get<String>("Name"));
                Assert.Equal(logicalIndex, table.Rows.IndexOf(row));
            }
            List<Int32> visitedIds = CollectIds(table);
            Assert.Equal(expectedIds, visitedIds);
        }

        // Collects the id of every visible row in iteration order.
        private static List<Int32> CollectIds(DataTable table)
        {
            List<Int32> ids = new List<Int32>();
            foreach (DataRow row in table.Rows)
            {
                Int32 id = row.Get<Int32>("Id");
                ids.Add(id);
            }
            return ids;
        }

        // Finds the visible row carrying a given id, for tests that delete by payload rather than by position.
        private static DataRow FindRowById(DataTable table, Int32 id)
        {
            foreach (DataRow row in table.Rows)
            {
                Int32 candidateId = row.Get<Int32>("Id");
                if (candidateId == id) { return row; }
            }
            throw new InvalidOperationException($"No visible row carries id {id}.");
        }
        #endregion
    }
}
