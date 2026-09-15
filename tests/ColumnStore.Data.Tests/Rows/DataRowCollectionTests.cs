///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: The highest-priority correctness area in the library (03_Design.md section 6.3): the row lifecycle, and in
//   particular the commit/discard protocol that FR-7 and NFR-10 exist to protect. These tests define the contract.
// Assumptions: DataRow.RowIndex is a physical storage slot. Several tests assert on specific slot numbers, which is
//   legitimate here because "physical slots are appended and never reused" is itself part of the contract.
// Design Considerations: The hazard tests are written as the primary tests, not as an afterthought. The dangerous
//   property of a columnar store with a two-step row creation flow is that a field write lands in storage before the
//   row is part of the table, so the failure mode to rule out is "data readable but not counted" or "counted but not
//   valid". Each test below names the specific hazard it forecloses.
//   THE DETACHED ROW TESTS ARE THE ORDER TESTS. Any number of rows may be detached at once, and a row's position is
//   its storage slot's position, so rows appear in the order NewRow CREATED them where System.Data orders by the
//   order they were ADDED. Those coincide while nothing else joins the table between creating a row and adding it,
//   and AddingDetachedRowsOutOfCreationOrderIsRefused is the test that keeps every other case loud: the day it stops
//   throwing is the day a ported application silently reorders its rows.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Data;

using Xunit;

namespace ColumnStore.Data.Tests.Rows
{
    /// <summary>
    /// Tests for <see cref="DataRowCollection"/>.
    /// </summary>
    public sealed class DataRowCollectionTests
    {
        #region Public Methods
        /// <summary>
        /// AddNewRow appends a counted row whose fields can then be written and read back at the right position.
        /// </summary>
        [Fact]
        public void AddNewRowThenSetMakesValuesReadableAtTheRightRow()
        {
            DataTable table = BuildTable();
            DataRow first = table.Rows.AddNewRow();
            first.Set<Int32>("Id", 1);
            first.Set<String>("Name", "first");
            DataRow second = table.Rows.AddNewRow();
            second.Set<Int32>("Id", 2);
            second.Set<String>("Name", "second");
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(1, table.Rows[0].Get<Int32>("Id"));
            Assert.Equal("first", table.Rows[0].Get<String>("Name"));
            Assert.Equal(2, table.Rows[1].Get<Int32>("Id"));
            Assert.Equal("second", table.Rows[1].Get<String>("Name"));
        }

        /// <summary>
        /// The recommended path - Rows.Add with positional values - populates, commits and counts in one call.
        /// </summary>
        [Fact]
        public void AddWithValuesPopulatesPositionallyAndCommitsAtomically()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.Add(7, "widget", 12.5m, true);
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(0, row.RowIndex);
            Assert.Equal(7, row.Get<Int32>("Id"));
            Assert.Equal("widget", row.Get<String>("Name"));
            Assert.Equal(12.5m, row.Get<Decimal>("Price"));
            Assert.True(row.Get<Boolean>("Active"));
        }

        /// <summary>
        /// Fewer values than columns is allowed; the cells not supplied stay null for nullable columns.
        /// </summary>
        [Fact]
        public void AddWithFewerValuesThanColumnsLeavesTheRestNull()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.Add(7, "widget");
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(7, row.Get<Int32>("Id"));
            Assert.True(row.IsNull("Price"));
            Assert.Equal(DBNull.Value, row["Price"]);
        }

        /// <summary>
        /// A null in the value array sets that cell to null, and DBNull means the same thing.
        /// </summary>
        [Fact]
        public void AddWithValuesAcceptsNullAndDBNull()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.Add(7, null, DBNull.Value, false);
            Assert.True(row.IsNull("Name"));
            Assert.True(row.IsNull("Price"));
            Assert.False(row.Get<Boolean>("Active"));
        }

        /// <summary>
        /// More values than columns is a caller error and is refused before the row is appended.
        /// </summary>
        [Fact]
        public void AddWithTooManyValuesThrowsAndAddsNoRow()
        {
            DataTable table = BuildTable();
            Assert.Throws<ArgumentException>(() => table.Rows.Add(1, "a", 2m, true, "extra"));
            Assert.Equal(0, table.Rows.Count);
            Assert.Equal(0, table.Rows.PhysicalCount);
        }

        /// <summary>
        /// Any number of rows may be detached at once, each with its own storage slot, and each readable and writable
        /// while detached without any of them being part of the table. This is the System.Data behaviour the
        /// one-reserved-row-at-a-time rule used to refuse.
        /// </summary>
        [Fact]
        public void ManyRowsCanBeDetachedAtOnceAndAddedLater()
        {
            DataTable table = BuildTable();
            DataRow[] detachedRows = new DataRow[64];
            for (Int32 rowNumber = 0; rowNumber < detachedRows.Length; rowNumber++)
            {
                detachedRows[rowNumber] = table.Rows.NewRow();
                detachedRows[rowNumber].Set<Int32>("Id", rowNumber);
                detachedRows[rowNumber].Set<String>("Name", "row" + rowNumber);
            }
            Assert.Equal(0, table.Rows.Count);
            Assert.Equal(64, table.Rows.DetachedRowCount);
            Assert.True(table.Rows.HasDetachedRows);
            // Each one kept its own slot: reading any of them back gives that row's values, not another's.
            Assert.Equal(7, detachedRows[7].Get<Int32>("Id"));
            Assert.Equal("row63", detachedRows[63].Get<String>("Name"));

            for (Int32 rowNumber = 0; rowNumber < detachedRows.Length; rowNumber++)
            {
                table.Rows.Add(detachedRows[rowNumber]);
            }
            Assert.Equal(64, table.Rows.Count);
            Assert.False(table.Rows.HasDetachedRows);
            Assert.Equal(AscendingIds(64), CollectIds(table));
        }

        /// <summary>
        /// Appending directly while rows are detached is allowed and orders correctly: a detached row holds a slot
        /// reserved when it was created, so a later direct Add lands after it, and adding the detached row before
        /// that direct Add keeps both in creation order.
        /// </summary>
        [Fact]
        public void DirectAddsInterleaveWithDetachedRows()
        {
            DataTable table = BuildTable();
            DataRow detached = table.Rows.NewRow();
            detached.Set<Int32>("Id", 1);
            table.Rows.Add(detached);
            table.Rows.Add(2, "direct");
            DataRow later = table.Rows.NewRow();
            later.Set<Int32>("Id", 3);
            table.Rows.Add(later);
            table.Rows.AddNewRow().Set<Int32>("Id", 4);
            Assert.Equal(new List<Int32> { 1, 2, 3, 4 }, CollectIds(table));
        }

        /// <summary>
        /// HAZARD, AND THE ONE DIVERGENCE FROM System.Data. A row's position is its slot's position, so rows appear
        /// in creation order; System.Data uses Add order. Adding an older detached row after a newer row is already
        /// in the table would therefore place it ahead of that row instead of at the end. That is refused rather than
        /// silently misordered - the table is left exactly as it was, and the row stays detached and addable once the
        /// caller reorders. Both System.Data orderings are reachable: add in creation order, or copy the values.
        /// </summary>
        [Fact]
        public void AddingDetachedRowsOutOfCreationOrderIsRefused()
        {
            DataTable table = BuildTable();
            DataRow first = table.Rows.NewRow();
            first.Set<Int32>("Id", 1);
            DataRow second = table.Rows.NewRow();
            second.Set<Int32>("Id", 2);

            table.Rows.Add(second);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => table.Rows.Add(first));
            Assert.Contains("order", failure.Message);
            Assert.Contains("AddAtEnd", failure.Message);

            // Nothing moved, and nothing was half-added.
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(2, table.Rows[0].Get<Int32>("Id"));
            Assert.Equal(1, table.Rows.DetachedRowCount);
            Assert.Equal(DataRowState.Detached, first.RowState);

            // The way out named in the message works, appends at the end, and hands back the row that is actually in
            // the table - the argument stays detached and abandoned.
            DataRow relocated = table.Rows.AddAtEnd(first);
            Assert.Equal(new List<Int32> { 2, 1 }, CollectIds(table));
            Assert.Equal(1, relocated.Get<Int32>("Id"));
            Assert.Equal(1, table.Rows.IndexOf(relocated));
            Assert.Equal(0, table.Rows.DetachedRowCount);
        }

        /// <summary>
        /// Adding a SUBSEQUENCE of the detached rows - create several, add some, abandon the rest - is the common
        /// conditional-build pattern and gives exactly System.Data's order, because the rows that are added are still
        /// added in creation order.
        /// </summary>
        [Fact]
        public void AddingOnlySomeOfTheDetachedRowsKeepsTheirRelativeOrder()
        {
            DataTable table = BuildTable();
            DataRow[] candidates = new DataRow[6];
            for (Int32 rowNumber = 0; rowNumber < candidates.Length; rowNumber++)
            {
                candidates[rowNumber] = table.Rows.NewRow();
                candidates[rowNumber].Set<Int32>("Id", rowNumber);
            }
            for (Int32 rowNumber = 0; rowNumber < candidates.Length; rowNumber++)
            {
                if (rowNumber % 2 == 0) { table.Rows.Add(candidates[rowNumber]); }
            }
            Assert.Equal(new List<Int32> { 0, 2, 4 }, CollectIds(table));
            Assert.Equal(3, table.Rows.DetachedRowCount);
        }

        /// <summary>
        /// A detached row that is simply never added, and never discarded either, stays invisible for good. It costs
        /// its storage slot - which is why DetachedRowCount exists to be watched - and nothing else.
        /// </summary>
        [Fact]
        public void DetachedRowsThatAreNeverAddedStayInvisible()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "kept");
            for (Int32 abandoned = 0; abandoned < 10; abandoned++)
            {
                DataRow throwaway = table.Rows.NewRow();
                throwaway.Set<Int32>("Id", 900 + abandoned);
            }
            table.Rows.Add(2, "also kept");
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(12, table.Rows.PhysicalCount);
            Assert.Equal(10, table.Rows.DetachedRowCount);
            Assert.Equal(new List<Int32> { 1, 2 }, CollectIds(table));
        }

        /// <summary>
        /// The slot a detached row occupies is the slot it keeps, so a handle taken before Add still addresses the
        /// right row afterwards - including the strongly typed column handle written against its RowIndex, which is
        /// the whole reason detached rows are stored in real columns rather than a boxed side buffer.
        /// </summary>
        [Fact]
        public void ADetachedRowKeepsItsSlotAndItsHandleAcrossAdd()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "first");
            DataRow detached = table.Rows.NewRow();
            DataColumn<Int32> idColumn = table.Columns.GetColumn<Int32>("Id");
            idColumn.Set(detached.RowIndex, 2);
            Int32 slotBeforeAdd = detached.RowIndex;

            table.Rows.Add(detached);
            Assert.Equal(slotBeforeAdd, detached.RowIndex);
            Assert.Equal(2, table.Rows[1].Get<Int32>("Id"));

            // And the handle still writes to the right row now that it is part of the table.
            detached.Set<String>("Name", "written-after-add");
            Assert.Equal("written-after-add", table.Rows[1].Get<String>("Name"));
            Assert.Equal(DataRowState.Unchanged, detached.RowState);
        }

        /// <summary>
        /// A detached row is not counted until added, and its already-written fields are not reachable through any
        /// public path in the meantime - "readable but not counted" is exactly the state that must not exist.
        /// </summary>
        [Fact]
        public void ADetachedRowIsNeitherCountedNorReachable()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "committed");
            DataRow detached = table.Rows.NewRow();
            detached.Set<Int32>("Id", 99);
            Assert.Equal(1, table.Rows.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Rows[1]);
            List<Int32> visitedIds = CollectIds(table);
            Assert.Equal(new List<Int32> { 1 }, visitedIds);
            Assert.Equal(-1, table.Rows.IndexOf(detached));
            Assert.Equal(DataRowState.Detached, detached.RowState);
        }

        /// <summary>
        /// Committing a reserved row counts it and makes its already-written fields visible at the right position -
        /// "counted but not valid" must not exist either.
        /// </summary>
        [Fact]
        public void NewRowThenAddCommitsTheAlreadyWrittenFields()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "first");
            DataRow detached = table.Rows.NewRow();
            detached.Set<Int32>("Id", 2);
            detached.Set<String>("Name", "second");
            table.Rows.Add(detached);
            Assert.Equal(2, table.Rows.Count);
            Assert.False(table.Rows.HasDetachedRows);
            Assert.Equal(2, table.Rows[1].Get<Int32>("Id"));
            Assert.Equal("second", table.Rows[1].Get<String>("Name"));
            Assert.Equal(1, table.Rows.IndexOf(detached));
        }

        /// <summary>
        /// Discarding a reserved row leaves the count alone, hides the slot for good, and does not hand that slot to
        /// the next row - reuse would let a stale row handle write into an unrelated row later.
        /// </summary>
        [Fact]
        public void NewRowThenDiscardAbandonsTheSlotPermanently()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "first");
            DataRow discarded = table.Rows.NewRow();
            discarded.Set<Int32>("Id", 999);
            table.Rows.Discard(discarded);
            Assert.Equal(1, table.Rows.Count);
            Assert.False(table.Rows.HasDetachedRows);
            Assert.True(table.Rows.IsDeleted(discarded.RowIndex));
            DataRow next = table.Rows.AddNewRow();
            Assert.NotEqual(discarded.RowIndex, next.RowIndex);
            Assert.Equal(2, next.RowIndex);
            next.Set<Int32>("Id", 2);
            List<Int32> visitedIds = CollectIds(table);
            Assert.Equal(new List<Int32> { 1, 2 }, visitedIds);
        }

        /// <summary>
        /// HAZARD: adding or discarding a row that is not detached would revive or abandon the wrong slot. A row that
        /// is already in the table, and a handle from a completed NewRow cycle, are both refused - and the two say
        /// different things, because they are different mistakes.
        /// </summary>
        [Fact]
        public void AddOrDiscardOfARowThatIsNotDetachedThrows()
        {
            DataTable table = BuildTable();
            DataRow committed = table.Rows.Add(1, "first");
            InvalidOperationException doubleAdd = Assert.Throws<InvalidOperationException>(() => table.Rows.Add(committed));
            Assert.Contains("already part of the table", doubleAdd.Message);
            Assert.Throws<InvalidOperationException>(() => table.Rows.Discard(committed));

            DataRow detached = table.Rows.NewRow();
            table.Rows.Add(detached);
            InvalidOperationException addedTwice = Assert.Throws<InvalidOperationException>(() => table.Rows.Add(detached));
            Assert.Contains("already part of the table", addedTwice.Message);

            DataRow discarded = table.Rows.NewRow();
            table.Rows.Discard(discarded);
            InvalidOperationException afterDiscard = Assert.Throws<InvalidOperationException>(() => table.Rows.Add(discarded));
            Assert.Contains("already added or discarded", afterDiscard.Message);
            Assert.Equal(2, table.Rows.Count);
        }

        /// <summary>
        /// Clear releases detached rows along with everything else, so a handle created before it is no longer a row
        /// this table will accept. Refused loudly rather than silently reviving a slot that now belongs to a
        /// different row.
        /// </summary>
        [Fact]
        public void ADetachedRowCreatedBeforeClearIsRefusedAfterIt()
        {
            DataTable table = BuildTable();
            DataRow beforeClear = table.Rows.NewRow();
            beforeClear.Set<Int32>("Id", 7);
            table.Rows.Clear();
            Assert.Equal(0, table.Rows.DetachedRowCount);
            Assert.Throws<InvalidOperationException>(() => table.Rows.Add(beforeClear));
            Assert.Equal(0, table.Rows.Count);
        }

        /// <summary>
        /// A row from another table is refused rather than having its index applied to this table's storage.
        /// </summary>
        [Fact]
        public void RowLifecycleOperationsRejectRowsFromAnotherTable()
        {
            DataTable first = BuildTable();
            DataTable second = BuildTable();
            DataRow foreignRow = second.Rows.Add(1, "elsewhere");
            first.Rows.NewRow();
            Assert.Equal(1, first.Rows.DetachedRowCount);
            Assert.Throws<ArgumentException>(() => first.Rows.Add(foreignRow));
            Assert.Throws<ArgumentException>(() => first.Rows.Discard(foreignRow));
            Assert.Throws<ArgumentException>(() => first.Rows.Delete(foreignRow));
            Assert.Equal(-1, first.Rows.IndexOf(foreignRow));
        }

        /// <summary>
        /// An unbound default(DataRow) is refused everywhere rather than dereferencing a null table.
        /// </summary>
        [Fact]
        public void RowLifecycleOperationsRejectAnUnboundRow()
        {
            DataTable table = BuildTable();
            DataRow unbound = default(DataRow);
            Assert.Throws<ArgumentException>(() => table.Rows.Delete(unbound));
            Assert.Equal(-1, table.Rows.IndexOf(unbound));
            Assert.Throws<InvalidOperationException>(() => unbound.Get<Int32>(0));
        }

        /// <summary>
        /// A detached row cannot be deleted - it is not part of the table, so there is nothing to delete and the
        /// caller means Discard. System.Data allows DataRow.Delete() here and does nothing useful with it; refusing
        /// says which of the two operations was meant.
        /// </summary>
        [Fact]
        public void DeleteOnADetachedRowThrowsAndPointsAtDiscard()
        {
            DataTable table = BuildTable();
            DataRow detached = table.Rows.NewRow();
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => table.Rows.Delete(detached));
            Assert.Contains("Discard", failure.Message);
            Assert.Equal(1, table.Rows.DetachedRowCount);
        }

        /// <summary>
        /// Iterating visits exactly the visible rows, in logical order.
        /// </summary>
        [Fact]
        public void EnumerationVisitsEveryVisibleRowInOrder()
        {
            DataTable table = BuildTable();
            for (Int32 id = 0; id < 150; id++)
            {
                table.Rows.Add(id, "row" + id);
            }
            List<Int32> visitedIds = CollectIds(table);
            Assert.Equal(150, visitedIds.Count);
            for (Int32 position = 0; position < visitedIds.Count; position++)
            {
                Assert.Equal(position, visitedIds[position]);
            }
        }

        /// <summary>
        /// The enumerator stays exhausted once it has run out, rather than restarting.
        /// </summary>
        [Fact]
        public void EnumeratorStaysExhausted()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "only");
            DataRowCollection.Enumerator enumerator = table.Rows.GetEnumerator();
            Assert.True(enumerator.MoveNext());
            Assert.False(enumerator.MoveNext());
            Assert.False(enumerator.MoveNext());
            enumerator.Reset();
            Assert.True(enumerator.MoveNext());
            Assert.Equal(1, enumerator.Current.Get<Int32>("Id"));
        }

        /// <summary>
        /// Reading Current before the first MoveNext is a caller error, not a silent row zero.
        /// </summary>
        [Fact]
        public void EnumeratorCurrentBeforeMoveNextThrows()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "only");
            DataRowCollection.Enumerator enumerator = table.Rows.GetEnumerator();
            Assert.Throws<InvalidOperationException>(() => enumerator.Current);
        }

        /// <summary>
        /// The logical indexer rejects positions outside the visible rows.
        /// </summary>
        [Fact]
        public void IndexerRejectsOutOfRangePositions()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "only");
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Rows[-1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Rows[1]);
        }

        /// <summary>
        /// Clear resets the row count, releases detached rows and the column data, and lets the table be refilled
        /// from slot zero.
        /// </summary>
        [Fact]
        public void ClearResetsRowStateAndReleasesData()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "first");
            table.Rows.Add(2, "second");
            table.Rows.NewRow();
            table.Rows.Clear();
            Assert.Equal(0, table.Rows.Count);
            Assert.Equal(0, table.Rows.PhysicalCount);
            Assert.False(table.Rows.HasDetachedRows);
            Assert.Equal(4, table.Columns.Count);
            DataRow refilled = table.Rows.Add(9, "again");
            Assert.Equal(0, refilled.RowIndex);
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(9, table.Rows[0].Get<Int32>("Id"));
        }

        /// <summary>
        /// Clearing does not leave a previous row's values visible in a newly added row - the row-count reset and the
        /// column-data release always happen together.
        /// </summary>
        [Fact]
        public void ClearDoesNotLeaveStaleValuesInNewRows()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "stale", 5m, true);
            table.Rows.Clear();
            DataRow fresh = table.Rows.AddNewRow();
            Assert.True(fresh.IsNull("Name"));
            Assert.True(fresh.IsNull("Price"));
            Assert.True(fresh.IsNull("Id"));
        }
        #endregion

        #region Private Methods
        // A four-column table covering a value type, a reference type, a decimal and a bit-packed Boolean, all
        // nullable so that unpopulated cells are observable as null.
        private static DataTable BuildTable()
        {
            DataTable table = new DataTable("Orders");
            table.Columns.Add<Int32>("Id");
            table.Columns.Add<String>("Name");
            table.Columns.Add<Decimal>("Price");
            table.Columns.Add<Boolean>("Active");
            return table;
        }

        // The Ids 0..count-1, the expected result whenever rows were created and added in the same order.
        private static List<Int32> AscendingIds(Int32 count)
        {
            List<Int32> ids = new List<Int32>(count);
            for (Int32 id = 0; id < count; id++)
            {
                ids.Add(id);
            }
            return ids;
        }

        // Collects the Id of every visible row, in iteration order, using foreach over the concrete collection - the
        // allocation-free path.
        private static List<Int32> CollectIds(DataTable table)
        {
            List<Int32> ids = new List<Int32>();
            foreach (DataRow row in table.Rows)
            {
                Int32 id = row.GetOrDefault<Int32>("Id");
                ids.Add(id);
            }
            return ids;
        }
        #endregion
    }
}
