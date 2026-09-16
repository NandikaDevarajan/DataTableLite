///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The strongly typed column contract. Get/Set here are the zero-boxing, zero-type-check fast path: the whole
//   cost of deciding what type a column holds was paid once, when the column was created.
// Assumptions: Row indices are PHYSICAL storage slots. T is never a Nullable<T> - a nullable column of Int32 is
//   IDataColumn<Int32> with AllowDBNull true, not IDataColumn<Int32?>, so the value array stays four bytes per row
//   instead of eight.
// Design Considerations: Split into its own file from IDataColumn per coding standard section 1: they are two distinct
//   types, and generic and non-generic consumers use them for genuinely different purposes - the non-generic interface
//   for ordinal-driven walks, this one for resolved hot loops.
//   The interface exists so a future storage-backed column implementation can be substituted, but note that the
//   fastest available path is a concrete DataColumn<T> reference rather than this interface, since a sealed class's
//   Get/Set can be inlined where an interface call cannot always be. Both are allocation and boxing free.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data
{
    /// <summary>
    /// A column of the table with its element type known at compile time.
    /// </summary>
    /// <typeparam name="T">The column's element type.</typeparam>
    public interface IDataColumn<T> : IDataColumn
    {
        #region Public Methods
        /// <summary>
        /// Returns the typed cell value. No boxing, no type check.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The stored value.</returns>
        /// <exception cref="InvalidOperationException">The cell is null. Test <see cref="IDataColumn.IsNull"/> first,
        /// or use the GetOrDefault extension.</exception>
        T Get(Int32 rowIndex);

        /// <summary>
        /// Stores a typed cell value, clearing the cell's null state. No boxing, no type check.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The value to store.</param>
        void Set(Int32 rowIndex, T value);
        #endregion
    }
}
