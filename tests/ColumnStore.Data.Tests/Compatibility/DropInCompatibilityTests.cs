///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies that code written against System.Data.DataTable behaves the same way after nothing more than a
//   change of using directive - and pins, precisely, the places where it cannot.
// Assumptions: Both implementations are driven through the SAME statements wherever C# allows it, so a difference the
//   suite reports is behavioural rather than a difference in how the test was written.
// Design Considerations: The two tests that matter most here are EveryRowAssignmentShapeThatCompilesAlsoWrites and
//   CompoundAssignmentBetweenTwoRowsWritesToTheCorrectRow. DataRow is a readonly struct - that is where the memory
//   saving comes from (NFR-1) - and the row indexer returns it BY REFERENCE so that every System.Data assignment
//   shape compiles. The second test is the one guarding the design: a ref return needs real per-row storage, and the
//   cheaper shared-slot version writes to the wrong row in a compound statement without raising anything.
//   The refusal inventory at the bottom is deliberately exhaustive rather than representative. Its job is to be the
//   checklist someone runs their code against before porting, so a member that is quietly implemented later shows up
//   as a failing test that says "update the documentation".
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;

using Xunit;

namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// Tests that ColumnStore.Data presents the same surface and the same behaviour as System.Data.
    /// </summary>
    public sealed class DropInCompatibilityTests
    {
        #region Public Methods
        /// <summary>
        /// The canonical System.Data.DataTable opening - construct, add columns by name and Type, add rows by value
        /// array, read cells back by name and ordinal - compiles and behaves identically in both.
        /// </summary>
        [Fact]
        public void TheCanonicalOpeningBehavesIdentically()
        {
            DataTable lite = new DataTable("Orders");
            lite.Columns.Add("Id", typeof(Int32));
            lite.Columns.Add("Name", typeof(String));
            lite.Columns.Add("Price", typeof(Decimal));
            lite.Rows.Add(1, "widget", 9.5m);
            lite.Rows.Add(2, "gadget", 4.25m);

            System.Data.DataTable framework = new System.Data.DataTable("Orders");
            framework.Columns.Add("Id", typeof(Int32));
            framework.Columns.Add("Name", typeof(String));
            framework.Columns.Add("Price", typeof(Decimal));
            framework.Rows.Add(1, "widget", 9.5m);
            framework.Rows.Add(2, "gadget", 4.25m);

            Assert.Equal(framework.TableName, lite.TableName);
            Assert.Equal(framework.Columns.Count, lite.Columns.Count);
            Assert.Equal(framework.Rows.Count, lite.Rows.Count);
            Assert.Equal(framework.Rows[0]["Name"], lite.Rows[0]["Name"]);
            Assert.Equal(framework.Rows[1][2], lite.Rows[1][2]);
            Assert.Equal(framework.Columns[1].ColumnName, lite.Columns[1].ColumnName);
            Assert.Equal(framework.Columns["Price"].DataType, lite.Columns["Price"].DataType);
            Assert.Equal(framework.Columns["Price"].Ordinal, lite.Columns["Price"].Ordinal);
        }

        /// <summary>
        /// A column is reachable as a non-generic DataColumn, which is the type System.Data code names constantly -
        /// "DataColumn column = table.Columns["x"];" has to compile or nothing else matters.
        /// </summary>
        [Fact]
        public void AColumnIsUsableAsANonGenericDataColumn()
        {
            DataTable table = BuildLiteTable();
            DataColumn column = table.Columns["Name"];
            Assert.Equal("Name", column.ColumnName);
            Assert.Equal(typeof(String), column.DataType);
            Assert.True(column.AllowDBNull);
            Assert.Equal(1, column.Ordinal);
            Assert.Same(table, column.Table);
            Assert.Equal("Name", column.Caption);
            column.Caption = "Product name";
            Assert.Equal("Product name", column.Caption);
            Assert.Equal("Name", column.ToString());
        }

        /// <summary>
        /// The typed handle and the non-generic column are the same object, so the fast path and the compatibility
        /// path address the same storage. This is what makes a partial port - hot loops typed, everything else left
        /// alone - work at all.
        /// </summary>
        [Fact]
        public void TheTypedHandleAndTheCompatibilitySurfaceAreTheSameColumn()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "widget", 9.5m);
            DataColumn<String> typed = table.Columns.GetColumn<String>("Name");
            DataColumn erased = table.Columns["Name"];
            Assert.Same(typed, erased);
            typed.Set(0, "changed");
            Assert.Equal("changed", table.Rows[0]["Name"]);
        }

        /// <summary>
        /// A null cell reads as DBNull.Value through every Object-shaped accessor, exactly as System.Data reports it,
        /// and writing DBNull.Value or null makes a cell null. This is the single behaviour most ported code depends
        /// on without ever naming it.
        /// </summary>
        [Fact]
        public void NullCellsReadAsDBNullEverywhere()
        {
            DataTable lite = BuildLiteTable();
            lite.Rows.Add(1, null, DBNull.Value);
            System.Data.DataTable framework = BuildFrameworkTable();
            framework.Rows.Add(1, null, DBNull.Value);

            Assert.Equal(framework.Rows[0]["Name"], lite.Rows[0]["Name"]);
            Assert.Same(DBNull.Value, lite.Rows[0]["Name"]);
            Assert.Same(DBNull.Value, lite.Rows[0][2]);
            Assert.Same(DBNull.Value, lite.Rows[0][lite.Columns["Price"]]);
            Assert.Equal(DBNull.Value, lite.Rows[0].ItemArray[1]);
            Assert.Equal(framework.Rows[0].IsNull("Name"), lite.Rows[0].IsNull("Name"));
            Assert.True(lite.Rows[0].IsNull("Price"));
        }

        /// <summary>
        /// ItemArray round-trips in both directions, with DBNull for the null cells, as System.Data's does.
        /// </summary>
        [Fact]
        public void ItemArrayRoundTripsWithDBNull()
        {
            DataTable table = BuildLiteTable();
            DataRow row = table.Rows.AddNewRow();
            row.ItemArray = new Object[] { 7, "widget", DBNull.Value };
            Object[] values = row.ItemArray;
            Assert.Equal(3, values.Length);
            Assert.Equal(7, values[0]);
            Assert.Equal("widget", values[1]);
            Assert.Equal(DBNull.Value, values[2]);
        }

        /// <summary>
        /// EVERY WAY System.Data CODE ASSIGNS A CELL, including the two that used to be rejected. DataRow is a
        /// readonly struct - that is where the memory saving comes from - and C# only allows assignment through a
        /// property whose receiver is a VARIABLE, which a by-value struct return is not. The row indexer therefore
        /// returns "ref DataRow", so the receiver IS a variable and every shape below compiles. Each one is asserted
        /// to actually reach the table, because compiling is only half of working.
        /// </summary>
        [Fact]
        public void EveryRowAssignmentShapeThatCompilesAlsoWrites()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "first", 9.5m);
            table.Rows.Add(2, "second", 4.0m);

            // The form most ported code uses. The iteration variable is a readonly location, and a readonly struct's
            // members may be invoked on one, so this compiles - and the writes reach the table.
            foreach (DataRow row in table.Rows)
            {
                row["Name"] = "via-foreach";
            }
            Assert.Equal("via-foreach", table.Rows[0]["Name"]);
            Assert.Equal("via-foreach", table.Rows[1]["Name"]);

            // A local is a variable, so assignment through it is allowed.
            DataRow local = table.Rows[0];
            local["Name"] = "via-local";
            Assert.Equal("via-local", table.Rows[0]["Name"]);

            // A METHOD call on the indexer's result is legal C# even though an assignment is not, so the strongly
            // typed setter works straight off the collection.
            table.Rows[1].Set<String>("Name", "via-method");
            Assert.Equal("via-method", table.Rows[1]["Name"]);

            // Array elements are variables, so a row out of Select() takes assignment directly.
            DataRow[] selected = table.Select();
            selected[0]["Price"] = 11.25m;
            Assert.Equal(11.25m, table.Rows[0]["Price"]);

            // Straight off the collection indexer - the shape a port would otherwise have had to rewrite.
            table.Rows[0]["Name"] = "via-indexer";
            Assert.Equal("via-indexer", table.Rows[0]["Name"]);

            // And the same for ItemArray, the other shape the by-value indexer rejected.
            table.Rows[1].ItemArray = new Object[] { 9, "via-itemarray", 1.5m };
            Assert.Equal(9, table.Rows[1]["Id"]);
            Assert.Equal("via-itemarray", table.Rows[1]["Name"]);
            Assert.Equal(1.5m, table.Rows[1]["Price"]);

            // The struct is a VIEW, not a copy: two views of the same row see each other's writes, which is what
            // makes all of the above equivalent.
            DataRow firstView = table.Rows[0];
            DataRow secondView = table.Rows[0];
            firstView["Name"] = "via-view";
            Assert.Equal("via-view", secondView["Name"]);
            Assert.Equal(firstView, secondView);
        }

        /// <summary>
        /// THE TEST THAT DECIDED THE DESIGN. A ref-returning indexer needs somewhere real to point, and the cheap
        /// choice - one shared scratch slot reused by every call - is wrong in a way nothing would ever report. In
        /// "rows[0][x] = rows[1][x]" the compiler takes the target's address first and then evaluates the right-hand
        /// side, which with a shared slot overwrites that address with row 1: the write silently lands in the WRONG
        /// ROW and no exception is raised. One view slot per row is what makes it exact, and this is the test that
        /// would catch a future "optimisation" back to a shared slot.
        /// </summary>
        [Fact]
        public void CompoundAssignmentBetweenTwoRowsWritesToTheCorrectRow()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "zero", 1m);
            table.Rows.Add(2, "one", 2m);
            table.Rows.Add(3, "two", 3m);

            table.Rows[0]["Name"] = table.Rows[1]["Name"];
            Assert.Equal("one", table.Rows[0]["Name"]);
            Assert.Equal("one", table.Rows[1]["Name"]);
            Assert.Equal("two", table.Rows[2]["Name"]);

            // Three rows in one statement, for the same reason.
            table.Rows[2]["Price"] = (Decimal)table.Rows[0]["Price"] + (Decimal)table.Rows[1]["Price"];
            Assert.Equal(3m, table.Rows[2]["Price"]);
            Assert.Equal(1m, table.Rows[0]["Price"]);
            Assert.Equal(2m, table.Rows[1]["Price"]);
        }

        /// <summary>
        /// The row-view store is chunked and its chunks are never reallocated, so a reference taken before rows are
        /// appended still addresses the row it was taken for. A store that grew by copying into a bigger array would
        /// leave that reference pointing at a stale copy.
        /// </summary>
        [Fact]
        public void ARowReferenceSurvivesLaterAppends()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "first", 1m);
            ref DataRow first = ref table.Rows[0];
            for (Int32 rowIndex = 0; rowIndex < 5000; rowIndex++)
            {
                table.Rows.Add(rowIndex + 2, "row" + rowIndex, rowIndex);
            }
            first["Name"] = "written-through-old-reference";
            Assert.Equal("written-through-old-reference", table.Rows[0]["Name"]);
            Assert.Equal(5001, table.Rows.Count);
        }

        /// <summary>
        /// Deleted rows are skipped by the indexer exactly as before, so the view store is addressed by PHYSICAL slot
        /// while the caller keeps passing logical positions.
        /// </summary>
        [Fact]
        public void TheRowIndexerStillSkipsDeletedRowsWhenAssigning()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "a", 1m);
            table.Rows.Add(2, "b", 2m);
            table.Rows.Add(3, "c", 3m);
            DataRow second = table.Rows[1];
            second.Delete();
            Assert.Equal(2, table.Rows.Count);
            table.Rows[1]["Name"] = "was-c";
            Assert.Equal(3, table.Rows[1]["Id"]);
            Assert.Equal("was-c", table.Rows[1]["Name"]);
            Assert.Equal("a", table.Rows[0]["Name"]);
        }

        /// <summary>
        /// foreach over Rows works, which is how most System.Data code reads a table.
        /// </summary>
        [Fact]
        public void ForeachOverRowsAndColumnsWorks()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "a", 1m);
            table.Rows.Add(2, "b", 2m);
            Int32 total = 0;
            foreach (DataRow row in table.Rows)
            {
                total = total + (Int32)row["Id"];
            }
            Assert.Equal(3, total);
            Int32 columnCount = 0;
            foreach (DataColumn column in table.Columns)
            {
                Assert.False(String.IsNullOrEmpty(column.ColumnName));
                columnCount = columnCount + 1;
            }
            Assert.Equal(3, columnCount);
        }

        /// <summary>
        /// THE DETACHED ROW PATTERN, RUN AGAINST BOTH IMPLEMENTATIONS AND COMPARED. System.Data code routinely holds
        /// several rows from NewRow at once - build them all, decide, then add the ones that survived - and appends
        /// directly in between. Every observable of that script has to match, which is why this asserts against a
        /// real System.Data.DataTable rather than against remembered behaviour.
        /// </summary>
        [Fact]
        public void ManyDetachedRowsBehaveTheSameAsSystemData()
        {
            DataTable liteTable = BuildLiteTable();
            System.Data.DataTable frameworkTable = BuildFrameworkTable();

            DataRow[] liteRows = new DataRow[5];
            System.Data.DataRow[] frameworkRows = new System.Data.DataRow[5];
            for (Int32 rowNumber = 0; rowNumber < liteRows.Length; rowNumber++)
            {
                liteRows[rowNumber] = liteTable.NewRow();
                liteRows[rowNumber]["Id"] = rowNumber;
                liteRows[rowNumber]["Name"] = "row" + rowNumber;
                frameworkRows[rowNumber] = frameworkTable.NewRow();
                frameworkRows[rowNumber]["Id"] = rowNumber;
                frameworkRows[rowNumber]["Name"] = "row" + rowNumber;
            }

            // Detached rows are readable and writable, and none of them is part of the table.
            Assert.Equal(frameworkTable.Rows.Count, liteTable.Rows.Count);
            Assert.Equal(0, liteTable.Rows.Count);
            Assert.Equal(frameworkRows[3]["Name"], liteRows[3]["Name"]);
            Assert.Equal(frameworkRows[3].RowState, liteRows[3].RowState);
            Assert.Equal(frameworkTable.Rows.IndexOf(frameworkRows[3]), liteTable.Rows.IndexOf(liteRows[3]));

            // Add some of them in creation order and abandon the rest - the conditional-build pattern, which gives
            // System.Data's table exactly.
            liteTable.Rows.Add(liteRows[0]);
            frameworkTable.Rows.Add(frameworkRows[0]);
            liteTable.Rows.Add(liteRows[2]);
            frameworkTable.Rows.Add(frameworkRows[2]);
            liteTable.Rows.Add(liteRows[4]);
            frameworkTable.Rows.Add(frameworkRows[4]);

            Assert.Equal(frameworkTable.Rows.Count, liteTable.Rows.Count);
            AssertSameNameColumn(frameworkTable, liteTable);

            // A plain append afterwards lands at the end in both.
            liteTable.Rows.Add(99, "direct", 1m);
            frameworkTable.Rows.Add(99, "direct", 1m);
            AssertSameNameColumn(frameworkTable, liteTable);

            // A handle taken before Add still addresses the row it was taken for, in both.
            liteRows[2]["Name"] = "written-after-add";
            frameworkRows[2]["Name"] = "written-after-add";
            AssertSameNameColumn(frameworkTable, liteTable);

            // The two that were never added stay out of the table, in both.
            Assert.Equal(frameworkRows[1].RowState, liteRows[1].RowState);
            Assert.Equal(System.Data.DataRowState.Detached, liteRows[1].RowState);
            Assert.Equal(frameworkTable.Rows.Count, liteTable.Rows.Count);

            // And one of the abandoned ones can still be brought in at the end, matching what System.Data does when
            // a long-held detached row is finally added.
            DataRow relocated = liteTable.Rows.AddAtEnd(liteRows[1]);
            frameworkTable.Rows.Add(frameworkRows[1]);
            AssertSameNameColumn(frameworkTable, liteTable);
            Assert.Equal(liteTable.Rows.Count - 1, liteTable.Rows.IndexOf(relocated));
        }

        /// <summary>
        /// THE ONE DIVERGENCE, PINNED AGAINST THE REAL THING. A row's position here is its storage slot's position,
        /// so rows appear in the order NewRow created them; System.Data orders by the order they were added. This
        /// test runs the reordering script against both and asserts that the library REFUSES where System.Data would
        /// have produced a different order - so the divergence can never be silent - and that the workaround the
        /// exception names reproduces System.Data's answer exactly.
        /// </summary>
        [Fact]
        public void AddingDetachedRowsOutOfOrderIsRefusedRatherThanSilentlyReordered()
        {
            DataTable liteTable = BuildLiteTable();
            System.Data.DataTable frameworkTable = BuildFrameworkTable();

            DataRow liteFirst = liteTable.NewRow();
            liteFirst["Id"] = 1;
            liteFirst["Name"] = "created-first";
            DataRow liteSecond = liteTable.NewRow();
            liteSecond["Id"] = 2;
            liteSecond["Name"] = "created-second";

            System.Data.DataRow frameworkFirst = frameworkTable.NewRow();
            frameworkFirst["Id"] = 1;
            frameworkFirst["Name"] = "created-first";
            System.Data.DataRow frameworkSecond = frameworkTable.NewRow();
            frameworkSecond["Id"] = 2;
            frameworkSecond["Name"] = "created-second";

            // System.Data puts the later-created row first, because it was added first.
            frameworkTable.Rows.Add(frameworkSecond);
            frameworkTable.Rows.Add(frameworkFirst);
            Assert.Equal("created-second", frameworkTable.Rows[0]["Name"]);
            Assert.Equal("created-first", frameworkTable.Rows[1]["Name"]);

            // The library cannot represent that order in place, so it says so instead of producing a different table.
            liteTable.Rows.Add(liteSecond);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => liteTable.Rows.Add(liteFirst));
            Assert.Contains("order", failure.Message);
            Assert.Contains("AddAtEnd", failure.Message);
            Assert.Equal(1, liteTable.Rows.Count);
            Assert.Equal(System.Data.DataRowState.Detached, liteFirst.RowState);

            // And the way out the message names produces System.Data's table exactly.
            liteTable.Rows.AddAtEnd(liteFirst);
            AssertSameNameColumn(frameworkTable, liteTable);
        }

        /// <summary>
        /// The SAME divergence reached the other way: a plain Rows.Add landing at the tail while a row created
        /// earlier is still detached. System.Data would put the detached row after the appended one; here the
        /// detached row's slot came first, so adding it in place would put it before. Refused, and AddAtEnd is again
        /// the exact way to get System.Data's answer.
        /// </summary>
        [Fact]
        public void AppendingDirectlyWhileARowIsDetachedIsRefusedRatherThanSilentlyReordered()
        {
            DataTable liteTable = BuildLiteTable();
            System.Data.DataTable frameworkTable = BuildFrameworkTable();

            DataRow liteDetached = liteTable.NewRow();
            liteDetached["Id"] = 1;
            liteDetached["Name"] = "detached-first";
            System.Data.DataRow frameworkDetached = frameworkTable.NewRow();
            frameworkDetached["Id"] = 1;
            frameworkDetached["Name"] = "detached-first";

            liteTable.Rows.Add(2, "appended", 5m);
            frameworkTable.Rows.Add(2, "appended", 5m);

            frameworkTable.Rows.Add(frameworkDetached);
            Assert.Throws<InvalidOperationException>(() => liteTable.Rows.Add(liteDetached));
            Assert.Equal(1, liteTable.Rows.Count);

            DataRow relocated = liteTable.Rows.AddAtEnd(liteDetached);
            AssertSameNameColumn(frameworkTable, liteTable);
            // The copy preserved both the written cell and the untouched, still-null one.
            Assert.Equal(1, relocated["Id"]);
            Assert.Equal(DBNull.Value, relocated["Price"]);
        }

        /// <summary>
        /// NewRow, populate, Rows.Add is the other canonical System.Data population pattern, and it behaves the same.
        /// </summary>
        [Fact]
        public void NewRowThenAddBehavesTheSame()
        {
            DataTable table = BuildLiteTable();
            DataRow row = table.NewRow();
            Assert.Equal(DataRowState.Detached, row.RowState);
            row["Id"] = 5;
            row["Name"] = "widget";
            table.Rows.Add(row);
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState);
            Assert.Equal(5, table.Rows[0]["Id"]);
            Assert.Equal(DBNull.Value, table.Rows[0]["Price"]);
        }

        /// <summary>
        /// Clone copies the schema and no rows; Copy copies both; the copy is independent of the original.
        /// </summary>
        [Fact]
        public void CloneAndCopyMatchSystemDataSemantics()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "widget", 9.5m);
            table.Rows.Add(2, "gadget", DBNull.Value);

            DataTable clone = table.Clone();
            Assert.Equal(3, clone.Columns.Count);
            Assert.Equal(0, clone.Rows.Count);
            Assert.Equal("Orders", clone.TableName);
            Assert.False(clone.Columns["Id"].AllowDBNull);

            DataTable copy = table.Copy();
            Assert.Equal(2, copy.Rows.Count);
            Assert.Equal("widget", copy.Rows[0]["Name"]);
            Assert.Equal(DBNull.Value, copy.Rows[1]["Price"]);
            DataRow copiedRow = copy.Rows[0];
            copiedRow["Name"] = "changed";
            Assert.Equal("widget", table.Rows[0]["Name"]);
        }

        /// <summary>
        /// Copy skips deleted rows, so the copy's rows are contiguous even when the original's physical slots are not.
        /// </summary>
        [Fact]
        public void CopySkipsDeletedRows()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "a", 1m);
            table.Rows.Add(2, "b", 2m);
            table.Rows.Add(3, "c", 3m);
            DataRow second = table.Rows[1];
            second.Delete();
            DataTable copy = table.Copy();
            Assert.Equal(2, copy.Rows.Count);
            Assert.Equal(1, copy.Rows[0]["Id"]);
            Assert.Equal(3, copy.Rows[1]["Id"]);
        }

        /// <summary>
        /// ImportRow copies a row between tables by column name, ignoring columns the destination does not have.
        /// </summary>
        [Fact]
        public void ImportRowCopiesByColumnName()
        {
            DataTable source = BuildLiteTable();
            source.Rows.Add(1, "widget", 9.5m);
            DataTable destination = new DataTable("Narrow");
            destination.Columns.Add<String>("Name", true);
            destination.Columns.Add<Boolean>("Active", true);
            destination.ImportRow(source.Rows[0]);
            Assert.Equal(1, destination.Rows.Count);
            Assert.Equal("widget", destination.Rows[0]["Name"]);
            Assert.Equal(DBNull.Value, destination.Rows[0]["Active"]);
        }

        /// <summary>
        /// row.Delete() works from the row itself, as System.Data code writes it, and the row then reports Deleted.
        /// </summary>
        [Fact]
        public void RowDeleteWorksFromTheRow()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "a", 1m);
            table.Rows.Add(2, "b", 2m);
            DataRow first = table.Rows[0];
            first.Delete();
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(DataRowState.Deleted, first.RowState);
            Assert.Equal(2, table.Rows[0]["Id"]);
        }

        /// <summary>
        /// Select with no filter returns every visible row, which is the one Select overload that needs no expression
        /// engine.
        /// </summary>
        [Fact]
        public void SelectWithoutAFilterReturnsEveryRow()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "a", 1m);
            table.Rows.Add(2, "b", 2m);
            DataRow[] selected = table.Select();
            Assert.Equal(2, selected.Length);
            Assert.Equal(1, selected[0]["Id"]);
            Assert.Equal(2, selected[1]["Id"]);
        }

        /// <summary>
        /// A default value is written into the cells of rows created after it was set, and costs nothing when no
        /// column has one.
        /// </summary>
        [Fact]
        public void DefaultValueIsAppliedToNewRows()
        {
            DataTable table = new DataTable("Orders");
            table.Columns.Add<Int32>("Id", false);
            DataColumn status = table.Columns.Add("Status", typeof(String), true);
            status.DefaultValue = "New";
            DataRow row = table.Rows.AddNewRow();
            Assert.Equal("New", row["Status"]);
            table.Rows.Add(7);
            Assert.Equal("New", table.Rows[1]["Status"]);
        }

        /// <summary>
        /// A default value that the column could never hold is refused where it is set, not on the first row that
        /// would have used it.
        /// </summary>
        [Fact]
        public void AnImpossibleDefaultValueIsRefusedWhereItIsSet()
        {
            DataTable table = new DataTable("Orders");
            DataColumn quantity = table.Columns.Add("Quantity", typeof(Int32), true);
            Assert.Throws<ArgumentException>(() => quantity.DefaultValue = "not a number");
            quantity.DefaultValue = DBNull.Value;
            Assert.Equal(DBNull.Value, quantity.DefaultValue);
        }

        /// <summary>
        /// A read-only column refuses writes through the compatibility surface with the exception System.Data raises,
        /// so an existing catch block keeps working.
        /// </summary>
        [Fact]
        public void ReadOnlyColumnsRefuseWritesWithReadOnlyException()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "widget", 9.5m);
            table.Columns["Name"].ReadOnly = true;
            DataRow row = table.Rows[0];
            Assert.Throws<ReadOnlyException>(() => row["Name"] = "changed");
            Assert.Throws<ReadOnlyException>(() => row[1] = "changed");
            Assert.Equal("widget", row["Name"]);
        }

        /// <summary>
        /// AllowDBNull can be turned on for a column that did not have it, and the rows that already existed keep
        /// reading as the values they hold rather than turning into nulls.
        /// </summary>
        [Fact]
        public void AllowDBNullCanBeTurnedOnWithoutLosingExistingValues()
        {
            DataTable table = new DataTable("Orders");
            DataColumn<Int32> quantity = table.Columns.Add<Int32>("Quantity", false);
            table.Rows.Add(1);
            table.Rows.Add(2);
            quantity.AllowDBNull = true;
            Assert.True(quantity.AllowDBNull);
            Assert.False(table.Rows[0].IsNull("Quantity"));
            Assert.Equal(1, table.Rows[0]["Quantity"]);
            Assert.Equal(2, table.Rows[1]["Quantity"]);
            DataRow row = table.Rows[1];
            row["Quantity"] = DBNull.Value;
            Assert.True(table.Rows[1].IsNull("Quantity"));
        }

        /// <summary>
        /// Turning AllowDBNull off is refused while a cell is actually null, naming the row - which is what
        /// System.Data does rather than silently replacing the nulls.
        /// </summary>
        [Fact]
        public void AllowDBNullCannotBeTurnedOffWhileANullExists()
        {
            DataTable table = new DataTable("Orders");
            DataColumn<Int32> quantity = table.Columns.Add<Int32>("Quantity", true);
            table.Rows.Add(1);
            table.Rows.Add(DBNull.Value);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => quantity.AllowDBNull = false);
            Assert.Contains("Quantity", failure.Message);
            Assert.True(quantity.AllowDBNull);
        }

        /// <summary>
        /// Clear empties the rows and keeps the schema, and the table stays usable afterwards.
        /// </summary>
        [Fact]
        public void ClearEmptiesRowsAndKeepsSchema()
        {
            DataTable lite = BuildLiteTable();
            lite.Rows.Add(1, "a", 1m);
            System.Data.DataTable framework = BuildFrameworkTable();
            framework.Rows.Add(1, "a", 1m);
            lite.Clear();
            framework.Clear();
            Assert.Equal(framework.Rows.Count, lite.Rows.Count);
            Assert.Equal(framework.Columns.Count, lite.Columns.Count);
            lite.Rows.Add(2, "b", 2m);
            Assert.Equal(1, lite.Rows.Count);
        }

        /// <summary>
        /// The properties a port merely reads or assigns - names, namespaces, capacity hints, extended properties -
        /// are honoured rather than refused, because refusing them would break code that is not asking for anything.
        /// </summary>
        [Fact]
        public void MetadataPropertiesAreHonoured()
        {
            DataTable table = BuildLiteTable();
            table.TableName = "Renamed";
            table.Namespace = "urn:test";
            table.Prefix = "t";
            table.MinimumCapacity = 5000;
            table.ExtendedProperties["Owner"] = "Nandika";
            table.BeginInit();
            Assert.False(table.IsInitialized);
            table.EndInit();
            Assert.True(table.IsInitialized);
            table.BeginLoadData();
            table.EndLoadData();
            table.AcceptChanges();
            Assert.Equal("Renamed", table.TableName);
            Assert.Equal("urn:test", table.Namespace);
            Assert.Equal("t", table.Prefix);
            Assert.Equal(5000, table.MinimumCapacity);
            Assert.Equal("Nandika", table.ExtendedProperties["Owner"]);
            Assert.False(table.HasErrors);
        }

        /// <summary>
        /// A table is disposable, so "using (DataTable table = ...)" compiles and runs.
        /// </summary>
        [Fact]
        public void TableIsDisposable()
        {
            using (DataTable table = BuildLiteTable())
            {
                table.Rows.Add(1, "a", 1m);
                Assert.Equal(1, table.Rows.Count);
            }
        }

        /// <summary>
        /// THE REFUSAL INVENTORY. Every DataTable member that needs machinery this library deliberately does not have
        /// throws NotImplementedException. Run your own code against this list before porting: anything here is a line
        /// you will have to change.
        /// </summary>
        [Fact]
        public void UnsupportedTableMembersThrowNotImplemented()
        {
            DataTable table = BuildLiteTable();
            Assert.Throws<NotImplementedException>(() => table.Constraints);
            Assert.Throws<NotImplementedException>(() => table.DefaultView);
            Assert.Throws<NotImplementedException>(() => table.ChildRelations);
            Assert.Throws<NotImplementedException>(() => table.ParentRelations);
            Assert.Throws<NotImplementedException>(() => table.Select("Id > 1"));
            Assert.Throws<NotImplementedException>(() => table.Select("Id > 1", "Id DESC"));
            Assert.Throws<NotImplementedException>(() => table.Compute("SUM(Id)", String.Empty));
            Assert.Throws<NotImplementedException>(() => table.GetChanges());
            Assert.Throws<NotImplementedException>(() => table.RejectChanges());
            Assert.Throws<NotImplementedException>(() => table.CaseSensitive = true);
            Assert.Throws<NotImplementedException>(() => table.PrimaryKey = new DataColumn[] { table.Columns["Id"] });
            Assert.Throws<NotImplementedException>(() => table.DisplayExpression = "Name");
            Assert.Throws<NotImplementedException>(() => table.Columns["Id"].Unique = true);
            Assert.Throws<NotImplementedException>(() => table.Columns["Id"].AutoIncrement = true);
            Assert.Throws<NotImplementedException>(() => table.Columns["Name"].MaxLength = 50);
            Assert.Throws<NotImplementedException>(() => table.Columns["Name"].Expression = "Id + 1");
            Assert.Throws<NotImplementedException>(() => table.Columns["Id"].DataType = typeof(Int64));
            Assert.Throws<NotImplementedException>(() => table.Rows.InsertAt(table.NewRow(), 0));
            Assert.Throws<NotImplementedException>(() => table.Rows.Find(1));
            Assert.Throws<NotImplementedException>(() => table.Rows.Contains(1));
        }

        /// <summary>
        /// Table events refuse subscription rather than accepting it and never firing, so a handler that would have
        /// been silently dead fails at wire-up instead.
        /// </summary>
        [Fact]
        public void TableEventsRefuseSubscription()
        {
            DataTable table = BuildLiteTable();
            Assert.Throws<NotImplementedException>(() => table.RowChanged += IgnoreRowChange);
            Assert.Throws<NotImplementedException>(() => table.RowChanging += IgnoreRowChange);
            Assert.Throws<NotImplementedException>(() => table.RowDeleted += IgnoreRowChange);
            Assert.Throws<NotImplementedException>(() => table.RowDeleting += IgnoreRowChange);
            Assert.Throws<NotImplementedException>(() => table.ColumnChanged += IgnoreColumnChange);
            Assert.Throws<NotImplementedException>(() => table.ColumnChanging += IgnoreColumnChange);
            Assert.Throws<NotImplementedException>(() => table.TableCleared += IgnoreTableClear);
            Assert.Throws<NotImplementedException>(() => table.TableClearing += IgnoreTableClear);
            Assert.Throws<NotImplementedException>(() => table.TableNewRow += IgnoreNewRow);
        }

        /// <summary>
        /// The row members that would need per-row change or error state refuse, while the ones whose answer is exact
        /// for this library - AcceptChanges, BeginEdit, EndEdit, HasVersion - answer.
        /// </summary>
        [Fact]
        public void UnsupportedRowMembersThrowAndExactOnesAnswer()
        {
            DataTable table = BuildLiteTable();
            table.Rows.Add(1, "a", 1m);
            DataRow row = table.Rows[0];
            row.AcceptChanges();
            row.BeginEdit();
            row.EndEdit();
            row.ClearErrors();
            Assert.True(row.HasVersion(DataRowVersion.Current));
            Assert.False(row.HasVersion(DataRowVersion.Original));
            Assert.False(row.HasErrors);
            Assert.Equal(String.Empty, row.RowError);
            Assert.Equal(String.Empty, row.GetColumnError(table.Columns["Name"]));
            Assert.Throws<NotImplementedException>(() => row.RejectChanges());
            Assert.Throws<NotImplementedException>(() => row.CancelEdit());
            Assert.Throws<NotImplementedException>(() => row.RowError = "bad");
            Assert.Throws<NotImplementedException>(() => row.SetColumnError(table.Columns["Name"], "bad"));
        }

        /// <summary>
        /// Addressing a cell with a column that belongs to a different table is refused rather than silently reading
        /// whatever sits at that row index in the other table's column.
        /// </summary>
        [Fact]
        public void ACellCannotBeAddressedWithAnotherTablesColumn()
        {
            DataTable table = BuildLiteTable();
            DataTable other = BuildLiteTable();
            table.Rows.Add(1, "a", 1m);
            other.Rows.Add(2, "b", 2m);
            DataRow row = table.Rows[0];
            DataColumn foreignColumn = other.Columns["Name"];
            ArgumentException failure = Assert.Throws<ArgumentException>(() => row[foreignColumn]);
            Assert.Contains("Name", failure.Message);
        }
        #endregion

        #region Private Methods
        // The three-column table most tests here start from.
        private static DataTable BuildLiteTable()
        {
            DataTable table = new DataTable("Orders");
            table.Columns.Add<Int32>("Id", false);
            table.Columns.Add<String>("Name", true);
            table.Columns.Add<Decimal>("Price", true);
            return table;
        }

        // Asserts that both tables hold the same Name values in the same order - the comparison that makes an
        // ordering claim about a row collection concrete.
        private static void AssertSameNameColumn(System.Data.DataTable frameworkTable, DataTable liteTable)
        {
            Assert.Equal(frameworkTable.Rows.Count, liteTable.Rows.Count);
            for (Int32 rowIndex = 0; rowIndex < frameworkTable.Rows.Count; rowIndex++)
            {
                Assert.Equal(frameworkTable.Rows[rowIndex]["Name"], liteTable.Rows[rowIndex]["Name"]);
                Assert.Equal(frameworkTable.Rows[rowIndex]["Id"], liteTable.Rows[rowIndex]["Id"]);
            }
        }

        // The same shape as a System.Data.DataTable, for the side-by-side assertions.
        private static System.Data.DataTable BuildFrameworkTable()
        {
            System.Data.DataTable table = new System.Data.DataTable("Orders");
            System.Data.DataColumn identifier = table.Columns.Add("Id", typeof(Int32));
            identifier.AllowDBNull = false;
            table.Columns.Add("Name", typeof(String));
            table.Columns.Add("Price", typeof(Decimal));
            return table;
        }

        // Handlers that exist only so the event-subscription refusals have something to subscribe.
        private static void IgnoreRowChange(Object sender, DataRowChangeEventArgs eventArguments)
        {
        }

        // Column-change handler, as above.
        private static void IgnoreColumnChange(Object sender, DataColumnChangeEventArgs eventArguments)
        {
        }

        // Table-clear handler, as above.
        private static void IgnoreTableClear(Object sender, DataTableClearEventArgs eventArguments)
        {
        }

        // New-row handler, as above.
        private static void IgnoreNewRow(Object sender, DataTableNewRowEventArgs eventArguments)
        {
        }
        #endregion
    }
}
