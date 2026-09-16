///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies the schema surface: adding columns by type and by generic parameter, name and ordinal lookup,
//   the typed handle retrieval that FR-5's hot-loop pattern depends on, and the clear, actionable errors NFR-12 asks
//   for when a caller asks for the wrong type.
// Assumptions: Column names are compared case-insensitively, matching System.Data.DataTable's default (NFR-8).
// Design Considerations: The identity test matters more than it looks. The whole point of GetColumn<T> is that the
//   handle it returns is the same object the collection holds, so writes through a cached handle are visible through
//   every other access path. A future change that returned a wrapper or a copy would break the hot-loop pattern
//   silently, and this is the test that would catch it.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

using Xunit;

namespace ColumnStore.Data.Tests.Columns
{
    /// <summary>
    /// Tests for <see cref="DataColumnCollection"/>.
    /// </summary>
    public sealed class DataColumnCollectionTests
    {
        #region Public Methods
        /// <summary>
        /// Columns added either way land in ordinal order and are reachable by ordinal and by name.
        /// </summary>
        [Fact]
        public void AddAssignsOrdinalsAndRegistersNames()
        {
            DataColumnCollection columns = new DataColumnCollection();
            columns.Add("Id", typeof(Int32), false);
            columns.Add<String>("Name");
            columns.Add("Created", typeof(DateTime), true);
            Assert.Equal(3, columns.Count);
            Assert.Equal("Id", columns[0].ColumnName);
            Assert.Equal("Name", columns[1].ColumnName);
            Assert.Equal("Created", columns[2].ColumnName);
            Assert.Equal(0, columns.IndexOf("Id"));
            Assert.Equal(2, columns.IndexOf("Created"));
            Assert.Equal(-1, columns.IndexOf("Missing"));
            Assert.True(columns.Contains("Name"));
            Assert.False(columns.Contains("Missing"));
        }

        /// <summary>
        /// Names are matched case-insensitively, on lookup and on duplicate detection.
        /// </summary>
        [Fact]
        public void NameLookupIsCaseInsensitive()
        {
            DataColumnCollection columns = new DataColumnCollection();
            columns.Add<Int32>("Quantity");
            Assert.Equal("Quantity", columns["QUANTITY"].ColumnName);
            Assert.Equal(0, columns.IndexOf("quantity"));
            Assert.True(columns.Contains("QuAnTiTy"));
            Assert.Throws<ArgumentException>(() => columns.Add<Int32>("QUANTITY"));
        }

        /// <summary>
        /// The typed handle from Add, the typed handle from GetColumn and the erased column from the indexer are all
        /// the same object - which is what makes caching a handle outside a hot loop correct.
        /// </summary>
        [Fact]
        public void GetColumnReturnsTheSameInstanceTheCollectionHolds()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> fromAdd = columns.Add<Int32>("Quantity");
            DataColumn<Int32> byName = columns.GetColumn<Int32>("Quantity");
            DataColumn<Int32> byOrdinal = columns.GetColumn<Int32>(0);
            IDataColumn erased = columns["Quantity"];
            Assert.Same(fromAdd, byName);
            Assert.Same(fromAdd, byOrdinal);
            Assert.Same(fromAdd, erased);
            byName.Set(0, 5);
            Assert.Equal(5, fromAdd.Get(0));
            Assert.Equal(5, erased.GetValue(0));
        }

        /// <summary>
        /// A column created from a runtime Type is retrievable as its typed handle, so a schema built by the SQL
        /// loader supports the same hot-loop pattern as a hand-written one.
        /// </summary>
        [Fact]
        public void GetColumnAfterAddByRuntimeTypeReturnsATypedHandle()
        {
            DataColumnCollection columns = new DataColumnCollection();
            columns.Add("Total", typeof(Decimal), true);
            DataColumn<Decimal> total = columns.GetColumn<Decimal>("Total");
            total.Set(0, 12.5m);
            Assert.Equal(12.5m, total.Get(0));
        }

        /// <summary>
        /// Asking for a column as the wrong type fails with a message naming the column and both types (NFR-12).
        /// </summary>
        [Fact]
        public void GetColumnWithTheWrongTypeThrowsNamingTheColumnAndBothTypes()
        {
            DataColumnCollection columns = new DataColumnCollection();
            columns.Add<Int32>("Quantity");
            InvalidOperationException byName = Assert.Throws<InvalidOperationException>(() => columns.GetColumn<Int64>("Quantity"));
            Assert.Contains("Quantity", byName.Message);
            Assert.Contains("Int32", byName.Message);
            Assert.Contains("Int64", byName.Message);
            InvalidOperationException byOrdinal = Assert.Throws<InvalidOperationException>(() => columns.GetColumn<String>(0));
            Assert.Contains("Quantity", byOrdinal.Message);
        }

        /// <summary>
        /// A nullable generic type parameter is refused with a message showing what to write instead - accepting it
        /// would double the column's storage width for no benefit.
        /// </summary>
        [Fact]
        public void AddOfNullableValueTypeIsRefusedWithGuidance()
        {
            DataColumnCollection columns = new DataColumnCollection();
            ArgumentException failure = Assert.Throws<ArgumentException>(() => columns.Add<Nullable<Int32>>("Quantity"));
            Assert.Contains("allowDBNull", failure.Message);
            Assert.Contains("Int32", failure.Message);
        }

        /// <summary>
        /// Bad names and missing types are rejected, and a failed Add leaves the schema untouched.
        /// </summary>
        [Fact]
        public void AddWithInvalidInputThrowsAndLeavesTheSchemaUnchanged()
        {
            DataColumnCollection columns = new DataColumnCollection();
            Assert.Throws<ArgumentNullException>(() => columns.Add(null, typeof(Int32), true));
            Assert.Throws<ArgumentNullException>(() => columns.Add("Value", null, true));
            Assert.Throws<ArgumentException>(() => columns.Add("", typeof(Int32), true));
            Assert.Throws<ArgumentException>(() => columns.Add<Int32>("  "));
            Assert.Equal(0, columns.Count);
        }

        /// <summary>
        /// Lookup failures behave exactly as System.Data.DataColumnCollection's do, which is not what a library
        /// designed from scratch would choose: an unknown NAME gives null rather than throwing, an out-of-range
        /// ORDINAL throws IndexOutOfRangeException rather than ArgumentOutOfRangeException, and a null name is
        /// tolerated by IndexOf and Contains but not by the indexer. Code being ported depends on every one of these.
        /// </summary>
        [Fact]
        public void LookupFailuresMatchSystemDataExactly()
        {
            DataColumnCollection columns = new DataColumnCollection();
            columns.Add<Int32>("Quantity");
            Assert.Throws<IndexOutOfRangeException>(() => columns[-1]);
            Assert.Throws<IndexOutOfRangeException>(() => columns[1]);
            Assert.Null(columns["Missing"]);
            Assert.Throws<ArgumentNullException>(() => columns[null]);
            Assert.Equal(-1, columns.IndexOf((String)null));
            Assert.Equal(-1, columns.IndexOf(String.Empty));
            Assert.Equal(-1, columns.IndexOf("Missing"));
            Assert.False(columns.Contains(null));
            Assert.False(columns.Contains("Missing"));
        }

        /// <summary>
        /// DataColumnCollection.Clear drops the COLUMNS, exactly as System.Data.DataColumnCollection.Clear does -
        /// it is DataTable.Clear that empties the rows and keeps the schema (FR-11). The two are easy to confuse and
        /// the difference is load-bearing for ported code, so both are pinned.
        /// </summary>
        [Fact]
        public void ClearDropsTheColumnsThemselvesAsSystemDataDoes()
        {
            DataTable table = new DataTable("Orders");
            DataColumn<Int32> quantity = table.Columns.Add<Int32>("Quantity", true);
            table.Columns.Add<String>("Name", true);
            table.Rows.Add(1, "widget");
            table.Columns.Clear();
            Assert.Equal(0, table.Columns.Count);
            Assert.Null(table.Columns["Quantity"]);
            // The dropped column is detached, not destroyed: a handle taken before the Clear still reads its data,
            // which is what makes "pull a column out of a table and keep the values" work.
            Assert.Null(quantity.Table);
            Assert.Equal(-1, quantity.Ordinal);
            Assert.Equal(1, quantity.Get(0));
        }

        /// <summary>
        /// Enumeration yields the columns in ordinal order.
        /// </summary>
        [Fact]
        public void EnumerationYieldsColumnsInOrdinalOrder()
        {
            DataColumnCollection columns = new DataColumnCollection();
            columns.Add<Int32>("A");
            columns.Add<Int32>("B");
            columns.Add<Int32>("C");
            List<String> names = new List<String>();
            foreach (IDataColumn column in columns)
            {
                names.Add(column.ColumnName);
            }
            Assert.Equal(new List<String> { "A", "B", "C" }, names);
        }

        /// <summary>
        /// An explicit chunk row count is honoured, which is what lets tests reach chunk boundaries cheaply.
        /// </summary>
        [Fact]
        public void AddWithExplicitChunkSizeHonoursIt()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true, 4);
            ColumnStore.Data.Storage.TypedColumnStorage<Int32> storage = (ColumnStore.Data.Storage.TypedColumnStorage<Int32>)quantity.ValueStorage;
            Assert.Equal(4, storage.ChunkRowCount);
        }
        #endregion
    }
}
