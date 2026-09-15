///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: One body of test logic, run against both System.Data.DataTable and ColumnStore.Data.DataTable, asserting
//   they behave equivalently across the overlapping API surface (NFR-8, 03_Design.md section 6.7).
// Assumptions: Only the overlapping surface is compared. Everything ColumnStore.Data does not implement in v1 -
//   constraints, relations, RowState, AcceptChanges, XML, column removal - is out of scope by design and listed in
//   readme.txt rather than tested here.
// Design Considerations: Each test runs twice, once per implementation, driven by xunit's theory data. That is
//   stronger than writing two parallel suites: a behaviour that only one implementation has cannot quietly become
//   the expectation, because the same assertions have to hold for both.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

using Xunit;

namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// Behavioural equivalence tests across both table implementations.
    /// </summary>
    public sealed class TableCompatibilityTests
    {
        #region Public Methods
        /// <summary>
        /// Schema creation, positional row addition, indexed reads and the row count all behave the same.
        /// </summary>
        [Theory]
        [InlineData(TableImplementation.Lite)]
        [InlineData(TableImplementation.Framework)]
        public void SchemaAndPositionalRowsBehaveTheSame(TableImplementation implementation)
        {
            ITableHarness harness = CreateHarness(implementation);
            DefineSchema(harness);
            Assert.Equal(4, harness.ColumnCount);
            harness.AddRow(new Object[] { 1, "first", 10.5m, true });
            harness.AddRow(new Object[] { 2, "second", 20.5m, false });
            Assert.Equal(2, harness.RowCount);
            Assert.Equal(1, harness.GetCell(0, "Id"));
            Assert.Equal("first", harness.GetCell(0, "Name"));
            Assert.Equal(10.5m, harness.GetCell(0, "Price"));
            Assert.Equal(true, harness.GetCell(0, "Active"));
            Assert.Equal(2, harness.GetCell(1, "Id"));
            Assert.Equal(false, harness.GetCell(1, "Active"));
        }

        /// <summary>
        /// The reserve, write, commit flow produces the same result in both.
        /// </summary>
        [Theory]
        [InlineData(TableImplementation.Lite)]
        [InlineData(TableImplementation.Framework)]
        public void TwoStepRowCreationBehavesTheSame(TableImplementation implementation)
        {
            ITableHarness harness = CreateHarness(implementation);
            DefineSchema(harness);
            Dictionary<String, Object> fields = new Dictionary<String, Object>();
            fields.Add("Id", 7);
            fields.Add("Name", "reserved");
            fields.Add("Price", 3.25m);
            fields.Add("Active", true);
            harness.AddRowInTwoSteps(fields);
            Assert.Equal(1, harness.RowCount);
            Assert.Equal(7, harness.GetCell(0, "Id"));
            Assert.Equal("reserved", harness.GetCell(0, "Name"));
            Assert.Equal(3.25m, harness.GetCell(0, "Price"));
        }

        /// <summary>
        /// Nulls behave the same: writing null makes the cell null, IsNull reports it, and an unpopulated cell of a
        /// nullable column is null rather than a default value.
        /// </summary>
        [Theory]
        [InlineData(TableImplementation.Lite)]
        [InlineData(TableImplementation.Framework)]
        public void NullHandlingBehavesTheSame(TableImplementation implementation)
        {
            ITableHarness harness = CreateHarness(implementation);
            DefineSchema(harness);
            harness.AddRow(new Object[] { 1, "first", 10.5m, true });
            harness.SetCell(0, "Name", null);
            Assert.True(harness.IsCellNull(0, "Name"));
            Assert.Equal(DBNull.Value, harness.GetCell(0, "Name"));
            harness.AddRow(new Object[] { 2 });
            Assert.True(harness.IsCellNull(1, "Name"));
            Assert.True(harness.IsCellNull(1, "Price"));
            Assert.Equal(DBNull.Value, harness.GetCell(1, "Price"));
            harness.SetCell(1, "Price", 9.75m);
            Assert.False(harness.IsCellNull(1, "Price"));
            Assert.Equal(9.75m, harness.GetCell(1, "Price"));
        }

        /// <summary>
        /// Iterating rows visits the same values in the same order in both.
        /// </summary>
        [Theory]
        [InlineData(TableImplementation.Lite)]
        [InlineData(TableImplementation.Framework)]
        public void IterationBehavesTheSame(TableImplementation implementation)
        {
            ITableHarness harness = CreateHarness(implementation);
            DefineSchema(harness);
            for (Int32 id = 0; id < 25; id++)
            {
                harness.AddRow(new Object[] { id, "row" + id, id * 1.5m, id % 2 == 0 });
            }
            List<Object> ids = harness.EnumerateColumn("Id");
            Assert.Equal(25, ids.Count);
            for (Int32 position = 0; position < ids.Count; position++)
            {
                Assert.Equal(position, ids[position]);
            }
            Int64 total = harness.SumInt32Column("Id");
            Assert.Equal(300L, total);
        }

        /// <summary>
        /// Removing a row drops the count and renumbers the rest identically in both, however deletion is
        /// implemented underneath.
        /// </summary>
        [Theory]
        [InlineData(TableImplementation.Lite)]
        [InlineData(TableImplementation.Framework)]
        public void RowRemovalBehavesTheSame(TableImplementation implementation)
        {
            ITableHarness harness = CreateHarness(implementation);
            DefineSchema(harness);
            for (Int32 id = 0; id < 10; id++)
            {
                harness.AddRow(new Object[] { id, "row" + id, id, true });
            }
            harness.DeleteRow(0);
            harness.DeleteRow(4);
            Assert.Equal(8, harness.RowCount);
            List<Object> ids = harness.EnumerateColumn("Id");
            Assert.Equal(new List<Object> { 1, 2, 3, 4, 6, 7, 8, 9 }, ids);
            Assert.Equal(1, harness.GetCell(0, "Id"));
            Assert.Equal(6, harness.GetCell(4, "Id"));
            Assert.Equal(40L, harness.SumInt32Column("Id"));
        }

        /// <summary>
        /// Clear empties the rows, keeps the schema, and leaves the table ready to refill - the same in both.
        /// </summary>
        [Theory]
        [InlineData(TableImplementation.Lite)]
        [InlineData(TableImplementation.Framework)]
        public void ClearBehavesTheSame(TableImplementation implementation)
        {
            ITableHarness harness = CreateHarness(implementation);
            DefineSchema(harness);
            for (Int32 id = 0; id < 5; id++)
            {
                harness.AddRow(new Object[] { id, "row" + id, id, true });
            }
            harness.Clear();
            Assert.Equal(0, harness.RowCount);
            Assert.Equal(4, harness.ColumnCount);
            harness.AddRow(new Object[] { 99, "after", 1m, false });
            Assert.Equal(1, harness.RowCount);
            Assert.Equal(99, harness.GetCell(0, "Id"));
            Assert.Equal("after", harness.GetCell(0, "Name"));
        }

        /// <summary>
        /// Positional population with a widenable literal behaves the same: both convert an Int32 literal into an
        /// Int64 column rather than refusing it.
        /// </summary>
        [Theory]
        [InlineData(TableImplementation.Lite)]
        [InlineData(TableImplementation.Framework)]
        public void WidenedLiteralBehavesTheSame(TableImplementation implementation)
        {
            ITableHarness harness = CreateHarness(implementation);
            harness.AddColumn("Total", typeof(Int64), true);
            harness.AddRow(new Object[] { 42 });
            Assert.Equal(42L, harness.GetCell(0, "Total"));
        }

        /// <summary>
        /// A larger mixed workload - add, read, overwrite, null out, remove, iterate - lands both implementations in
        /// the same observable state.
        /// </summary>
        [Theory]
        [InlineData(TableImplementation.Lite)]
        [InlineData(TableImplementation.Framework)]
        public void MixedWorkloadLeavesBothInTheSameState(TableImplementation implementation)
        {
            ITableHarness harness = CreateHarness(implementation);
            DefineSchema(harness);
            for (Int32 id = 0; id < 40; id++)
            {
                harness.AddRow(new Object[] { id, "row" + id, id * 2m, id % 3 == 0 });
            }
            for (Int32 rowIndex = 0; rowIndex < harness.RowCount; rowIndex = rowIndex + 5)
            {
                harness.SetCell(rowIndex, "Name", null);
            }
            harness.DeleteRow(0);
            harness.DeleteRow(harness.RowCount - 1);
            harness.SetCell(0, "Price", 123.45m);
            Assert.Equal(38, harness.RowCount);
            Assert.Equal(1, harness.GetCell(0, "Id"));
            Assert.Equal(123.45m, harness.GetCell(0, "Price"));
            List<Object> names = harness.EnumerateColumn("Name");
            Int32 nullNameCount = 0;
            for (Int32 position = 0; position < names.Count; position++)
            {
                if (names[position] is DBNull) { nullNameCount = nullNameCount + 1; }
            }
            Assert.Equal(7, nullNameCount);
            Assert.Equal(741L, harness.SumInt32Column("Id"));
        }
        #endregion

        #region Private Methods
        // Builds a fresh harness for the requested implementation, inside the test rather than as theory data, so no
        // state can travel between test cases.
        private static ITableHarness CreateHarness(TableImplementation implementation)
        {
            if (implementation == TableImplementation.Lite) { return new LiteTableHarness(); }
            return new SystemTableHarness();
        }

        // The shared four-column schema: a value type, a reference type, a decimal and a Boolean, all nullable so
        // that unpopulated cells are observable in both implementations.
        private static void DefineSchema(ITableHarness harness)
        {
            harness.AddColumn("Id", typeof(Int32), true);
            harness.AddColumn("Name", typeof(String), true);
            harness.AddColumn("Price", typeof(Decimal), true);
            harness.AddColumn("Active", typeof(Boolean), true);
        }
        #endregion
    }
}
