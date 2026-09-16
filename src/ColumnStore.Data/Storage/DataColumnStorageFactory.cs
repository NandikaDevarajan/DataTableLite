///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The single decision point for "which storage implementation does a column of T get". Every DataColumn<T>
//   obtains its value storage from here and nowhere else.
// Assumptions: Called once per column, at column-creation time. Never on a per-row or per-cell path.
// Design Considerations: Centralising the choice here is what makes FR-3 - Boolean columns are bit-packed, always - a
//   structural guarantee rather than a convention. Because DataColumn<T>'s constructor is the only caller and this is
//   its only source of storage, there is no code path anywhere in the library, generic or reflective, that can produce
//   a Boolean column backed by one array slot per row. The Boolean test is a typeof comparison against a generic type
//   parameter, which the JIT resolves to a constant for each instantiation and eliminates entirely - it costs nothing
//   even though it reads like a runtime branch. See 03_Design.md sections 1.3 and 2.2.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace ColumnStore.Data.Storage
{
    /// <summary>
    /// Creates the value storage for a typed column.
    /// </summary>
    public static class DataColumnStorageFactory
    {
        #region Public Methods
        /// <summary>
        /// Returns the storage implementation appropriate for <typeparamref name="T"/>: bit-packed
        /// <see cref="BitmapColumnStorage"/> for <see cref="Boolean"/>, chunked
        /// <see cref="TypedColumnStorage{T}"/> for everything else.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <returns>Empty storage for one column.</returns>
        public static IDataColumnStorage<T> Create<T>()
        {
            if (typeof(T) == typeof(Boolean))
            {
                BitmapColumnStorage bitmapStorage = new BitmapColumnStorage();
                // BitmapColumnStorage implements IDataColumnStorage<Boolean>, and T is known to be Boolean here, but
                // the compiler cannot see that through the open type parameter. The cast through Object is the
                // standard way to express it and is free at runtime for this instantiation.
                Object untypedStorage = bitmapStorage;
                return (IDataColumnStorage<T>)untypedStorage;
            }
            TypedColumnStorage<T> chunkedStorage = new TypedColumnStorage<T>();
            return chunkedStorage;
        }

        /// <summary>
        /// Returns the storage implementation appropriate for <typeparamref name="T"/> with an explicit chunk row
        /// count. The chunk size is meaningless for <see cref="Boolean"/> columns, which are always bit-packed, and is
        /// ignored for them.
        /// </summary>
        /// <typeparam name="T">The column's element type.</typeparam>
        /// <param name="chunkRowCount">Rows per chunk. Must be a positive power of two.</param>
        /// <returns>Empty storage for one column.</returns>
        public static IDataColumnStorage<T> Create<T>(Int32 chunkRowCount)
        {
            if (typeof(T) == typeof(Boolean))
            {
                IDataColumnStorage<T> bitmapStorage = Create<T>();
                return bitmapStorage;
            }
            TypedColumnStorage<T> chunkedStorage = new TypedColumnStorage<T>(chunkRowCount);
            return chunkedStorage;
        }
        #endregion
    }
}
