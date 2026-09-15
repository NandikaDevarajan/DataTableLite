///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies the typed column contract: value round-trips, the null model, the fail-fast read of a null cell
//   (FR-9), the Object compatibility surface, and the structural guarantee that Boolean columns are bit-packed (FR-3).
// Assumptions: Row indices here are physical slots, used directly - these tests deliberately bypass the row layer so
//   a failure points at the column, not at index translation.
// Design Considerations: The bit-packing tests assert on the storage instance's runtime type through the internal
//   test hook rather than inferring it from memory measurements, which is what 03_Design.md section 6.2 asks for. A
//   memory-based assertion would pass for a column that merely happens to be small.
//   Two tests pin down the is-null model documented in DataColumn's header, and they are the reason that model is
//   safe: one proves a write over a previously-null cell still clears the null - the constraint that stops Set from
//   skipping the bitmap altogether - and one proves a nullable column that never receives a null never allocates a
//   bitmap word, which is the memory half of the same choice.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using ColumnStore.Data.Storage;
using Xunit;

namespace ColumnStore.Data.Tests.Columns
{
    /// <summary>
    /// Tests for <see cref="DataColumn{T}"/>.
    /// </summary>
    public sealed class DataColumnTests
    {
        #region Public Methods
        /// <summary>
        /// Typed writes and reads round-trip, for value and reference types alike.
        /// </summary>
        [Fact]
        public void SetAndGetRoundTrips()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity");
            DataColumn<String> name = columns.Add<String>("Name");
            DataColumn<Decimal> price = columns.Add<Decimal>("Price");
            DataColumn<DateTime> created = columns.Add<DateTime>("Created");
            DateTime timestamp = new DateTime(2026, 9, 8, 13, 45, 0, DateTimeKind.Utc);
            quantity.Set(0, 42);
            name.Set(0, "widget");
            price.Set(0, 19.99m);
            created.Set(0, timestamp);
            Assert.Equal(42, quantity.Get(0));
            Assert.Equal("widget", name.Get(0));
            Assert.Equal(19.99m, price.Get(0));
            Assert.Equal(timestamp, created.Get(0));
        }

        /// <summary>
        /// A null cell is reported as null, throws on the typed read, and comes back as null through the Object
        /// surface.
        /// </summary>
        [Fact]
        public void SetNullMakesTheCellNullAndTypedReadsFailFast()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            quantity.Set(0, 7);
            Assert.False(quantity.IsNull(0));
            quantity.SetNull(0);
            Assert.True(quantity.IsNull(0));
            Assert.Null(quantity.GetValue(0));
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => quantity.Get(0));
            Assert.Contains("Quantity", failure.Message);
            Assert.Equal(0, quantity.GetOrDefault(0));
            Assert.Equal(-1, quantity.GetOrDefault(0, -1));
        }

        /// <summary>
        /// Writing a value over a null clears the null - the two halves of the cell must stay in step. This is the
        /// correctness constraint that stops Set from skipping the null bitmap altogether: the fast path may skip the
        /// WRITE, never the read that decides whether a write is needed.
        /// </summary>
        [Fact]
        public void SetOverANullCellClearsTheNull()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            quantity.SetNull(0);
            quantity.Set(0, 5);
            Assert.False(quantity.IsNull(0));
            Assert.Equal(5, quantity.Get(0));
            quantity.SetNull(4);
            Assert.True(quantity.IsNull(4));
            quantity.Set(4, 99);
            Assert.False(quantity.IsNull(4));
            Assert.Equal(99, quantity.Get(4));
            Assert.Equal(99, quantity.GetValue(4));
        }

        /// <summary>
        /// A cell of a nullable column that has never been written reads as null, matching System.Data.DataTable's
        /// DBNull for an unpopulated cell - and it does so without a single bit ever being written, because every row
        /// above the column's written watermark is null by definition. This is the test that proves the is-null sense
        /// costs nothing on the value path without costing correctness.
        /// </summary>
        [Fact]
        public void UnwrittenCellOfANullableColumnReadsAsNull()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            Assert.True(quantity.IsNull(0));
            Assert.Null(quantity.GetValue(0));
            Assert.True(quantity.IsNull(10_000));
            Assert.Equal(0, quantity.NullFlags.WordCount);
            quantity.Set(0, 42);
            Assert.False(quantity.IsNull(0));
            Assert.True(quantity.IsNull(1));
            Assert.Equal(0, quantity.NullFlags.WordCount);
        }

        /// <summary>
        /// THE CASE THE WATERMARK USED TO GET WRONG, now exact. A caller who writes a HIGH row index and leaves LOWER
        /// ones untouched once left those lower cells below the watermark with no null bit, so they read as default(T)
        /// rather than null. Advancing the watermark now marks every row it jumps over as null, so a skipped cell is
        /// null whatever order the writes arrive in. This matters because detached rows and partially populated rows
        /// make out-of-order writes ordinary rather than exotic.
        /// </summary>
        [Fact]
        public void SparseWriteLeavesLowerUnwrittenCellsReadingAsNull()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            Assert.True(quantity.IsNull(0));
            quantity.Set(5, 42);
            Assert.True(quantity.IsNull(0));
            Assert.True(quantity.IsNull(4));
            Assert.False(quantity.IsNull(5));
            Assert.True(quantity.IsNull(6));
            Assert.Null(quantity.GetValue(0));

            // Filling one of the skipped cells afterwards clears just that cell's null bit.
            quantity.Set(2, 7);
            Assert.False(quantity.IsNull(2));
            Assert.Equal(7, quantity.Get(2));
            Assert.True(quantity.IsNull(1));
            Assert.True(quantity.IsNull(3));
        }

        /// <summary>
        /// Skipping rows costs the bitmap only where rows were actually skipped: a sequential fill never runs the
        /// gap-marking loop, so a nullable column populated in order still allocates no bitmap words at all.
        /// </summary>
        [Fact]
        public void SequentialWritesNeverPayForGapMarking()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            for (Int32 rowIndex = 0; rowIndex < 1000; rowIndex++)
            {
                quantity.Set(rowIndex, rowIndex);
            }
            Assert.Equal(0, quantity.NullFlags.WordCount);
            Assert.False(quantity.IsNull(999));
            Assert.True(quantity.IsNull(1000));
        }

        /// <summary>
        /// A nullable column that never receives a null never allocates a single bitmap word. This is the memory half
        /// of the is-null sense: null tracking costs nothing at all until a null actually appears.
        /// </summary>
        [Fact]
        public void NullableColumnWithNoNullsAllocatesNoBitmapWords()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            for (Int32 rowIndex = 0; rowIndex < 5000; rowIndex++)
            {
                quantity.Set(rowIndex, rowIndex);
            }
            Assert.Equal(0, quantity.NullFlags.WordCount);
            quantity.SetNull(4999);
            Assert.Equal(79, quantity.NullFlags.WordCount);
        }

        /// <summary>
        /// A non-nullable column has no null tracking at all: it cannot be set null, never reports null, and spends
        /// no memory on a null bitmap.
        /// </summary>
        [Fact]
        public void NonNullableColumnRejectsNullAndCarriesNoNullTracking()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", false);
            Assert.False(quantity.AllowDBNull);
            Assert.Null(quantity.NullFlags);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => quantity.SetNull(0));
            Assert.Contains("Quantity", failure.Message);
            quantity.Set(0, 3);
            Assert.False(quantity.IsNull(0));
        }

        /// <summary>
        /// A nullable column carries exactly one null bitmap, which is where the "one bit per row" claim of FR-4
        /// lives.
        /// </summary>
        [Fact]
        public void NullableColumnTracksNullabilityWithABitmap()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Decimal> price = columns.Add<Decimal>("Price", true);
            Assert.NotNull(price.NullFlags);
            price.Set(0, 1m);
            price.Set(1, 2m);
            price.SetNull(1);
            // A set bit means null, so exactly one bit is set: the cell that was nulled.
            Assert.Equal(1, price.NullFlags.CountSetBits());
            Assert.False(price.IsNull(0));
            Assert.True(price.IsNull(1));
        }

        /// <summary>
        /// Every public path that can produce a Boolean column produces bit-packed storage (FR-3) - the guarantee is
        /// structural, not a convention that a future code path could miss.
        /// </summary>
        [Fact]
        public void BooleanColumnIsAlwaysBitPacked()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Boolean> viaGeneric = columns.Add<Boolean>("ViaGeneric");
            Assert.IsType<BitmapColumnStorage>(viaGeneric.ValueStorage);
            columns.Add("ViaType", typeof(Boolean), true);
            DataColumn<Boolean> viaType = columns.GetColumn<Boolean>("ViaType");
            Assert.IsType<BitmapColumnStorage>(viaType.ValueStorage);
            IDataColumn viaFactory = DataColumnFactory.Create("ViaFactory", typeof(Boolean), true);
            DataColumn<Boolean> typedViaFactory = (DataColumn<Boolean>)viaFactory;
            Assert.IsType<BitmapColumnStorage>(typedViaFactory.ValueStorage);
            IDataColumn viaNullableType = DataColumnFactory.Create("ViaNullableType", typeof(Nullable<Boolean>), false);
            DataColumn<Boolean> typedViaNullableType = (DataColumn<Boolean>)viaNullableType;
            Assert.IsType<BitmapColumnStorage>(typedViaNullableType.ValueStorage);
        }

        /// <summary>
        /// A non-Boolean column uses chunked typed storage, so the Boolean special case really is special-cased and
        /// not the general path.
        /// </summary>
        [Fact]
        public void NonBooleanColumnUsesChunkedTypedStorage()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity");
            Assert.IsType<TypedColumnStorage<Int32>>(quantity.ValueStorage);
        }

        /// <summary>
        /// Boolean cells round-trip through bit-packed storage, including across a word boundary.
        /// </summary>
        [Fact]
        public void BooleanColumnRoundTripsAcrossAWordBoundary()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Boolean> active = columns.Add<Boolean>("Active", false);
            for (Int32 rowIndex = 0; rowIndex < 130; rowIndex++)
            {
                Boolean value = rowIndex % 3 == 0;
                active.Set(rowIndex, value);
            }
            for (Int32 rowIndex = 0; rowIndex < 130; rowIndex++)
            {
                Boolean expected = rowIndex % 3 == 0;
                Assert.Equal(expected, active.Get(rowIndex));
            }
        }

        /// <summary>
        /// The Object surface accepts null and <see cref="DBNull"/> interchangeably, which is what makes
        /// DataTable-shaped assignment code work unchanged.
        /// </summary>
        [Fact]
        public void SetValueNullAndDBNullBothMeanNull()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            quantity.Set(0, 1);
            quantity.Set(1, 2);
            IDataColumn erased = quantity;
            erased.SetValue(0, null);
            erased.SetValue(1, DBNull.Value);
            Assert.True(quantity.IsNull(0));
            Assert.True(quantity.IsNull(1));
        }

        /// <summary>
        /// A value of another type that converts cleanly is converted, so positional population with literals behaves
        /// the way callers coming from System.Data.DataTable expect.
        /// </summary>
        [Fact]
        public void SetValueConvertibleTypeIsConverted()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int64> total = columns.Add<Int64>("Total");
            IDataColumn erased = total;
            erased.SetValue(0, 42);
            Assert.Equal(42L, total.Get(0));
        }

        /// <summary>
        /// A value that cannot be converted fails with a message naming the column and both types (NFR-12), not a
        /// bare cast failure from inside the framework.
        /// </summary>
        [Fact]
        public void SetValueIncompatibleTypeThrowsNamingTheColumn()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity");
            IDataColumn erased = quantity;
            ArgumentException failure = Assert.Throws<ArgumentException>(() => erased.SetValue(0, new Object()));
            Assert.Contains("Quantity", failure.Message);
            Assert.Contains("Int32", failure.Message);
            Assert.Contains("Object", failure.Message);
        }

        /// <summary>
        /// Clear releases values and null tracking together. A cleared nullable column reports every cell null again;
        /// a cleared non-nullable column has no storage to read from.
        /// </summary>
        [Fact]
        public void ClearReleasesValuesAndNullTracking()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> nullable = columns.Add<Int32>("Nullable", true);
            DataColumn<Int32> required = columns.Add<Int32>("Required", false);
            nullable.Set(0, 1);
            required.Set(0, 2);
            nullable.Clear();
            required.Clear();
            Assert.True(nullable.IsNull(0));
            Assert.Throws<IndexOutOfRangeException>(() => required.Get(0));
        }

        /// <summary>
        /// A negative row index is rejected consistently across every accessor (NFR-11).
        /// </summary>
        [Fact]
        public void NegativeRowIndexIsRejectedEverywhere()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            Assert.Throws<IndexOutOfRangeException>(() => quantity.Get(-1));
            Assert.Throws<IndexOutOfRangeException>(() => quantity.Set(-1, 1));
            Assert.Throws<IndexOutOfRangeException>(() => quantity.IsNull(-1));
            Assert.Throws<IndexOutOfRangeException>(() => quantity.SetNull(-1));
        }

        /// <summary>
        /// Setting a reference-typed cell to null releases the reference, so a cleared cell cannot keep a large
        /// object alive for the life of the table.
        /// </summary>
        [Fact]
        public void SetNullOnAReferenceColumnReleasesTheReference()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<String> name = columns.Add<String>("Name", true);
            name.Set(0, "widget");
            name.SetNull(0);
            TypedColumnStorage<String> storage = (TypedColumnStorage<String>)name.ValueStorage;
            Assert.Null(storage.Get(0));
        }

        /// <summary>
        /// The Nullable interop extensions map a null cell to a null <see cref="System.Nullable{T}"/> and back,
        /// without the column ever widening its storage.
        /// </summary>
        [Fact]
        public void NullableExtensionsRoundTripThroughNullable()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", true);
            quantity.Set(0, (Nullable<Int32>)17);
            quantity.Set(1, (Nullable<Int32>)null);
            Nullable<Int32> populated = quantity.GetNullable(0);
            Nullable<Int32> empty = quantity.GetNullable(1);
            Assert.True(populated.HasValue);
            Assert.Equal(17, populated.Value);
            Assert.False(empty.HasValue);
            Assert.IsType<TypedColumnStorage<Int32>>(quantity.ValueStorage);
        }

        /// <summary>
        /// ToString describes the column usefully enough to read in a test failure message.
        /// </summary>
        [Fact]
        public void ToStringReturnsTheColumnNameAsSystemDataDoes()
        {
            DataColumnCollection columns = new DataColumnCollection();
            DataColumn<Int32> quantity = columns.Add<Int32>("Quantity", false);
            Assert.Equal("Quantity", quantity.ToString());
        }
        #endregion
    }
}
