///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Verifies the only reflective entry point in the library: runtime Type in, typed column out, with
//   Nullable<T> unwrapped and unusable types rejected with an actionable message.
// Assumptions: The factory is a schema-time API. These tests exercise correctness, not throughput.
// Design Considerations: The reflective fallback is tested with an enum and a custom struct precisely because those
//   are the types the well-known fast paths do not cover - if the fallback ever broke, the fast paths would hide it
//   for every common type and the failure would only appear in someone else's schema.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using ColumnStore.Data.Storage;
using Xunit;

namespace ColumnStore.Data.Tests.Columns
{
    /// <summary>
    /// Tests for <see cref="DataColumnFactory"/>.
    /// </summary>
    public sealed class DataColumnFactoryTests
    {
        #region Public Methods
        /// <summary>
        /// Every well-known type produces a column of exactly that element type.
        /// </summary>
        [Theory]
        [InlineData(typeof(Int32))]
        [InlineData(typeof(Int64))]
        [InlineData(typeof(Int16))]
        [InlineData(typeof(Byte))]
        [InlineData(typeof(Boolean))]
        [InlineData(typeof(Decimal))]
        [InlineData(typeof(Double))]
        [InlineData(typeof(Single))]
        [InlineData(typeof(String))]
        [InlineData(typeof(DateTime))]
        [InlineData(typeof(DateTimeOffset))]
        [InlineData(typeof(TimeSpan))]
        [InlineData(typeof(Guid))]
        [InlineData(typeof(Char))]
        [InlineData(typeof(Byte[])) ]
        public void CreateWellKnownTypeProducesAColumnOfThatType(Type dataType)
        {
            IDataColumn column = DataColumnFactory.Create("Value", dataType, true);
            Assert.Equal(dataType, column.DataType);
            Assert.Equal("Value", column.ColumnName);
            Assert.True(column.AllowDBNull);
        }

        /// <summary>
        /// A <see cref="System.Nullable{T}"/> type is unwrapped to its underlying type and forces the column to be
        /// nullable, so the value storage stays narrow.
        /// </summary>
        [Fact]
        public void CreateNullableTypeUnwrapsAndForcesNullable()
        {
            IDataColumn column = DataColumnFactory.Create("Quantity", typeof(Nullable<Int32>), false);
            Assert.Equal(typeof(Int32), column.DataType);
            Assert.True(column.AllowDBNull);
            DataColumn<Int32> typedColumn = (DataColumn<Int32>)column;
            Assert.IsType<TypedColumnStorage<Int32>>(typedColumn.ValueStorage);
        }

        /// <summary>
        /// An enum takes the reflective fallback and still round-trips values without boxing at the storage level.
        /// </summary>
        [Fact]
        public void CreateEnumTypeUsesTheReflectiveFallback()
        {
            IDataColumn column = DataColumnFactory.Create("Day", typeof(DayOfWeek), false);
            Assert.Equal(typeof(DayOfWeek), column.DataType);
            DataColumn<DayOfWeek> typedColumn = (DataColumn<DayOfWeek>)column;
            typedColumn.Set(0, DayOfWeek.Thursday);
            Assert.Equal(DayOfWeek.Thursday, typedColumn.Get(0));
        }

        /// <summary>
        /// A custom struct takes the reflective fallback too, and its storage is sized from the struct's own width.
        /// </summary>
        [Fact]
        public void CreateCustomStructTypeUsesTheReflectiveFallback()
        {
            IDataColumn column = DataColumnFactory.Create("Point", typeof(TestPoint), false);
            Assert.Equal(typeof(TestPoint), column.DataType);
            DataColumn<TestPoint> typedColumn = (DataColumn<TestPoint>)column;
            TestPoint stored = new TestPoint(3, 4);
            typedColumn.Set(0, stored);
            Assert.Equal(stored, typedColumn.Get(0));
        }

        /// <summary>
        /// Missing and blank inputs are rejected before any column is built.
        /// </summary>
        [Fact]
        public void CreateMissingOrBlankInputThrows()
        {
            Assert.Throws<ArgumentNullException>(() => DataColumnFactory.Create(null, typeof(Int32), true));
            Assert.Throws<ArgumentNullException>(() => DataColumnFactory.Create("Value", null, true));
            Assert.Throws<ArgumentException>(() => DataColumnFactory.Create("   ", typeof(Int32), true));
        }

        /// <summary>
        /// A type that cannot back a column is refused with a message naming the column, rather than surfacing as a
        /// reflection failure from deep inside MakeGenericType.
        /// </summary>
        [Fact]
        public void CreateUnusableTypeThrowsNamingTheColumn()
        {
            Type openGenericType = typeof(System.Collections.Generic.List<>);
            ArgumentException failure = Assert.Throws<ArgumentException>(() => DataColumnFactory.Create("Bad", openGenericType, true));
            Assert.Contains("Bad", failure.Message);
        }
        #endregion

        #region Nested Types
        /// <summary>
        /// A custom value type with no fast path, used to exercise the reflective column construction route.
        /// </summary>
        private readonly struct TestPoint : IEquatable<TestPoint>
        {
            #region Private Members
            // Horizontal coordinate.
            private readonly Int32 x;

            // Vertical coordinate.
            private readonly Int32 y;
            #endregion

            #region Constructor / Destructor
            /// <summary>
            /// Creates a point.
            /// </summary>
            /// <param name="x">Horizontal coordinate.</param>
            /// <param name="y">Vertical coordinate.</param>
            public TestPoint(Int32 x, Int32 y)
            {
                this.x = x;
                this.y = y;
            }
            #endregion

            #region Public Methods
            /// <summary>
            /// Value equality over both coordinates.
            /// </summary>
            /// <param name="other">The point to compare with.</param>
            /// <returns>True when both coordinates match.</returns>
            public Boolean Equals(TestPoint other)
            {
                return this.x == other.x && this.y == other.y;
            }

            /// <summary>
            /// Value equality against an arbitrary object.
            /// </summary>
            /// <param name="obj">The object to compare with.</param>
            /// <returns>True when it is a matching point.</returns>
            public override Boolean Equals(Object obj)
            {
                if (obj is TestPoint other) { return Equals(other); }
                return false;
            }

            /// <summary>
            /// Hash code over both coordinates.
            /// </summary>
            /// <returns>The hash code.</returns>
            public override Int32 GetHashCode()
            {
                // Written out rather than taken from System.HashCode, which .NET Framework does not have. Any stable
                // combination will do here - this is a test fixture, not a hashing benchmark.
                unchecked
                {
                    return (this.x * 397) ^ this.y;
                }
            }
            #endregion
        }
        #endregion
    }
}
