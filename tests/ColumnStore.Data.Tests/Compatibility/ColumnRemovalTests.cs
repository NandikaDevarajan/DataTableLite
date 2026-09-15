///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies column removal and the schema reshaping that comes with it - Remove, RemoveAt, CanRemove, Clear,
//   SetOrdinal and renaming - against the behaviour System.Data.DataColumnCollection has, which is the behaviour
//   ported code depends on.
// Assumptions: Removal is a schema operation, not a row operation: it never touches the rows that remain.
// Design Considerations: The tests that matter most here are the ones about ORDINALS MOVING, because that is the
//   hazard removal introduces and the reason the schema used to be append-only. A caller who cached an ordinal across
//   a removal now addresses a different column, and no exception will tell them - so the behaviour is pinned
//   explicitly rather than left to be discovered in production.
//   Every behavioural assertion is made against System.Data.DataTable in the same test wherever the two are supposed
//   to agree, so a future change that drifts from System.Data fails here rather than in a caller's code.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using Xunit;

namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// Tests for removing and reordering columns.
    /// </summary>
    public sealed class ColumnRemovalTests
    {
        #region Public Methods
        /// <summary>
        /// Removing a column by name drops it from the schema, and the columns after it move down - exactly as they
        /// do in System.Data.
        /// </summary>
        [Fact]
        public void RemoveByNameDropsTheColumnAndClosesTheGap()
        {
            DataTable lite = BuildTable();
            System.Data.DataTable framework = BuildFrameworkTable();
            lite.Columns.Remove("Name");
            framework.Columns.Remove("Name");
            Assert.Equal(framework.Columns.Count, lite.Columns.Count);
            Assert.Equal(3, lite.Columns.Count);
            Assert.Equal("Id", lite.Columns[0].ColumnName);
            Assert.Equal("Price", lite.Columns[1].ColumnName);
            Assert.Equal("Active", lite.Columns[2].ColumnName);
            Assert.Equal(framework.Columns[1].ColumnName, lite.Columns[1].ColumnName);
            Assert.False(lite.Columns.Contains("Name"));
            Assert.Null(lite.Columns["Name"]);
            Assert.Equal(-1, lite.Columns.IndexOf("Name"));
        }

        /// <summary>
        /// Every surviving column's Ordinal is updated, not just its position in the list - so a caller who reads
        /// Ordinal after a removal gets the truth.
        /// </summary>
        [Fact]
        public void RemoveRenumbersTheSurvivingColumns()
        {
            DataTable table = BuildTable();
            DataColumn price = table.Columns["Price"];
            Assert.Equal(2, price.Ordinal);
            table.Columns.RemoveAt(0);
            Assert.Equal(1, price.Ordinal);
            Assert.Equal(0, table.Columns["Name"].Ordinal);
            Assert.Equal(2, table.Columns["Active"].Ordinal);
            for (Int32 ordinal = 0; ordinal < table.Columns.Count; ordinal++)
            {
                Assert.Equal(ordinal, table.Columns[ordinal].Ordinal);
            }
        }

        /// <summary>
        /// THE HAZARD REMOVAL INTRODUCES, pinned deliberately. A cached ORDINAL silently addresses a different column
        /// after a removal; a cached HANDLE does not. This is why the library's rule is "cache the handle, never the
        /// ordinal", and why every internal consumer re-reads the schema per operation.
        /// </summary>
        [Fact]
        public void CachedHandleSurvivesRemovalButCachedOrdinalDoesNot()
        {
            DataTable table = BuildTable();
            DataColumn<Decimal> price = table.Columns.GetColumn<Decimal>("Price");
            table.Rows.Add(1, "widget", 9.5m, true);
            Int32 cachedOrdinalOfPrice = price.Ordinal;
            table.Columns.Remove("Id");
            // The handle still reads the same cell.
            Assert.Equal(9.5m, price.Get(0));
            // The ordinal now points at a different column entirely, with no error to say so.
            Assert.NotEqual(price, table.Columns[cachedOrdinalOfPrice]);
            Assert.Equal("Active", table.Columns[cachedOrdinalOfPrice].ColumnName);
        }

        /// <summary>
        /// A removed column is detached rather than emptied: it reports no table and no ordinal, but still holds its
        /// values, so pulling a column out of a table and reading it afterwards works.
        /// </summary>
        [Fact]
        public void RemovedColumnIsDetachedButKeepsItsData()
        {
            DataTable table = BuildTable();
            DataColumn<String> name = table.Columns.GetColumn<String>("Name");
            table.Rows.Add(1, "widget", 9.5m, true);
            table.Rows.Add(2, "gadget", 4.0m, false);
            Assert.Same(table, name.Table);
            table.Columns.Remove(name);
            Assert.Null(name.Table);
            Assert.Equal(-1, name.Ordinal);
            Assert.Equal("widget", name.Get(0));
            Assert.Equal("gadget", name.Get(1));
        }

        /// <summary>
        /// The rows that remain are untouched by a removal: the other columns still read the same values at the same
        /// row indices.
        /// </summary>
        [Fact]
        public void RemoveLeavesEveryOtherColumnsDataIntact()
        {
            DataTable table = BuildTable();
            for (Int32 id = 0; id < 10; id++)
            {
                table.Rows.Add(id, "row" + id, id * 1.5m, id % 2 == 0);
            }
            table.Columns.Remove("Name");
            Assert.Equal(10, table.Rows.Count);
            DataColumn<Int32> identifiers = table.Columns.GetColumn<Int32>("Id");
            DataColumn<Decimal> prices = table.Columns.GetColumn<Decimal>("Price");
            for (Int32 rowIndex = 0; rowIndex < 10; rowIndex++)
            {
                Assert.Equal(rowIndex, identifiers.Get(rowIndex));
                Assert.Equal(rowIndex * 1.5m, prices.Get(rowIndex));
            }
        }

        /// <summary>
        /// A column can be removed and a new one added in its place, and the new column's cells start null rather
        /// than carrying the removed column's values.
        /// </summary>
        [Fact]
        public void ColumnAddedAfterARemovalStartsEmpty()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "widget", 9.5m, true);
            table.Columns.Remove("Price");
            table.Columns.Add<Decimal>("Cost", true);
            Assert.True(table.Rows[0].IsNull("Cost"));
            Assert.Equal(DBNull.Value, table.Rows[0]["Cost"]);
        }

        /// <summary>
        /// Removal is rejected with a named error when the column belongs to another table or to none, rather than
        /// silently doing nothing.
        /// </summary>
        [Fact]
        public void RemoveRejectsAColumnThatDoesNotBelongHere()
        {
            DataTable table = BuildTable();
            DataTable other = BuildTable();
            DataColumn foreignColumn = other.Columns["Id"];
            ArgumentException failure = Assert.Throws<ArgumentException>(() => table.Columns.Remove(foreignColumn));
            Assert.Contains("Id", failure.Message);
            Assert.Throws<ArgumentNullException>(() => table.Columns.Remove((DataColumn)null));
            Assert.Throws<ArgumentException>(() => table.Columns.Remove("Missing"));
            Assert.Throws<IndexOutOfRangeException>(() => table.Columns.RemoveAt(99));
        }

        /// <summary>
        /// CanRemove answers for a column of this table, a column of another, and null, without throwing - matching
        /// System.Data, where it is the way to ask before trying.
        /// </summary>
        [Fact]
        public void CanRemoveAnswersWithoutThrowing()
        {
            DataTable table = BuildTable();
            DataTable other = BuildTable();
            Assert.True(table.Columns.CanRemove(table.Columns["Id"]));
            Assert.False(table.Columns.CanRemove(other.Columns["Id"]));
            Assert.False(table.Columns.CanRemove(null));
        }

        /// <summary>
        /// SetOrdinal moves a column and renumbers the rest, and the data moves with the column rather than staying
        /// at the ordinal.
        /// </summary>
        [Fact]
        public void SetOrdinalMovesTheColumnAndItsData()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "widget", 9.5m, true);
            DataColumn price = table.Columns["Price"];
            price.SetOrdinal(0);
            Assert.Equal(0, price.Ordinal);
            Assert.Equal("Price", table.Columns[0].ColumnName);
            Assert.Equal("Id", table.Columns[1].ColumnName);
            Assert.Equal("Name", table.Columns[2].ColumnName);
            Assert.Equal(9.5m, table.Rows[0]["Price"]);
            Assert.Equal(9.5m, table.Rows[0][0]);
            Assert.Equal(1, table.Rows[0][1]);
        }

        /// <summary>
        /// Renaming a column re-keys the name lookup: the new name resolves, the old one no longer does, and the
        /// ordinal is unchanged.
        /// </summary>
        [Fact]
        public void RenamingAColumnRekeysTheNameLookup()
        {
            DataTable table = BuildTable();
            table.Rows.Add(1, "widget", 9.5m, true);
            DataColumn name = table.Columns["Name"];
            name.ColumnName = "Description";
            Assert.Equal(1, name.Ordinal);
            Assert.Same(name, table.Columns["Description"]);
            Assert.Null(table.Columns["Name"]);
            Assert.Equal("widget", table.Rows[0]["Description"]);
            Assert.Throws<ArgumentException>(() => table.Rows[0]["Name"]);
        }

        /// <summary>
        /// A rename that would collide with another column is refused, and the column keeps its original name.
        /// </summary>
        [Fact]
        public void RenamingOntoAnExistingNameIsRefused()
        {
            DataTable table = BuildTable();
            DataColumn name = table.Columns["Name"];
            Assert.Throws<ArgumentException>(() => name.ColumnName = "Price");
            Assert.Throws<ArgumentException>(() => name.ColumnName = "   ");
            Assert.Equal("Name", name.ColumnName);
            Assert.Same(name, table.Columns["Name"]);
        }

        /// <summary>
        /// Renaming a column to the name it already has is a no-op rather than a self-collision error.
        /// </summary>
        [Fact]
        public void RenamingToTheSameNameIsAccepted()
        {
            DataTable table = BuildTable();
            DataColumn name = table.Columns["Name"];
            name.ColumnName = "NAME";
            Assert.Equal("NAME", name.ColumnName);
            Assert.Same(name, table.Columns["name"]);
        }

        /// <summary>
        /// A detached column can be added to another table, which is the point of detaching rather than destroying.
        /// </summary>
        [Fact]
        public void ADetachedColumnCanBeAddedToAnotherTable()
        {
            DataTable source = BuildTable();
            DataTable destination = new DataTable("Destination");
            DataColumn name = source.Columns["Name"];
            source.Columns.Remove(name);
            destination.Columns.Add(name);
            Assert.Same(destination, name.Table);
            Assert.Equal(0, name.Ordinal);
            Assert.Same(name, destination.Columns["Name"]);
        }

        /// <summary>
        /// A column that still belongs to a table cannot be added to a second one, which would leave two tables
        /// sharing storage and disagreeing about its ordinal.
        /// </summary>
        [Fact]
        public void AnAttachedColumnCannotJoinASecondTable()
        {
            DataTable source = BuildTable();
            DataTable destination = new DataTable("Destination");
            ArgumentException failure = Assert.Throws<ArgumentException>(() => destination.Columns.Add(source.Columns["Name"]));
            Assert.Contains("Name", failure.Message);
            Assert.Equal(0, destination.Columns.Count);
        }
        #endregion

        #region Private Methods
        // The four-column table every test here starts from.
        private static DataTable BuildTable()
        {
            DataTable table = new DataTable("Orders");
            table.Columns.Add<Int32>("Id", false);
            table.Columns.Add<String>("Name", true);
            table.Columns.Add<Decimal>("Price", true);
            table.Columns.Add<Boolean>("Active", false);
            return table;
        }

        // The same shape as a System.Data.DataTable, for the side-by-side assertions.
        private static System.Data.DataTable BuildFrameworkTable()
        {
            System.Data.DataTable table = new System.Data.DataTable("Orders");
            table.Columns.Add("Id", typeof(Int32));
            table.Columns.Add("Name", typeof(String));
            table.Columns.Add("Price", typeof(Decimal));
            table.Columns.Add("Active", typeof(Boolean));
            return table;
        }
        #endregion
    }
}
