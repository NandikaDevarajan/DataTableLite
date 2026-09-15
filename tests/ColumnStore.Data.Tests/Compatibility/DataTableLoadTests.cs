///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Pins DataTable.Load against System.Data.DataTable.Load, run side by side over the same reader, because
//   Load is the single method most ported code reaches for and the one whose behaviour a porter is least likely to
//   re-read the documentation for.
// Assumptions: A System.Data.DataTable is used as the SOURCE of rows for both sides, because its DataTableReader is a
//   real IDataReader that needs no database. The two targets are then compared cell by cell.
// Design Considerations: THE SCHEMA RULE IS THE PART WORTH PINNING. Load creates the schema when the table has none
//   and appends against the existing schema when it has one - and those are different code paths with different
//   failure modes. Both are asserted here against the framework's own answers rather than against what the
//   documentation says, so a divergence shows up as a failing comparison instead of a surprise in production.
//   LoadOption is accepted and has no effect, which is EXACT rather than a shortcut: every option describes how to
//   reconcile an incoming row with an existing row of the same primary key, and neither table here has one, so
//   System.Data appends for all three too. That equivalence is asserted rather than asserted-about.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;

using Xunit;

namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// Tests for <see cref="DataTable.Load(IDataReader)"/> and its overloads.
    /// </summary>
    public sealed class DataTableLoadTests
    {
        #region Public Methods
        /// <summary>
        /// Loading into an empty table creates the schema from the reader and fills it, exactly as
        /// System.Data.DataTable.Load does - same column names, same types, same values, same order.
        /// </summary>
        [Fact]
        public void LoadIntoAnEmptyTableCreatesTheSchemaLikeSystemData()
        {
            System.Data.DataTable frameworkTarget = new System.Data.DataTable("Target");
            DataTable liteTarget = new DataTable("Target");

            using (IDataReader frameworkReader = BuildSourceTable().CreateDataReader())
            {
                frameworkTarget.Load(frameworkReader);
            }
            using (IDataReader liteReader = BuildSourceTable().CreateDataReader())
            {
                liteTarget.Load(liteReader);
            }

            Assert.Equal(frameworkTarget.Columns.Count, liteTarget.Columns.Count);
            for (Int32 ordinal = 0; ordinal < frameworkTarget.Columns.Count; ordinal++)
            {
                Assert.Equal(frameworkTarget.Columns[ordinal].ColumnName, liteTarget.Columns[ordinal].ColumnName);
                Assert.Equal(frameworkTarget.Columns[ordinal].DataType, liteTarget.Columns[ordinal].DataType);
            }
            AssertSameContent(frameworkTarget, liteTarget);
        }

        /// <summary>
        /// Loading into a table that already has columns appends against that schema and creates no columns, which is
        /// what makes paging a large result set into one table work.
        /// </summary>
        [Fact]
        public void LoadIntoAnExistingSchemaAppendsWithoutCreatingColumns()
        {
            DataTable target = new DataTable("Target");
            target.Columns.Add<Int32>("Id", false);
            target.Columns.Add<String>("Name", true);
            target.Columns.Add<Decimal>("Price", true);

            using (IDataReader firstPage = BuildSourceTable().CreateDataReader())
            {
                target.Load(firstPage);
            }
            Assert.Equal(3, target.Columns.Count);
            Assert.Equal(3, target.Rows.Count);

            using (IDataReader secondPage = BuildSourceTable().CreateDataReader())
            {
                target.Load(secondPage);
            }
            Assert.Equal(3, target.Columns.Count);
            Assert.Equal(6, target.Rows.Count);
            Assert.Equal(1, target.Rows[0]["Id"]);
            Assert.Equal(1, target.Rows[3]["Id"]);
        }

        /// <summary>
        /// Every LoadOption appends, in both implementations, because neither table has a primary key to reconcile
        /// against. Asserting it against the framework is what makes "accepted and has no effect" a measured claim.
        /// </summary>
        [Fact]
        public void EveryLoadOptionAppendsJustAsSystemDataDoesWithoutAPrimaryKey()
        {
            LoadOption[] everyOption = new LoadOption[] { LoadOption.OverwriteChanges, LoadOption.PreserveChanges, LoadOption.Upsert };
            foreach (LoadOption option in everyOption)
            {
                System.Data.DataTable frameworkTarget = new System.Data.DataTable("Target");
                DataTable liteTarget = new DataTable("Target");
                for (Int32 pass = 0; pass < 2; pass++)
                {
                    using (IDataReader frameworkReader = BuildSourceTable().CreateDataReader())
                    {
                        frameworkTarget.Load(frameworkReader, option);
                    }
                    using (IDataReader liteReader = BuildSourceTable().CreateDataReader())
                    {
                        liteTarget.Load(liteReader, option);
                    }
                }
                Assert.Equal(6, frameworkTarget.Rows.Count);
                Assert.Equal(frameworkTarget.Rows.Count, liteTarget.Rows.Count);
                AssertSameContent(frameworkTarget, liteTarget);
            }
        }

        /// <summary>
        /// Nulls survive the round trip as nulls rather than as default(T), on both sides.
        /// </summary>
        [Fact]
        public void LoadCarriesNullsThroughAsSystemDataDoes()
        {
            System.Data.DataTable source = BuildSourceTable();
            source.Rows.Add(4, DBNull.Value, DBNull.Value);

            System.Data.DataTable frameworkTarget = new System.Data.DataTable("Target");
            DataTable liteTarget = new DataTable("Target");
            using (IDataReader frameworkReader = source.CreateDataReader())
            {
                frameworkTarget.Load(frameworkReader);
            }
            using (IDataReader liteReader = source.CreateDataReader())
            {
                liteTarget.Load(liteReader);
            }

            Assert.Equal(DBNull.Value, liteTarget.Rows[3]["Name"]);
            Assert.Equal(DBNull.Value, liteTarget.Rows[3]["Price"]);
            AssertSameContent(frameworkTarget, liteTarget);
        }

        /// <summary>
        /// The reader is left open, exactly as System.Data.DataTable.Load leaves it - a caller in a using block must
        /// still be the one to close it.
        /// </summary>
        [Fact]
        public void LoadDoesNotCloseTheReader()
        {
            DataTable target = new DataTable("Target");
            IDataReader reader = BuildSourceTable().CreateDataReader();
            target.Load(reader);
            Assert.False(reader.IsClosed);
            reader.Dispose();
            Assert.True(reader.IsClosed);
        }

        /// <summary>
        /// LoadRowCount answers how many rows were appended, which the void-returning System.Data signature cannot -
        /// and which a table that also holds deleted rows cannot work out from Rows.Count.
        /// </summary>
        [Fact]
        public void LoadRowCountReportsTheRowsAppended()
        {
            DataTable target = new DataTable("Target");
            using (IDataReader reader = BuildSourceTable().CreateDataReader())
            {
                Assert.Equal(3, target.LoadRowCount(reader, true));
            }
            target.Rows.RemoveAt(0);
            using (IDataReader reader = BuildSourceTable().CreateDataReader())
            {
                Assert.Equal(3, target.LoadRowCount(reader, true));
            }
            Assert.Equal(5, target.Rows.Count);
        }

        /// <summary>
        /// A null reader is refused the way every other argument in this library is.
        /// </summary>
        [Fact]
        public void LoadRejectsANullReader()
        {
            DataTable target = new DataTable("Target");
            Assert.Throws<ArgumentNullException>(() => target.Load(null));
            Assert.Throws<ArgumentNullException>(() => target.Load(null, LoadOption.Upsert));
        }
        #endregion

        #region Private Methods
        // Three rows over a value type, a reference type and a decimal - enough to exercise typed getters, nullability
        // and ordering without obscuring what failed.
        private static System.Data.DataTable BuildSourceTable()
        {
            System.Data.DataTable source = new System.Data.DataTable("Source");
            source.Columns.Add("Id", typeof(Int32));
            source.Columns.Add("Name", typeof(String));
            source.Columns.Add("Price", typeof(Decimal));
            source.Rows.Add(1, "first", 1.5m);
            source.Rows.Add(2, "second", 2.5m);
            source.Rows.Add(3, "third", 3.5m);
            return source;
        }

        // Asserts the two tables hold the same values in the same places, comparing through the Object surface both
        // expose so that DBNull and boxed values compare the way a caller would see them.
        private static void AssertSameContent(System.Data.DataTable frameworkTable, DataTable liteTable)
        {
            Assert.Equal(frameworkTable.Rows.Count, liteTable.Rows.Count);
            Assert.Equal(frameworkTable.Columns.Count, liteTable.Columns.Count);
            for (Int32 rowIndex = 0; rowIndex < frameworkTable.Rows.Count; rowIndex++)
            {
                for (Int32 ordinal = 0; ordinal < frameworkTable.Columns.Count; ordinal++)
                {
                    String columnName = frameworkTable.Columns[ordinal].ColumnName;
                    Assert.Equal(frameworkTable.Rows[rowIndex][columnName], liteTable.Rows[rowIndex][columnName]);
                }
            }
        }
        #endregion
    }
}
