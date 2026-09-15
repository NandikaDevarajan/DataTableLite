///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies the table façade: naming, the schema and row collections, the convenience aliases, and that Clear
//   preserves the schema while releasing the data (FR-11).
// Assumptions: The table delegates almost everything. These tests check the delegation and the few behaviours the
//   table owns outright, rather than re-testing the layers underneath.
// Design Considerations: The "column added after rows exist" test documents a behaviour rather than guarding an
//   invariant. It is allowed, the existing rows read null in the new column, and pinning it down here means a future
//   change to that policy has to be deliberate.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using Xunit;

namespace ColumnStore.Data.Tests.Rows
{
    /// <summary>
    /// Tests for <see cref="DataTable"/>.
    /// </summary>
    public sealed class DataTableTests
    {
        #region Public Methods
        /// <summary>
        /// A new table is empty, named as constructed, and has both collections ready.
        /// </summary>
        [Fact]
        public void NewTableIsEmptyAndNamed()
        {
            DataTable unnamed = new DataTable();
            Assert.Equal(String.Empty, unnamed.TableName);
            Assert.Equal(0, unnamed.Columns.Count);
            Assert.Equal(0, unnamed.Rows.Count);
            Assert.Equal(0, unnamed.Count);
            DataTable named = new DataTable("Orders");
            Assert.Equal("Orders", named.TableName);
            named.TableName = null;
            Assert.Equal(String.Empty, named.TableName);
        }

        /// <summary>
        /// Count is an alias for Rows.Count, and the collections are stable objects rather than fresh instances per
        /// access - cached handles and cached collections both have to stay valid.
        /// </summary>
        [Fact]
        public void CollectionsAreStableAndCountTracksRows()
        {
            DataTable table = new DataTable("Orders");
            DataColumnCollection columns = table.Columns;
            DataRowCollection rows = table.Rows;
            Assert.Same(columns, table.Columns);
            Assert.Same(rows, table.Rows);
            table.Columns.Add<Int32>("Id");
            table.Rows.Add(1);
            table.Rows.Add(2);
            Assert.Equal(2, table.Count);
            Assert.Equal(table.Rows.Count, table.Count);
        }

        /// <summary>
        /// The table-level row aliases delegate to the row collection, including the detached-row protocol.
        /// </summary>
        [Fact]
        public void RowAliasesDelegateToTheRowCollection()
        {
            DataTable table = new DataTable("Orders");
            table.Columns.Add<Int32>("Id");
            DataRow appended = table.AddNewRow();
            appended.Set<Int32>("Id", 1);
            Assert.Equal(1, table.Count);
            DataRow detached = table.NewRow();
            detached.Set<Int32>("Id", 2);
            Assert.Equal(1, table.Count);
            table.Rows.Add(detached);
            Assert.Equal(2, table.Count);
            // Several detached rows at once go through the table facade too, and none of them is counted.
            table.NewRow();
            table.NewRow();
            Assert.Equal(2, table.Count);
            Assert.Equal(2, table.Rows.DetachedRowCount);
        }

        /// <summary>
        /// Clear releases the rows and the cell data while leaving the schema and every cached column handle usable
        /// (FR-11).
        /// </summary>
        [Fact]
        public void ClearKeepsTheSchemaAndCachedHandles()
        {
            DataTable table = new DataTable("Orders");
            DataColumn<Int32> id = table.Columns.Add<Int32>("Id");
            DataColumn<String> name = table.Columns.Add<String>("Name");
            table.Rows.Add(1, "first");
            table.Rows.Add(2, "second");
            table.Clear();
            Assert.Equal(0, table.Count);
            Assert.Equal(2, table.Columns.Count);
            Assert.Same(id, table.Columns.GetColumn<Int32>("Id"));
            Assert.Same(name, table.Columns.GetColumn<String>("Name"));
            table.Rows.Add(3, "third");
            Assert.Equal(3, id.Get(0));
            Assert.Equal("third", name.Get(0));
        }

        /// <summary>
        /// A column added after rows exist is allowed; the existing rows read null in it. Documented behaviour, not
        /// an accident.
        /// </summary>
        [Fact]
        public void ColumnAddedAfterRowsExistLeavesExistingRowsNull()
        {
            DataTable table = new DataTable("Orders");
            table.Columns.Add<Int32>("Id");
            table.Rows.Add(1);
            table.Rows.Add(2);
            table.Columns.Add<String>("Note");
            Assert.Equal(2, table.Rows.Count);
            Assert.True(table.Rows[0].IsNull("Note"));
            table.Rows[1].Set<String>("Note", "later");
            Assert.Equal("later", table.Rows[1].Get<String>("Note"));
            Assert.True(table.Rows[0].IsNull("Note"));
        }

        /// <summary>
        /// The hot-loop pattern the library is built around works end to end: resolve the column once, then read by
        /// row index.
        /// </summary>
        [Fact]
        public void RecommendedHotLoopPatternReadsEveryRow()
        {
            DataTable table = new DataTable("Orders");
            DataColumn<Int32> quantity = table.Columns.Add<Int32>("Quantity", false);
            for (Int32 rowIndex = 0; rowIndex < 1000; rowIndex++)
            {
                DataRow row = table.Rows.AddNewRow();
                quantity.Set(row.RowIndex, rowIndex);
            }
            Int64 total = 0;
            for (Int32 rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                total = total + quantity.Get(rowIndex);
            }
            Assert.Equal(499500L, total);
        }

        /// <summary>
        /// ToString describes the table well enough to read in a failure message.
        /// </summary>
        [Fact]
        public void ToStringDescribesNameColumnsAndRows()
        {
            DataTable table = new DataTable("Orders");
            table.Columns.Add<Int32>("Id");
            table.Rows.Add(1);
            String description = table.ToString();
            Assert.Contains("Orders", description);
            Assert.Contains("1 columns", description);
            Assert.Contains("1 rows", description);
            DataTable unnamed = new DataTable();
            Assert.Contains("(unnamed)", unnamed.ToString());
        }
        #endregion
    }
}
