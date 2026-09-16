///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: One strongly typed column: a value store plus, for a nullable column, one bit per row of null tracking.
//   This is the type that makes the library's promise concrete - once a caller holds a DataColumn<T>, reading or
//   writing a cell is an array index and a bit test, with no boxing, no cast and no type dispatch of any kind.
// Assumptions: Single-writer access; not thread safe. Row indices are PHYSICAL storage slots - logical row positions
//   are translated by the row layer before they get here. T is never Nullable<T>: a nullable Int32 column is
//   DataColumn<Int32> with AllowDBNull true, so the value array stays four bytes per row.
// Design Considerations: CONSTRUCTION IS FACTORY-ONLY (constructors are internal), which coding standard section 4
//   permits when the reason is documented here. The reason is FR-3: bit-packed Boolean columns must be a structural
//   guarantee, not a convention. All storage comes from DataColumnStorageFactory, so no code path - generic,
//   reflective or otherwise - can produce a Boolean column that spends a byte per row. Use
//   DataColumnCollection.Add<T> or DataColumnFactory.Create.
//   NULL TRACKING IS "IS NULL": a SET bit means the cell is null, exactly as 03_Design.md section 1.3 specifies.
//   The point of that sense is that real data is overwhelmingly non-null, so the common path should not have to write
//   to the bitmap at all. Set therefore READS the null bit and clears it only when it is actually set - a load the
//   branch predictor gets right every time, instead of a store that dirties a cache line. For a nullable column that
//   never actually receives a null, the bitmap's word list is never grown: zero words allocated, zero writes.
//   IT IS NOT SAFE TO SKIP THE BITMAP ENTIRELY ON Set. Writing a value over a cell that was previously set null must
//   clear that cell's bit, or the cell keeps reading as null while holding a value. The conditional clear below is
//   what makes "usually skip the write" correct rather than merely fast.
//   THE PROBLEM THAT SENSE CREATES, AND HOW IT IS SOLVED. With no bit set for an untouched cell, an unwritten cell
//   would read as NOT null - so Get would hand back default(T), and for a reference type it would hand back an actual
//   null reference while claiming the cell has a value. That is not merely a compatibility wart: the table-valued
//   parameter writer would then call SqlDataRecord.SetString with a null, which throws a NullReferenceException from
//   inside Microsoft.Data.SqlClient. So the column also tracks highestWrittenRowIndex, the highest row it has ever
//   been asked to write. Any row above that watermark is null by definition - no bitmap lookup, no bit ever written -
//   and any row at or below it is decided by the bitmap. Creating a row therefore still costs nothing per column,
//   and an unpopulated cell still reads as null exactly as System.Data.DataTable reports DBNull.
//   The watermark would be an approximation in exactly one case - a caller who writes a HIGH row index and leaves
//   LOWER ones untouched, whose cells would sit below the watermark with no null bit and read as default(T) - so
//   advancing the watermark marks every row it jumps over as null. That closes the case completely and costs nothing
//   where it does not arise: sequential population, which is every loader, every Rows.Add and every
//   append-then-fill loop, jumps over no rows at all, so the fill loop never runs.
//   Get throws on a null cell rather than returning default(T) (FR-9): silently turning a null into zero is the class
//   of bug that survives into production. GetOrDefault and GetNullable in DataColumnExtensions serve callers who want
//   the other behaviour, explicitly.
//   See 03_Design.md section 2.2.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Globalization;
using System.Runtime.CompilerServices;

using ColumnStore.Data.Storage;

namespace ColumnStore.Data
{
    /// <summary>
    /// A strongly typed, column-oriented store for one column of a <see cref="DataTable"/>.
    /// </summary>
    /// <typeparam name="T">The column's element type. Never <see cref="System.Nullable{T}"/>.</typeparam>
    public sealed class DataColumn<T> : DataColumn, IDataColumn<T>
    {
        #region Private Constants
        // Watermark value meaning the column has never been written at any row, so every row is null.
        private const Int32 NEVER_WRITTEN = -1;
        #endregion

        #region Private Members
        // Whether cells may be null. Changed only through AllowDBNull, which also creates or drops nullFlags, so the
        // two are never out of step - and the hot path still branches on a null field reference the CPU predicts
        // perfectly, because within one load or one loop neither ever changes.
        private Boolean allowNull;

        // The value store: chunked arrays for every type except Boolean, which gets bit-packed storage.
        private readonly IDataColumnStorage<T> valueStorage;

        // One bit per row, SET when the cell IS NULL. A null reference for a non-nullable column, which therefore
        // pays no memory and no work for null tracking at all. See this file's header for why the sense is "is
        // null": it is what lets a non-null write skip the bitmap store entirely.
        private BitmapColumnStorage nullFlags;

        // Highest row index this column has ever been written at, or -1 when it has never been written. Rows above
        // it were never touched and are therefore null, which is what lets row creation cost nothing per column
        // while an unpopulated cell still reads as null. Only meaningful when nullFlags is present.
        private Int32 highestWrittenRowIndex;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a column with the storage recommended for <typeparamref name="T"/>. Internal by design - see this
        /// file's header; use <see cref="DataColumnCollection.Add{T}(String, Boolean)"/> or
        /// <see cref="DataColumnFactory.Create"/>.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <param name="allowNull">Whether cells may be null.</param>
        internal DataColumn(String name, Boolean allowNull)
            : base(name)
        {
            Contract.Assert(String.IsNullOrWhiteSpace(name) == false, "Column name must be supplied; DataColumnCollection validates it for public callers.");
            this.allowNull = allowNull;
            this.valueStorage = DataColumnStorageFactory.Create<T>();
            if (allowNull) { this.nullFlags = new BitmapColumnStorage(); }
            else { this.nullFlags = null; }
            this.highestWrittenRowIndex = NEVER_WRITTEN;
        }

        /// <summary>
        /// Creates a column with an explicit chunk row count, for unusual access patterns and for tests that need to
        /// force chunk-boundary conditions at small row counts. Internal by design - see this file's header.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <param name="allowNull">Whether cells may be null.</param>
        /// <param name="chunkRowCount">Rows per storage chunk. Must be a positive power of two.</param>
        internal DataColumn(String name, Boolean allowNull, Int32 chunkRowCount)
            : base(name)
        {
            Contract.Assert(String.IsNullOrWhiteSpace(name) == false, "Column name must be supplied; DataColumnCollection validates it for public callers.");
            this.allowNull = allowNull;
            this.valueStorage = DataColumnStorageFactory.Create<T>(chunkRowCount);
            if (allowNull) { this.nullFlags = new BitmapColumnStorage(); }
            else { this.nullFlags = null; }
            this.highestWrittenRowIndex = NEVER_WRITTEN;
        }
        #endregion

        #region Internal Properties
        /// <summary>
        /// The underlying value store. Exposed to the test assembly so the "Boolean columns are always bit-packed"
        /// guarantee (FR-3) can be asserted structurally rather than inferred from memory measurements.
        /// </summary>
        internal IDataColumnStorage<T> ValueStorage
        {
            get { return this.valueStorage; }
        }

        /// <summary>
        /// The is-null bitmap, or a null reference for a non-nullable column. A set bit means that row's cell is
        /// null. Exposed to the test assembly for the same reason as <see cref="ValueStorage"/>.
        /// </summary>
        internal BitmapColumnStorage NullFlags
        {
            get { return this.nullFlags; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Returns the typed cell value. This is the fast path: no boxing, no cast, no type check.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The stored value.</returns>
        /// <exception cref="InvalidOperationException">The cell is null.</exception>
        /// <exception cref="IndexOutOfRangeException">The index is negative, or beyond this column's storage.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get(Int32 rowIndex)
        {
            if (this.nullFlags != null)
            {
                Boolean isNull = rowIndex > this.highestWrittenRowIndex || this.nullFlags.Get(rowIndex);
                if (isNull) { throw new InvalidOperationException($"Column '{this.ColumnName}' is null at row index {rowIndex}. Test IsNull first, or use GetOrDefault."); }
            }
            return this.valueStorage.Get(rowIndex);
        }

        /// <summary>
        /// Stores a typed cell value, clearing the cell's null flag if it had one. This is the fast path: no boxing,
        /// no cast, no type check, and - for the overwhelmingly common case of writing a value into a cell that was
        /// not already null - no write to the null bitmap either.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The value to store.</param>
        /// <exception cref="IndexOutOfRangeException">The index is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(Int32 rowIndex, T value)
        {
            this.valueStorage.Set(rowIndex, value);
            if (this.nullFlags != null)
            {
                // Advance the watermark, then read the bit and clear it only if it was actually set. Skipping the
                // bitmap altogether would be faster still and would be wrong: a value written over a previously-null
                // cell has to clear that cell's bit, or the cell reads as null while holding a value. A load that
                // almost always finds a zero bit is far cheaper than an unconditional store, and for a column that
                // never receives a null the bitmap's word list is never allocated at all.
                if (rowIndex > this.highestWrittenRowIndex)
                {
                    if (rowIndex > this.highestWrittenRowIndex + 1) { MarkSkippedRowsNull(rowIndex); }
                    this.highestWrittenRowIndex = rowIndex;
                }
                Boolean wasNull = this.nullFlags.Get(rowIndex);
                if (wasNull) { this.nullFlags.Set(rowIndex, false); }
            }
        }

        /// <summary>
        /// Sets the cell to null.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <exception cref="InvalidOperationException">The column does not allow null.</exception>
        /// <exception cref="IndexOutOfRangeException">The index is negative.</exception>
        public void SetNull(Int32 rowIndex)
        {
            if (this.nullFlags == null) { throw new InvalidOperationException($"Column '{this.ColumnName}' does not allow null values."); }
            this.nullFlags.Set(rowIndex, true);
            if (rowIndex > this.highestWrittenRowIndex)
            {
                if (rowIndex > this.highestWrittenRowIndex + 1) { MarkSkippedRowsNull(rowIndex); }
                this.highestWrittenRowIndex = rowIndex;
            }
            // Only value types can be abandoned in place. A T that holds references must be overwritten, or the
            // column would keep the old object alive for as long as the table exists. IsReferenceOrContainsReferences
            // is a JIT-time constant, so the branch disappears entirely for the instantiation that does not need it.
            if (Bits.HoldsReferences<T>())
            {
                this.valueStorage.Set(rowIndex, default(T));
            }
        }

        /// <summary>
        /// True when the cell holds no value. Always false for a column that does not allow null.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>True when the cell is null.</returns>
        /// <exception cref="IndexOutOfRangeException">The index is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override Boolean IsNull(Int32 rowIndex)
        {
            if (rowIndex < 0) { throw new IndexOutOfRangeException($"Row Index: {rowIndex}"); }
            if (this.nullFlags == null) { return false; }
            if (rowIndex > this.highestWrittenRowIndex) { return true; }
            return this.nullFlags.Get(rowIndex);
        }

        /// <summary>
        /// Copies one cell from one physical row slot to another without boxing, preserving nullness.
        /// </summary>
        /// <param name="sourceRowIndex">Physical row index to copy from.</param>
        /// <param name="targetRowIndex">Physical row index to copy to.</param>
        public override void CopyCell(Int32 sourceRowIndex, Int32 targetRowIndex)
        {
            if (IsNull(sourceRowIndex))
            {
                SetNull(targetRowIndex);
                return;
            }
            Set(targetRowIndex, Get(sourceRowIndex));
        }

        /// <summary>
        /// Returns the cell value boxed as an <see cref="Object"/>, or null when the cell is null. Compatibility
        /// surface for <see cref="System.Data.DataTable"/>-shaped code; boxes value types on every call.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The boxed value, or null.</returns>
        public override Object GetValue(Int32 rowIndex)
        {
            if (this.nullFlags != null)
            {
                Boolean isNull = rowIndex > this.highestWrittenRowIndex || this.nullFlags.Get(rowIndex);
                if (isNull) { return null; }
            }
            T value = this.valueStorage.Get(rowIndex);
            return value;
        }

        /// <summary>
        /// Sets the cell from an <see cref="Object"/>. Null and <see cref="DBNull"/> set the cell to null; a value of
        /// the column's own type is stored directly; a convertible value of any other type is converted using the
        /// invariant culture.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The value to store.</param>
        /// <exception cref="ArgumentException">The value is neither of the column's type nor convertible to it.</exception>
        /// <exception cref="InvalidOperationException">The value is null and the column does not allow null.</exception>
        public override void SetValue(Int32 rowIndex, Object value)
        {
            if (value == null || value is DBNull)
            {
                SetNull(rowIndex);
                return;
            }
            if (value is T typedValue)
            {
                Set(rowIndex, typedValue);
                return;
            }
            T convertedValue = ConvertToColumnType(value);
            Set(rowIndex, convertedValue);
        }

        /// <summary>
        /// Releases all of this column's data, including its null tracking. Name, type and nullability are unaffected.
        /// </summary>
        public override void Clear()
        {
            this.valueStorage.Clear();
            if (this.nullFlags != null) { this.nullFlags.Clear(); }
            this.highestWrittenRowIndex = NEVER_WRITTEN;
        }
        #endregion

        #region Protected Methods
        /// <summary>
        /// Returns the column's element type.
        /// </summary>
        /// <returns>Always <typeparamref name="T"/>.</returns>
        protected override Type GetDataType()
        {
            return typeof(T);
        }

        /// <summary>
        /// Returns whether cells of this column may be null.
        /// </summary>
        /// <returns>True when nulls are allowed.</returns>
        protected override Boolean GetAllowDBNull()
        {
            return this.allowNull;
        }

        /// <summary>
        /// Turns null tracking on or off. Switching it on materialises the bitmap and places the watermark past every
        /// row that already exists, so those rows keep reading as the values they hold rather than becoming null.
        /// Switching it off scans for nulls first and refuses if it finds one, as System.Data.DataColumn does.
        /// </summary>
        /// <param name="allowNullValues">Whether nulls should be allowed.</param>
        /// <exception cref="InvalidOperationException">Turned off while the column holds a null.</exception>
        protected override void SetAllowDBNull(Boolean allowNullValues)
        {
            if (allowNullValues == this.allowNull) { return; }
            if (allowNullValues)
            {
                this.nullFlags = new BitmapColumnStorage();
                this.highestWrittenRowIndex = ExistingPhysicalRowCount() - 1;
                this.allowNull = true;
                return;
            }
            Int32 firstNullRowIndex = FindFirstNullRow();
            if (firstNullRowIndex >= 0) { throw new InvalidOperationException($"Column '{this.ColumnName}' cannot stop allowing nulls: row {firstNullRowIndex} is null. Replace the null values first."); }
            this.nullFlags = null;
            this.allowNull = false;
        }

        /// <summary>
        /// Rejects a default value the column could never store, so the failure surfaces where the mistake was made.
        /// </summary>
        /// <param name="value">The proposed default.</param>
        /// <exception cref="ArgumentException">The value is not of this column's type and is not convertible to it.</exception>
        protected override void ValidateDefaultValue(Object value)
        {
            if (value is T) { return; }
            ConvertToColumnType(value);
        }
        #endregion

        #region Private Methods
        // Marks every row between the watermark and a row about to be written as null, because those rows were
        // appended and then skipped by this column, and a skipped cell IS null.
        //
        // This is what makes the watermark exact rather than an approximation. The watermark alone says "above me is
        // null", which is right for sequential population but leaves a cell that was jumped over reading as
        // default(T) once a later row pushes the watermark past it - a row appended, left partly unwritten, and only
        // noticed as wrong when some later row wrote the same column. Deliberately NOT inlined into Set: the gap is
        // empty on every sequential write, so the caller only tests whether this needs calling and the loop stays out
        // of the hot path entirely.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void MarkSkippedRowsNull(Int32 rowIndexAboutToBeWritten)
        {
            for (Int32 skippedRowIndex = this.highestWrittenRowIndex + 1; skippedRowIndex < rowIndexAboutToBeWritten; skippedRowIndex++)
            {
                this.nullFlags.Set(skippedRowIndex, true);
            }
        }

        // How many physical row slots the owning table holds, or zero for a column that belongs to no table. Used only
        // when nullability is switched on, to place the watermark past rows that already carry values.
        private Int32 ExistingPhysicalRowCount()
        {
            DataTable owningTable = Table;
            if (owningTable == null) { return 0; }
            return owningTable.Rows.PhysicalCount;
        }

        // The first physical row whose cell is null, or -1 when none is. Only ever called when nullability is being
        // switched off, which is a schema operation, so the linear scan is not on any hot path.
        private Int32 FindFirstNullRow()
        {
            if (this.nullFlags == null) { return -1; }
            Int32 rowCount = ExistingPhysicalRowCount();
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                if (IsNull(rowIndex)) { return rowIndex; }
            }
            return -1;
        }

        // Converts a value of some other runtime type to T, so that Object-based population - Rows.Add(1, "x"), an
        // Int32 arriving for an Int64 column, a provider handing back a widened type - behaves the way callers coming
        // from System.Data.DataTable expect. A failure is reported as a clear, column-named ArgumentException rather
        // than a bare InvalidCastException from deep inside Convert (NFR-12).
        private T ConvertToColumnType(Object value)
        {
            Type targetType = typeof(T);
            Type actualType = value.GetType();
            try
            {
                Object convertedValue = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                return (T)convertedValue;
            }
            catch (Exception conversionFailure)
            {
                throw new ArgumentException($"Column '{this.ColumnName}' holds {targetType.FullName} but was given a value of type {actualType.FullName} that cannot be converted to it.", nameof(value), conversionFailure);
            }
        }
        #endregion
    }
}
