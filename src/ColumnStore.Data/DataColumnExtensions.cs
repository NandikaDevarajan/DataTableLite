///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Nullable<T> interop and default-on-null reads for typed columns, as extension methods.
// Assumptions: Row indices are PHYSICAL storage slots, matching DataColumn<T>'s own API.
// Design Considerations: These live outside DataColumn<T> on purpose (03_Design.md section 2.2). If the column class
//   itself understood Nullable<T>, every instantiation would carry that machinery whether or not the column is
//   nullable, and DataColumn<Int32> would need a policy for what T? means when T is already a reference type. As
//   extensions they cost nothing at all to the columns that never use them, and the Nullable-specific overloads can
//   carry the "where T : struct" constraint that the class cannot.
//   GetOrDefault deliberately reads as the exception rather than the rule: DataColumn<T>.Get throws on a null cell
//   (FR-9) so that a null never silently becomes a zero. A caller who genuinely wants zero says so here, at the call
//   site, where a reviewer can see the decision.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data
{
    /// <summary>
    /// Convenience accessors over <see cref="DataColumn{T}"/> for null-tolerant and <see cref="System.Nullable{T}"/>
    /// style access.
    /// </summary>
    public static class DataColumnExtensions
    {
        #region Public Methods
        /// <summary>
        /// Returns the cell value, or <c>default(T)</c> when the cell is null - the opposite of
        /// <see cref="DataColumn{T}.Get"/>, which throws.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="column">The column.</param>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The stored value, or <c>default(T)</c>.</returns>
        /// <exception cref="ArgumentNullException">The column is null.</exception>
        public static T GetOrDefault<T>(this DataColumn<T> column, Int32 rowIndex)
        {
            if (column == null) { throw new ArgumentNullException(nameof(column)); }
            Boolean isNull = column.IsNull(rowIndex);
            if (isNull) { return default(T); }
            return column.Get(rowIndex);
        }

        /// <summary>
        /// Returns the cell value, or <paramref name="fallbackValue"/> when the cell is null.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="column">The column.</param>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="fallbackValue">Value to return for a null cell.</param>
        /// <returns>The stored value, or the fallback.</returns>
        /// <exception cref="ArgumentNullException">The column is null.</exception>
        public static T GetOrDefault<T>(this DataColumn<T> column, Int32 rowIndex, T fallbackValue)
        {
            if (column == null) { throw new ArgumentNullException(nameof(column)); }
            Boolean isNull = column.IsNull(rowIndex);
            if (isNull) { return fallbackValue; }
            return column.Get(rowIndex);
        }

        /// <summary>
        /// Returns the cell value as a <see cref="System.Nullable{T}"/>, with a null cell becoming a null
        /// <see cref="System.Nullable{T}"/>. Note that the column's storage remains unwidened - the
        /// <see cref="System.Nullable{T}"/> exists only for the duration of the call.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="column">The column.</param>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The stored value, or null.</returns>
        /// <exception cref="ArgumentNullException">The column is null.</exception>
        public static Nullable<T> GetNullable<T>(this DataColumn<T> column, Int32 rowIndex) where T : struct
        {
            if (column == null) { throw new ArgumentNullException(nameof(column)); }
            Boolean isNull = column.IsNull(rowIndex);
            if (isNull) { return null; }
            T value = column.Get(rowIndex);
            return value;
        }

        /// <summary>
        /// Stores a <see cref="System.Nullable{T}"/>, routing a null to
        /// <see cref="DataColumn{T}.SetNull"/> and a value to <see cref="DataColumn{T}.Set"/>.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="column">The column.</param>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The value to store, or null.</param>
        /// <exception cref="ArgumentNullException">The column is null.</exception>
        /// <exception cref="InvalidOperationException">The value is null and the column does not allow null.</exception>
        public static void Set<T>(this DataColumn<T> column, Int32 rowIndex, Nullable<T> value) where T : struct
        {
            if (column == null) { throw new ArgumentNullException(nameof(column)); }
            if (value.HasValue == false)
            {
                column.SetNull(rowIndex);
                return;
            }
            T storedValue = value.Value;
            column.Set(rowIndex, storedValue);
        }
        #endregion
    }
}
