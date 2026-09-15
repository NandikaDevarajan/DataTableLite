///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The storage seam. Defines the whole contract the column layer needs from a value store: index in, value out;
//   index in, value stored. Nothing about column names, schemas, rows or nullability appears here.
// Assumptions: Single-writer access; no implementation locks. Row indices are always >= 0 and are PHYSICAL indices -
//   logical (post-deletion) row positions are translated to physical ones one layer up, by RowDeletionTracker.
// Design Considerations: Kept to Get/Set/Clear exactly as specified in 03_Design.md section 1.1 so that a future
//   sparse, compressed or memory-mapped storage can be dropped in without the column or row layers changing. Null
//   tracking is deliberately absent: it is a per-column concern layered on top by DataColumn<T> via a separate
//   BitmapColumnStorage, which keeps this interface usable for stores that have no concept of nullability.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Storage
{
    /// <summary>
    /// Raw value storage for a single column of <typeparamref name="T"/>, addressed by physical row index.
    /// </summary>
    /// <typeparam name="T">The value type stored. Never <see cref="System.Nullable{T}"/> - nullability is tracked
    /// separately by <see cref="ColumnStore.Data.DataColumn{T}"/>.</typeparam>
    public interface IDataColumnStorage<T>
    {
        #region Public Methods
        /// <summary>
        /// Returns the value stored at <paramref name="rowIndex"/>.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The stored value.</returns>
        /// <exception cref="IndexOutOfRangeException">The index is negative, or beyond the storage this instance has
        /// been grown to.</exception>
        T Get(Int32 rowIndex);

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="rowIndex"/>, growing the storage if required.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The value to store.</param>
        /// <exception cref="IndexOutOfRangeException">The index is negative.</exception>
        void Set(Int32 rowIndex, T value);

        /// <summary>
        /// Releases all stored values and returns the instance to its initial, empty state.
        /// </summary>
        void Clear();
        #endregion
    }
}
