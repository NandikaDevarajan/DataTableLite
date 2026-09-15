///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Drives ColumnStore.Data.DataTable through the shared compatibility harness.
// Assumptions: A DataRow is a struct view, so it is fetched fresh wherever one is needed rather than cached - which
//   is exactly how a consumer should use it.
// Design Considerations: Every method here is deliberately written the way a caller migrating from
//   System.Data.DataTable would write it, so that the compatibility suite exercises the migration path and not some
//   internal shortcut. SumInt32Column is the one place it uses the library's own preferred idiom - a column handle
//   resolved once outside the loop - because that is the API's answer to the same question, and comparing the two
//   fastest paths is more informative than comparing two identical slow ones.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// <see cref="ITableHarness"/> over <see cref="ColumnStore.Data.DataTable"/>.
    /// </summary>
    public sealed class LiteTableHarness : ITableHarness
    {
        #region Private Members
        // The table under test.
        private readonly DataTable table;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a harness over a new, empty table.
        /// </summary>
        public LiteTableHarness()
        {
            this.table = new DataTable("Harness");
        }
        #endregion

        #region Public Properties
        /// <summary>Which implementation this harness drives.</summary>
        public String ImplementationName
        {
            get { return "ColumnStore.Data.DataTable"; }
        }

        /// <summary>Number of visible rows.</summary>
        public Int32 RowCount
        {
            get { return this.table.Rows.Count; }
        }

        /// <summary>Number of columns.</summary>
        public Int32 ColumnCount
        {
            get { return this.table.Columns.Count; }
        }
        #endregion

        #region Public Methods
        /// <summary>Adds a column by runtime type.</summary>
        /// <param name="name">Column name.</param>
        /// <param name="dataType">Column CLR type.</param>
        /// <param name="allowNull">Whether cells may be null.</param>
        public void AddColumn(String name, Type dataType, Boolean allowNull)
        {
            this.table.Columns.Add(name, dataType, allowNull);
        }

        /// <summary>Appends a row populated positionally.</summary>
        /// <param name="values">Cell values in column order.</param>
        public void AddRow(Object[] values)
        {
            this.table.Rows.Add(values);
        }

        /// <summary>Appends a row using reserve, write, commit.</summary>
        /// <param name="valuesByColumnName">Field values keyed by column name.</param>
        public void AddRowInTwoSteps(Dictionary<String, Object> valuesByColumnName)
        {
            DataRow row = this.table.NewRow();
            foreach (KeyValuePair<String, Object> field in valuesByColumnName)
            {
                row[field.Key] = field.Value;
            }
            this.table.Rows.Add(row);
        }

        /// <summary>Reads a cell. A null cell reads back as DBNull.Value, as System.Data's does.</summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <returns>The cell value, or DBNull.Value.</returns>
        public Object GetCell(Int32 rowIndex, String columnName)
        {
            DataRow row = this.table.Rows[rowIndex];
            return row[columnName];
        }

        /// <summary>Writes a cell through the Object-based surface.</summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <param name="value">The value to write.</param>
        public void SetCell(Int32 rowIndex, String columnName, Object value)
        {
            DataRow row = this.table.Rows[rowIndex];
            row[columnName] = value;
        }

        /// <summary>True when the cell is null.</summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <returns>True when null.</returns>
        public Boolean IsCellNull(Int32 rowIndex, String columnName)
        {
            DataRow row = this.table.Rows[rowIndex];
            return row.IsNull(columnName);
        }

        /// <summary>Collects a column's values by iterating rows.</summary>
        /// <param name="columnName">Column name.</param>
        /// <returns>The values in visible row order.</returns>
        public List<Object> EnumerateColumn(String columnName)
        {
            List<Object> values = new List<Object>();
            foreach (DataRow row in this.table.Rows)
            {
                values.Add(row[columnName]);
            }
            return values;
        }

        /// <summary>Sums an Int32 column using a column handle resolved once, outside the loop.</summary>
        /// <param name="columnName">Column name.</param>
        /// <returns>The sum.</returns>
        public Int64 SumInt32Column(String columnName)
        {
            DataColumn<Int32> column = this.table.Columns.GetColumn<Int32>(columnName);
            Int64 total = 0;
            foreach (DataRow row in this.table.Rows)
            {
                total = total + column.GetOrDefault(row.RowIndex);
            }
            return total;
        }

        /// <summary>Removes the visible row at the given position.</summary>
        /// <param name="rowIndex">Visible row position.</param>
        public void DeleteRow(Int32 rowIndex)
        {
            this.table.Rows.RemoveAt(rowIndex);
        }

        /// <summary>Removes every row, keeping the schema.</summary>
        public void Clear()
        {
            this.table.Clear();
        }
        #endregion
    }
}
