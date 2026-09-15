///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: The swappable seam behind the compatibility suite (03_Design.md section 6.7). One shared body of test
//   logic runs against both System.Data.DataTable and ColumnStore.Data.DataTable through this interface, so "the two
//   behave the same for the overlapping API surface" is a measured claim rather than an assertion in a document.
// Assumptions: Only the OVERLAPPING surface is modelled here. Nothing in this interface may express something one of
//   the two tables cannot do - the moment it did, the suite would stop being a comparison.
// Design Considerations: A harness is used rather than a type alias because the two APIs are near-identical in shape
//   but not literally interchangeable in C# - Columns.Add returns different types and rows are a class in one and a
//   struct in the other. Normalising exactly those syntactic differences here, and nowhere else, means every
//   remaining difference the suite finds is a real behavioural difference rather than a shape difference.
//   NOTHING ABOUT VALUES IS NORMALISED ANY MORE. GetCell used to convert System.Data's DBNull into null so the two
//   could be compared; now that ColumnStore.Data reports DBNull.Value from the row indexer exactly as System.Data
//   does, that conversion would HIDE the very thing these tests exist to prove. The raw value from each
//   implementation is compared directly.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// The subset of table behaviour that both <see cref="System.Data.DataTable"/> and
    /// <see cref="ColumnStore.Data.DataTable"/> support, expressed once so a single test body can drive either.
    /// </summary>
    public interface ITableHarness
    {
        #region Public Properties
        /// <summary>Which implementation this harness drives, for test failure messages.</summary>
        String ImplementationName { get; }

        /// <summary>Number of visible rows.</summary>
        Int32 RowCount { get; }

        /// <summary>Number of columns.</summary>
        Int32 ColumnCount { get; }
        #endregion

        #region Public Methods
        /// <summary>
        /// Adds a column by runtime type.
        /// </summary>
        /// <param name="name">Column name.</param>
        /// <param name="dataType">Column CLR type.</param>
        /// <param name="allowNull">Whether cells may be null.</param>
        void AddColumn(String name, Type dataType, Boolean allowNull);

        /// <summary>
        /// Appends a row populated positionally, the way <c>Rows.Add(params Object[])</c> does in both APIs.
        /// </summary>
        /// <param name="values">Cell values in column order.</param>
        void AddRow(Object[] values);

        /// <summary>
        /// Appends a row using the two-step flow: reserve, write fields by name, commit.
        /// </summary>
        /// <param name="valuesByColumnName">Field values keyed by column name.</param>
        void AddRowInTwoSteps(Dictionary<String, Object> valuesByColumnName);

        /// <summary>
        /// Reads a cell, with a null cell normalised to null in both implementations - the one documented
        /// behavioural difference between them on this path.
        /// </summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <returns>The raw cell value, exactly as that implementation's row indexer returns it - DBNull.Value for
        /// a null cell in both.</returns>
        Object GetCell(Int32 rowIndex, String columnName);

        /// <summary>
        /// Writes a cell through the Object-based surface.
        /// </summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <param name="value">The value to write. Null sets the cell to null.</param>
        void SetCell(Int32 rowIndex, String columnName, Object value);

        /// <summary>
        /// True when the cell is null.
        /// </summary>
        /// <param name="rowIndex">Visible row position.</param>
        /// <param name="columnName">Column name.</param>
        /// <returns>True when null.</returns>
        Boolean IsCellNull(Int32 rowIndex, String columnName);

        /// <summary>
        /// Collects one column's values across every visible row by iterating rows, not by indexing them - so the
        /// comparison covers each implementation's iteration path.
        /// </summary>
        /// <param name="columnName">Column name.</param>
        /// <returns>The values in visible row order, nulls normalised to null.</returns>
        List<Object> EnumerateColumn(String columnName);

        /// <summary>
        /// Sums an Int32 column using each implementation's fastest strongly typed access path.
        /// </summary>
        /// <param name="columnName">Column name.</param>
        /// <returns>The sum.</returns>
        Int64 SumInt32Column(String columnName);

        /// <summary>
        /// Removes the visible row at the given position.
        /// </summary>
        /// <param name="rowIndex">Visible row position.</param>
        void DeleteRow(Int32 rowIndex);

        /// <summary>
        /// Removes every row, keeping the schema.
        /// </summary>
        void Clear();
        #endregion
    }
}
