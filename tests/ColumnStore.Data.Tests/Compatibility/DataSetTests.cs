///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies the DataSet container - naming, adding and removing tables, lookup, Clone, Copy and Clear -
//   against the behaviour System.Data.DataSet has, and pins the members that are refused rather than approximated.
// Assumptions: A table belongs to at most one set, and removing it from a set changes nothing about the table except
//   that it stops reporting one.
// Design Considerations: The refusal tests are as important as the capability tests. A NotImplementedException is a
//   deliberate, documented answer here, not a gap someone forgot to fill, so each one is asserted - if a future
//   change implements one of these for real, the test that fails is the reminder to update the documentation with it.
//   Merge is asserted with particular care. It is the member people assume works, and a Merge that appended instead
//   of matching would corrupt data silently rather than loudly; the test pins that it refuses.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using Xunit;

namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// Tests for <see cref="DataSet"/> and <see cref="DataTableCollection"/>.
    /// </summary>
    public sealed class DataSetTests
    {
        #region Public Methods
        /// <summary>
        /// A new set carries the same default name System.Data.DataSet uses, so code that reads DataSetName before
        /// setting it sees what it saw before.
        /// </summary>
        [Fact]
        public void DefaultNameMatchesSystemData()
        {
            DataSet lite = new DataSet();
            System.Data.DataSet framework = new System.Data.DataSet();
            Assert.Equal(framework.DataSetName, lite.DataSetName);
            Assert.Equal("NewDataSet", lite.DataSetName);
            Assert.Equal(0, lite.Tables.Count);
        }

        /// <summary>
        /// Tables are added by name, by instance and by generated name, and each is reachable by index and by name.
        /// </summary>
        [Fact]
        public void TablesCanBeAddedAndLookedUp()
        {
            DataSet set = new DataSet("Sales");
            DataTable orders = set.Tables.Add("Orders");
            DataTable customers = new DataTable("Customers");
            set.Tables.Add(customers);
            DataTable generated = set.Tables.Add();
            Assert.Equal(3, set.Tables.Count);
            Assert.Same(orders, set.Tables[0]);
            Assert.Same(customers, set.Tables["Customers"]);
            Assert.Same(customers, set.Tables["CUSTOMERS"]);
            Assert.Equal("Table3", generated.TableName);
            Assert.Equal(1, set.Tables.IndexOf("Customers"));
            Assert.True(set.Tables.Contains("Orders"));
            Assert.False(set.Tables.Contains("Missing"));
        }

        /// <summary>
        /// A table added to a set reports the set, and a removed one stops - while keeping every row, column and
        /// cached handle it had.
        /// </summary>
        [Fact]
        public void AddingAndRemovingATableAttachesAndDetachesIt()
        {
            DataSet set = new DataSet("Sales");
            DataTable orders = new DataTable("Orders");
            DataColumn<Int32> quantity = orders.Columns.Add<Int32>("Quantity", false);
            orders.Rows.Add(7);
            Assert.Null(orders.DataSet);
            set.Tables.Add(orders);
            Assert.Same(set, orders.DataSet);
            set.Tables.Remove(orders);
            Assert.Null(orders.DataSet);
            Assert.Equal(0, set.Tables.Count);
            Assert.Equal(1, orders.Rows.Count);
            Assert.Equal(7, quantity.Get(0));
        }

        /// <summary>
        /// Lookup failures behave as System.Data.DataTableCollection's do: an unknown name gives null, an
        /// out-of-range index throws IndexOutOfRangeException, and a null name is tolerated by Contains and IndexOf.
        /// </summary>
        [Fact]
        public void LookupFailuresMatchSystemData()
        {
            DataSet set = new DataSet("Sales");
            set.Tables.Add("Orders");
            Assert.Null(set.Tables["Missing"]);
            Assert.Throws<ArgumentNullException>(() => set.Tables[null]);
            Assert.Throws<IndexOutOfRangeException>(() => set.Tables[1]);
            Assert.Throws<IndexOutOfRangeException>(() => set.Tables[-1]);
            Assert.Equal(-1, set.Tables.IndexOf("Missing"));
            Assert.Equal(-1, set.Tables.IndexOf((String)null));
            Assert.False(set.Tables.Contains(null));
        }

        /// <summary>
        /// A table cannot belong to two sets, and two tables in one set cannot share a name.
        /// </summary>
        [Fact]
        public void DuplicateTablesAndNamesAreRefused()
        {
            DataSet first = new DataSet("First");
            DataSet second = new DataSet("Second");
            DataTable orders = first.Tables.Add("Orders");
            ArgumentException alreadyOwned = Assert.Throws<ArgumentException>(() => second.Tables.Add(orders));
            Assert.Contains("Orders", alreadyOwned.Message);
            ArgumentException duplicateName = Assert.Throws<ArgumentException>(() => first.Tables.Add("ORDERS"));
            Assert.Contains("ORDERS", duplicateName.Message);
            Assert.Equal(1, first.Tables.Count);
            Assert.Equal(0, second.Tables.Count);
        }

        /// <summary>
        /// Clone copies the schema of every table and no rows; the tables in the clone are new objects.
        /// </summary>
        [Fact]
        public void CloneCopiesEverySchemaAndNoRows()
        {
            DataSet set = BuildPopulatedSet();
            DataSet clone = set.Clone();
            Assert.Equal("Sales", clone.DataSetName);
            Assert.Equal(2, clone.Tables.Count);
            Assert.Equal(3, clone.Tables["Orders"].Columns.Count);
            Assert.Equal(0, clone.Tables["Orders"].Rows.Count);
            Assert.Equal(0, clone.Tables["Customers"].Rows.Count);
            Assert.NotSame(set.Tables["Orders"], clone.Tables["Orders"]);
            Assert.Equal(typeof(Decimal), clone.Tables["Orders"].Columns["Price"].DataType);
            Assert.False(clone.Tables["Orders"].Columns["Id"].AllowDBNull);
        }

        /// <summary>
        /// Copy carries the rows too, and the copy is independent: writing to one does not change the other.
        /// </summary>
        [Fact]
        public void CopyCarriesTheRowsAndIsIndependent()
        {
            DataSet set = BuildPopulatedSet();
            DataSet copy = set.Copy();
            Assert.Equal(2, copy.Tables["Orders"].Rows.Count);
            Assert.Equal("widget", copy.Tables["Orders"].Rows[0]["Name"]);
            Assert.Equal(9.5m, copy.Tables["Orders"].Rows[0]["Price"]);
            Assert.Equal(DBNull.Value, copy.Tables["Orders"].Rows[1]["Price"]);
            // NOTE the local. DataRow is a struct, so a cell cannot be assigned through the collection indexer in
            // one expression - see DropInCompatibilityTests.RowIsAStructSoCellsAreAssignedThroughALocal.
            DataRow copiedRow = copy.Tables["Orders"].Rows[0];
            copiedRow["Name"] = "changed";
            Assert.Equal("changed", copy.Tables["Orders"].Rows[0]["Name"]);
            Assert.Equal("widget", set.Tables["Orders"].Rows[0]["Name"]);
        }

        /// <summary>
        /// Clear empties every table of rows but keeps the schemas and the tables themselves.
        /// </summary>
        [Fact]
        public void ClearEmptiesTheRowsOfEveryTable()
        {
            DataSet set = BuildPopulatedSet();
            set.Clear();
            Assert.Equal(2, set.Tables.Count);
            Assert.Equal(0, set.Tables["Orders"].Rows.Count);
            Assert.Equal(3, set.Tables["Orders"].Columns.Count);
        }

        /// <summary>
        /// Reset drops the tables as well, returning the set to its just-constructed state.
        /// </summary>
        [Fact]
        public void ResetDropsTheTables()
        {
            DataSet set = BuildPopulatedSet();
            DataTable orders = set.Tables["Orders"];
            set.Reset();
            Assert.Equal(0, set.Tables.Count);
            Assert.Null(orders.DataSet);
        }

        /// <summary>
        /// AcceptChanges and HasChanges answer rather than throwing, because for this library their answers are exact:
        /// a write lands in column storage immediately, so nothing is ever pending.
        /// </summary>
        [Fact]
        public void ChangeStateAnswersAreExactRatherThanRefused()
        {
            DataSet set = BuildPopulatedSet();
            set.AcceptChanges();
            Assert.False(set.HasChanges());
            Assert.False(set.HasErrors);
            Assert.Equal(2, set.Tables["Orders"].Rows.Count);
        }

        /// <summary>
        /// Every member that would need relational machinery refuses out loud. Each of these is a deliberate answer,
        /// and each message says what to do instead - so this test is also the inventory of what a port has to change.
        /// </summary>
        [Fact]
        public void UnsupportedMembersThrowNotImplemented()
        {
            DataSet set = BuildPopulatedSet();
            Assert.Throws<NotImplementedException>(() => set.Relations);
            Assert.Throws<NotImplementedException>(() => set.Merge(new DataSet("Other")));
            Assert.Throws<NotImplementedException>(() => set.Merge(new DataTable("Other")));
            Assert.Throws<NotImplementedException>(() => set.GetChanges());
            Assert.Throws<NotImplementedException>(() => set.RejectChanges());
            Assert.Throws<NotImplementedException>(() => set.GetXml());
            Assert.Throws<NotImplementedException>(() => set.GetXmlSchema());
            Assert.Throws<NotImplementedException>(() => set.WriteXml("ignored.xml"));
            Assert.Throws<NotImplementedException>(() => set.ReadXml("ignored.xml"));
            Assert.Throws<NotImplementedException>(() => set.EnforceConstraints = true);
            Assert.Throws<NotImplementedException>(() => set.CaseSensitive = true);
        }

        /// <summary>
        /// The refusal messages name an alternative rather than just saying no, because the whole point of refusing
        /// loudly is that the caller learns what to do next.
        /// </summary>
        [Fact]
        public void RefusalMessagesSayWhatToDoInstead()
        {
            DataSet set = new DataSet("Sales");
            NotImplementedException merge = Assert.Throws<NotImplementedException>(() => set.Merge(new DataSet("Other")));
            Assert.Contains("ImportRow", merge.Message);
            NotImplementedException relations = Assert.Throws<NotImplementedException>(() => set.Relations);
            Assert.Contains("Join", relations.Message);
        }

        /// <summary>
        /// Setting a value these properties can honour is accepted; only the value that would need machinery the
        /// library does not have is refused. A blanket refusal would break code that merely assigns the default.
        /// </summary>
        [Fact]
        public void DefaultValuedAssignmentsToRefusedPropertiesAreAccepted()
        {
            DataSet set = new DataSet("Sales");
            set.CaseSensitive = false;
            set.EnforceConstraints = false;
            Assert.False(set.CaseSensitive);
            Assert.False(set.EnforceConstraints);
        }

        /// <summary>
        /// The set is usable in a using block and describes itself for diagnostics, both of which ported code relies
        /// on more often than it looks.
        /// </summary>
        [Fact]
        public void SetIsDisposableAndDescribesItself()
        {
            using (DataSet set = BuildPopulatedSet())
            {
                Assert.Contains("Sales", set.ToString());
                Assert.Contains("2", set.ToString());
            }
        }
        #endregion

        #region Private Methods
        // A two-table set with rows, used by most tests here.
        private static DataSet BuildPopulatedSet()
        {
            DataSet set = new DataSet("Sales");
            DataTable orders = set.Tables.Add("Orders");
            orders.Columns.Add<Int32>("Id", false);
            orders.Columns.Add<String>("Name", true);
            orders.Columns.Add<Decimal>("Price", true);
            orders.Rows.Add(1, "widget", 9.5m);
            orders.Rows.Add(2, "gadget", DBNull.Value);
            DataTable customers = set.Tables.Add("Customers");
            customers.Columns.Add<Int32>("Id", false);
            customers.Rows.Add(100);
            return set;
        }
        #endregion
    }
}
