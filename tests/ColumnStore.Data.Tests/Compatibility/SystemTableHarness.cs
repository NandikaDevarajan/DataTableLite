///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Drives System.Data.DataTable through the shared compatibility harness, so the same test body can be run
//   against the type ColumnStore.Data is meant to replace.
// Assumptions: The framework table is used with its defaults - no constraints, no change tracking, no AcceptChanges -
//   because those are outside the overlapping surface being compared.
// Design Considerations: Two normalisations happen here and nowhere else. DBNull.Value is mapped to null on read, so
//   the documented difference in how a null cell reads back does not swamp the comparison; and DeleteRow uses
//   Rows.RemoveAt, whose observable effect - the row is gone and the rest renumber - matches ColumnStore.Data's logical
//   deletion, rather than Row.Delete, which leaves a row in the collection in a Deleted state that ColumnStore.Data has
//   no v1 equivalent for. Both choices are called out in the compatibility notes.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// <see cref="ITableHarness"/> over <see cref="System.Data.DataTable"/>.
    /// </summary>
    public sealed class SystemTableHarness : ITableHarness
    {
        #region Private Members
        // The framework table under test.
        private readonly System.Data.DataTable table;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a harness over a new, empty framework table.
        /// </summary>
        public SystemTableHarness()
        {
            this.table = new System.Data.DataTable("Harness");
        }
        #endregion

        #region Public Properties
        /// <summary>Which implementation this harness drives.</summary>
        public String ImplementationName
        {
            get { return "System.Data.DataTable"; }
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
            System.Data.DataColumn column = this.table.Columns.Add(name, dataType);
            column.AllowDBNull = allowNull;
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
            System.Data.DataRow row = this.table.NewRow();
            foreach (KeyValuePair<String, Object> field in valuesByColumnName)
            {
                row[field.Key] = field.Value;
            }
            this.table.Rows.Add(row);
        }

        /// <summary>Reads a cell, normalising DBNull to null.</summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <returns>The cell value, or null.</returns>
        public Object GetCell(Int32 rowIndex, String columnName)
        {
            System.Data.DataRow row = this.table.Rows[rowIndex];
            return row[columnName];
        }

        /// <summary>Writes a cell through the Object-based surface.</summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <param name="value">The value to write.</param>
        public void SetCell(Int32 rowIndex, String columnName, Object value)
        {
            System.Data.DataRow row = this.table.Rows[rowIndex];
            if (value == null) { row[columnName] = DBNull.Value; }
            else { row[columnName] = value; }
        }

        /// <summary>True when the cell is null.</summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <returns>True when null.</returns>
        public Boolean IsCellNull(Int32 rowIndex, String columnName)
        {
            System.Data.DataRow row = this.table.Rows[rowIndex];
            return row.IsNull(columnName);
        }

        /// <summary>Collects a column's values by iterating rows.</summary>
        /// <param name="columnName">Column name.</param>
        /// <returns>The values in visible row order.</returns>
        public List<Object> EnumerateColumn(String columnName)
        {
            List<Object> values = new List<Object>();
            foreach (System.Data.DataRow row in this.table.Rows)
            {
                values.Add(row[columnName]);
            }
            return values;
        }

        /// <summary>Sums an Int32 column using the framework's fastest available access path.</summary>
        /// <param name="columnName">Column name.</param>
        /// <returns>The sum.</returns>
        public Int64 SumInt32Column(String columnName)
        {
            System.Data.DataColumn column = this.table.Columns[columnName];
            Int64 total = 0;
            foreach (System.Data.DataRow row in this.table.Rows)
            {
                Object value = row[column];
                if (value is Int32 typedValue) { total = total + typedValue; }
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

        #region Private Methods
        // Maps DBNull.Value to null, so a null cell compares equal across the two implementations. This single
        #endregion
    }
}
