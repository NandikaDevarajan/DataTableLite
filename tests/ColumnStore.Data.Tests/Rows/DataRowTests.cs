///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies the row-shaped view: typed access by ordinal and by name, the Object compatibility indexer,
//   ItemArray, null handling, and the deliberate absence of any per-row state inside the struct.
// Assumptions: A DataRow is a view, not a copy. Several tests below depend on that - two views of the same row must
//   see each other's writes immediately, because there is no buffer between them and column storage.
// Design Considerations: The "no detached buffer" test is the one that pins NFR-1 down behaviourally. If DataRow ever
//   grew an Object[] of cell values - the shape System.Data.DataRow has - a write through one view would not be
//   visible through another, and the per-row allocation this library exists to remove would be back.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using Xunit;

namespace ColumnStore.Data.Tests.Rows
{
    /// <summary>
    /// Tests for <see cref="DataRow"/>.
    /// </summary>
    public sealed class DataRowTests
    {
        #region Public Methods
        /// <summary>
        /// Typed access works by ordinal and by name, and both reach the same cell.
        /// </summary>
        [Fact]
        public void TypedAccessByOrdinalAndByNameReachTheSameCell()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.AddNewRow();
            row.Set<Int32>(0, 5);
            row.Set<String>("Name", "widget");
            Assert.Equal(5, row.Get<Int32>("Id"));
            Assert.Equal(5, row.Get<Int32>(0));
            Assert.Equal("widget", row.Get<String>(1));
            Assert.Equal("widget", row.Get<String>("Name"));
        }

        /// <summary>
        /// Two views of the same row see each other's writes, because a DataRow holds no cell buffer of its own.
        /// </summary>
        [Fact]
        public void TwoViewsOfTheSameRowShareStorageImmediately()
        {
            DataTable table = BuildTable();
            DataRow written = table.Rows.AddNewRow();
            DataRow read = table.Rows[0];
            written.Set<Int32>("Id", 11);
            Assert.Equal(11, read.Get<Int32>("Id"));
            Assert.Equal(written, read);
            Assert.Equal(written.GetHashCode(), read.GetHashCode());
        }

        /// <summary>
        /// The Object indexer round-trips values by ordinal and by name, and reports a null cell as DBNull.Value -
        /// exactly as System.Data.DataRow does, which is what lets ported code keep its "== DBNull.Value" tests.
        /// </summary>
        [Fact]
        public void ObjectIndexerRoundTripsAndReportsNullAsDBNull()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.AddNewRow();
            row[0] = 3;
            row["Name"] = "widget";
            row["Price"] = DBNull.Value;
            Assert.Equal(3, row[0]);
            Assert.Equal("widget", row["Name"]);
            Assert.Equal(DBNull.Value, row["Price"]);
            Assert.Same(DBNull.Value, row[2]);
            Assert.True(row.IsNull("Price"));
            Assert.True(row.IsNull(2));
        }

        /// <summary>
        /// The typed read of a null cell fails fast, and the defaulting read is the explicit way to ask for
        /// <c>default(T)</c> instead (FR-9).
        /// </summary>
        [Fact]
        public void TypedReadOfANullCellFailsFastUnlessDefaultingIsAskedFor()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.AddNewRow();
            Assert.Throws<InvalidOperationException>(() => row.Get<Decimal>("Price"));
            Assert.Throws<InvalidOperationException>(() => row.Get<Decimal>(2));
            Assert.Equal(0m, row.GetOrDefault<Decimal>("Price"));
            Assert.Equal(0m, row.GetOrDefault<Decimal>(2));
        }

        /// <summary>
        /// SetNull sets a cell to null by ordinal and by name.
        /// </summary>
        [Fact]
        public void SetNullByOrdinalAndByNameClearsTheCell()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.Add(1, "widget", 5m, true);
            row.SetNull("Name");
            row.SetNull(2);
            Assert.True(row.IsNull("Name"));
            Assert.True(row.IsNull("Price"));
            Assert.False(row.IsNull("Id"));
        }

        /// <summary>
        /// ItemArray reads every cell in column order and assigning it populates positionally.
        /// </summary>
        [Fact]
        public void ItemArrayReadsAndWritesEveryCellInColumnOrder()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.AddNewRow();
            row.ItemArray = new Object[] { 4, "widget", 9.5m, true };
            Object[] values = row.ItemArray;
            Assert.Equal(4, values.Length);
            Assert.Equal(4, values[0]);
            Assert.Equal("widget", values[1]);
            Assert.Equal(9.5m, values[2]);
            Assert.Equal(true, values[3]);
        }

        /// <summary>
        /// A partial ItemArray populates the leading cells and leaves the rest alone; an oversized one is refused.
        /// </summary>
        [Fact]
        public void ItemArrayPartialIsAllowedAndOversizedIsRefused()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.AddNewRow();
            row.ItemArray = new Object[] { 4, "widget" };
            Assert.Equal(4, row.Get<Int32>("Id"));
            Assert.True(row.IsNull("Price"));
            Assert.Throws<ArgumentException>(() => row.ItemArray = new Object[] { 1, "a", 2m, true, "extra" });
            Assert.Throws<ArgumentNullException>(() => row.ItemArray = null);
        }

        /// <summary>
        /// Asking for a cell as the wrong type fails with the column-named message from the column layer.
        /// </summary>
        [Fact]
        public void TypedAccessWithTheWrongTypeThrowsNamingTheColumn()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.Add(1, "widget");
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => row.Get<Int64>("Id"));
            Assert.Contains("Id", failure.Message);
        }

        /// <summary>
        /// Unknown columns and out-of-range ordinals are rejected consistently.
        /// </summary>
        [Fact]
        public void CellAccessRejectsUnknownColumns()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.AddNewRow();
            Assert.Throws<ArgumentException>(() => row["Missing"]);
            Assert.Throws<IndexOutOfRangeException>(() => row[99]);
            Assert.Throws<ArgumentException>(() => row.Get<Int32>("Missing"));
            Assert.Throws<IndexOutOfRangeException>(() => row.Get<Int32>(99));
        }

        /// <summary>
        /// An unbound row is rejected with an actionable message rather than a NullReferenceException.
        /// </summary>
        [Fact]
        public void UnboundRowIsRejectedWithAnActionableMessage()
        {
            DataRow unbound = default(DataRow);
            Assert.Null(unbound.Table);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => unbound["Anything"]);
            Assert.Contains("DataTable.Rows", failure.Message);
            Assert.Contains("unbound", unbound.ToString());
        }

        /// <summary>
        /// Row views compare by table identity and physical slot, so a row from another table is never equal to one
        /// from this table even at the same slot.
        /// </summary>
        [Fact]
        public void EqualityComparesTableIdentityAndSlot()
        {
            DataTable first = BuildTable();
            DataTable second = BuildTable();
            DataRow firstRow = first.Rows.AddNewRow();
            DataRow secondRow = second.Rows.AddNewRow();
            Assert.Equal(firstRow.RowIndex, secondRow.RowIndex);
            Assert.NotEqual(firstRow, secondRow);
            Assert.False(firstRow.Equals((Object)secondRow));
            Assert.False(firstRow.Equals("not a row"));
            Assert.True(firstRow.Equals((Object)first.Rows[0]));
        }

        /// <summary>
        /// ToString names the table and slot, which is what makes a failing assertion on a row readable.
        /// </summary>
        [Fact]
        public void ToStringNamesTheTableAndSlot()
        {
            DataTable table = BuildTable();
            DataRow row = table.Rows.AddNewRow();
            String description = row.ToString();
            Assert.Contains("Orders", description);
            Assert.Contains("0", description);
        }
        #endregion

        #region Private Methods
        // A four-column nullable table: a value type, a reference type, a decimal and a bit-packed Boolean.
        private static DataTable BuildTable()
        {
            DataTable table = new DataTable("Orders");
            table.Columns.Add<Int32>("Id");
            table.Columns.Add<String>("Name");
            table.Columns.Add<Decimal>("Price");
            table.Columns.Add<Boolean>("Active");
            return table;
        }
        #endregion
    }
}
