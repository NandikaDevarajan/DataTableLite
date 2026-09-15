///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The type-erased column contract - everything a consumer can do with a column without knowing its element
//   type at compile time.
// Assumptions: Row indices are PHYSICAL storage slots, not logical (post-deletion) row positions. Translation is the
//   row layer's job; a column has no concept of a deleted row.
// Design Considerations: IsNull deliberately lives here rather than on IDataColumn<T>. Every ordinal-driven consumer -
//   the IDataReader loader, the table-valued-parameter writer, the IDataReader adapter, DataRow.IsNull - needs to ask
//   "is this cell null" while walking columns by ordinal. If that question required the generic interface, each of
//   those callers would need a per-column type switch to answer it, which is exactly the per-row type dispatch this
//   library exists to eliminate. See 03_Design.md section 2.1.
//   GetValue/SetValue box value types by definition - they are the System.Data.DataTable compatibility surface, not
//   the fast path. Callers in a hot loop should resolve DataColumn<T> once and use its typed Get/Set instead.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data
{
    /// <summary>
    /// A column of the table, accessed without compile-time knowledge of its element type.
    /// </summary>
    public interface IDataColumn
    {
        #region Public Properties
        /// <summary>The column's name.</summary>
        String ColumnName { get; }

        /// <summary>The column's position in its table's schema, or -1 while it belongs to no table.</summary>
        Int32 Ordinal { get; }

        /// <summary>The CLR type of the column's values. Never a <see cref="System.Nullable{T}"/> type - nullability
        /// is expressed by <see cref="AllowDBNull"/> instead.</summary>
        Type DataType { get; }

        /// <summary>True when cells of this column may be null.</summary>
        Boolean AllowDBNull { get; }
        #endregion

        #region Public Methods
        /// <summary>
        /// Returns the cell value as an <see cref="Object"/>, or null when the cell is null. Boxes value types.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The boxed value, or null.</returns>
        Object GetValue(Int32 rowIndex);

        /// <summary>
        /// Sets the cell from an <see cref="Object"/>. Null and <see cref="DBNull"/> set the cell to null; a value of
        /// the column's type is stored directly; a convertible value of another type is converted.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The value to store.</param>
        void SetValue(Int32 rowIndex, Object value);

        /// <summary>
        /// True when the cell holds no value. Always false for a column that does not allow null.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>True when the cell is null.</returns>
        Boolean IsNull(Int32 rowIndex);

        /// <summary>
        /// Releases all of this column's data. The column itself, its name, type and nullability are unaffected.
        /// </summary>
        void Clear();
        #endregion
    }
}
