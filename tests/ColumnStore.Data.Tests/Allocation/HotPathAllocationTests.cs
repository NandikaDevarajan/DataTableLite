///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: The tests that hold the library's central claim to account: reading and writing cells through the typed
//   APIs, and iterating rows, allocate EXACTLY ZERO bytes (NFR-5, and the acceptance criteria in 01_Requirements
//   section 6).
// Assumptions: All measured work runs on the test thread, and every storage chunk and bitmap word it touches has
//   already been allocated by the warm-up runs - so what is measured is the steady-state hot path, which is what the
//   requirement is about.
// Design Considerations: These assertions are exact zero, not "small". A hot path that boxes allocates 24 bytes per
//   cell on 64-bit, and a per-foreach enumerator allocates 40-odd bytes per loop; both are unmistakable against a
//   zero baseline, and a threshold would let one of them creep back in unnoticed.
//   The row-oriented comparison at the end is not an assertion about System.Data.DataTable's quality - it is there so
//   the numbers in the same test run make the reason this library exists legible: the same read loop over
//   System.Data.DataTable allocates a box per cell.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using ColumnStore.Data.Tests.Support;
using Xunit;

namespace ColumnStore.Data.Tests.Allocation
{
    /// <summary>
    /// Allocation-delta tests over the typed hot paths.
    /// </summary>
    public sealed class HotPathAllocationTests
    {
        #region Private Constants
        // Rows used by the measured loops. Large enough that a per-cell or per-row allocation would be obvious,
        // small enough to keep the test fast.
        private const Int32 MEASURED_ROW_COUNT = 4096;
        #endregion

        #region Public Methods
        /// <summary>
        /// Reading every cell of every row through cached typed column handles - the recommended hot-loop pattern -
        /// allocates nothing at all.
        /// </summary>
        [Fact]
        public void TypedColumnReadsAllocateNothing()
        {
            DataTable table = BuildWarmTable();
            DataColumn<Int32> id = table.Columns.GetColumn<Int32>("Id");
            DataColumn<Int64> amount = table.Columns.GetColumn<Int64>("Amount");
            DataColumn<Boolean> active = table.Columns.GetColumn<Boolean>("Active");
            DataColumn<DateTime> when = table.Columns.GetColumn<DateTime>("When");
            DataColumn<String> name = table.Columns.GetColumn<String>("Name");
            Action work = () =>
            {
                Int64 checksum = 0;
                for (Int32 rowIndex = 0; rowIndex < MEASURED_ROW_COUNT; rowIndex++)
                {
                    checksum = checksum + id.Get(rowIndex);
                    checksum = checksum + amount.Get(rowIndex);
                    if (active.Get(rowIndex)) { checksum = checksum + 1; }
                    checksum = checksum + when.Get(rowIndex).Ticks;
                    checksum = checksum + name.Get(rowIndex).Length;
                }
                if (checksum == Int64.MinValue) { throw new InvalidOperationException("Unreachable; keeps the loop from being elided."); }
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work);
            Assert.Equal(0L, allocatedBytes);
        }

        /// <summary>
        /// Writing every cell of every row through cached typed column handles allocates nothing once the storage
        /// exists.
        /// </summary>
        [Fact]
        public void TypedColumnWritesAllocateNothing()
        {
            DataTable table = BuildWarmTable();
            DataColumn<Int32> id = table.Columns.GetColumn<Int32>("Id");
            DataColumn<Int64> amount = table.Columns.GetColumn<Int64>("Amount");
            DataColumn<Boolean> active = table.Columns.GetColumn<Boolean>("Active");
            DataColumn<DateTime> when = table.Columns.GetColumn<DateTime>("When");
            DataColumn<String> name = table.Columns.GetColumn<String>("Name");
            DateTime timestamp = new DateTime(2026, 9, 8);
            String constantName = "constant";
            Action work = () =>
            {
                for (Int32 rowIndex = 0; rowIndex < MEASURED_ROW_COUNT; rowIndex++)
                {
                    id.Set(rowIndex, rowIndex);
                    amount.Set(rowIndex, rowIndex * 3L);
                    active.Set(rowIndex, true);
                    when.Set(rowIndex, timestamp);
                    name.Set(rowIndex, constantName);
                }
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work);
            Assert.Equal(0L, allocatedBytes);
        }

        /// <summary>
        /// Setting cells to null and reading them back through the null-aware accessors allocates nothing either -
        /// the null model is a bitmap, not a wrapper object.
        /// </summary>
        [Fact]
        public void NullWritesAndNullChecksAllocateNothing()
        {
            DataTable table = BuildWarmTable();
            DataColumn<Int64> amount = table.Columns.GetColumn<Int64>("Amount");
            Action work = () =>
            {
                Int32 nullCount = 0;
                for (Int32 rowIndex = 0; rowIndex < MEASURED_ROW_COUNT; rowIndex++)
                {
                    amount.SetNull(rowIndex);
                    if (amount.IsNull(rowIndex)) { nullCount = nullCount + 1; }
                    Int64 defaulted = amount.GetOrDefault(rowIndex);
                    amount.Set(rowIndex, defaulted + 1L);
                }
                if (nullCount != MEASURED_ROW_COUNT) { throw new InvalidOperationException("Every cell should have read as null."); }
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work);
            Assert.Equal(0L, allocatedBytes);
        }

        /// <summary>
        /// A foreach over the rows allocates nothing - no enumerator object, and no DataRow on the heap (NFR-1).
        /// </summary>
        [Fact]
        public void RowEnumerationAllocatesNothing()
        {
            DataTable table = BuildWarmTable();
            DataColumn<Int32> id = table.Columns.GetColumn<Int32>("Id");
            Action work = () =>
            {
                Int64 checksum = 0;
                foreach (DataRow row in table.Rows)
                {
                    checksum = checksum + id.Get(row.RowIndex);
                }
                if (checksum == Int64.MinValue) { throw new InvalidOperationException("Unreachable; keeps the loop from being elided."); }
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work);
            Assert.Equal(0L, allocatedBytes);
        }

        /// <summary>
        /// A foreach over the rows of a table with deletions still allocates nothing - skipping tombstones is bit
        /// arithmetic, not a filtered sequence.
        /// </summary>
        [Fact]
        public void RowEnumerationWithDeletionsAllocatesNothing()
        {
            DataTable table = BuildWarmTable();
            DataColumn<Int32> id = table.Columns.GetColumn<Int32>("Id");
            for (Int32 logicalIndex = 0; logicalIndex < 500; logicalIndex++)
            {
                DataRow doomed = table.Rows[logicalIndex];
                table.Rows.Delete(doomed);
            }
            Action work = () =>
            {
                Int64 checksum = 0;
                foreach (DataRow row in table.Rows)
                {
                    checksum = checksum + id.Get(row.RowIndex);
                }
                if (checksum == Int64.MinValue) { throw new InvalidOperationException("Unreachable; keeps the loop from being elided."); }
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work);
            Assert.Equal(0L, allocatedBytes);
        }

        /// <summary>
        /// Indexed access into a table with deletions allocates nothing - index translation is arithmetic over a
        /// Fenwick tree, with no intermediate objects.
        /// </summary>
        [Fact]
        public void IndexedAccessWithDeletionsAllocatesNothing()
        {
            DataTable table = BuildWarmTable();
            DataColumn<Int32> id = table.Columns.GetColumn<Int32>("Id");
            for (Int32 deletion = 0; deletion < 500; deletion++)
            {
                DataRow doomed = table.Rows[deletion];
                table.Rows.Delete(doomed);
            }
            Int32 visibleCount = table.Rows.Count;
            Action work = () =>
            {
                Int64 checksum = 0;
                for (Int32 logicalIndex = 0; logicalIndex < visibleCount; logicalIndex++)
                {
                    DataRow row = table.Rows[logicalIndex];
                    checksum = checksum + id.Get(row.RowIndex);
                }
                if (checksum == Int64.MinValue) { throw new InvalidOperationException("Unreachable; keeps the loop from being elided."); }
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work);
            Assert.Equal(0L, allocatedBytes);
        }

        /// <summary>
        /// Row-level typed access - the convenient alternative to a cached column handle - also allocates nothing.
        /// It pays a column lookup and a cast per call, but neither of those touches the heap.
        /// </summary>
        [Fact]
        public void RowLevelTypedAccessAllocatesNothing()
        {
            DataTable table = BuildWarmTable();
            Action work = () =>
            {
                Int64 checksum = 0;
                for (Int32 rowIndex = 0; rowIndex < MEASURED_ROW_COUNT; rowIndex++)
                {
                    DataRow row = table.Rows[rowIndex];
                    row.Set<Int32>("Id", rowIndex);
                    checksum = checksum + row.Get<Int32>("Id");
                    checksum = checksum + row.Get<Int64>(1);
                }
                if (checksum == Int64.MinValue) { throw new InvalidOperationException("Unreachable; keeps the loop from being elided."); }
            };
            Int64 allocatedBytes = AllocationProbe.Measure(work);
            Assert.Equal(0L, allocatedBytes);
        }

        /// <summary>
        /// For contrast, in the same test run: the equivalent read loop over System.Data.DataTable allocates a box
        /// per value-typed cell. This is the cost ColumnStore.Data exists to remove, measured rather than asserted from
        /// memory.
        /// </summary>
        [Fact]
        public void RowOrientedComparisonShowsPerCellBoxing()
        {
            System.Data.DataTable rowOrientedTable = new System.Data.DataTable("Comparison");
            rowOrientedTable.Columns.Add("Id", typeof(Int32));
            rowOrientedTable.Columns.Add("Amount", typeof(Int64));
            for (Int32 rowIndex = 0; rowIndex < MEASURED_ROW_COUNT; rowIndex++)
            {
                rowOrientedTable.Rows.Add(rowIndex, rowIndex * 3L);
            }
            Action work = () =>
            {
                Int64 checksum = 0;
                for (Int32 rowIndex = 0; rowIndex < MEASURED_ROW_COUNT; rowIndex++)
                {
                    System.Data.DataRow row = rowOrientedTable.Rows[rowIndex];
                    checksum = checksum + (Int32)row[0];
                    checksum = checksum + (Int64)row[1];
                }
                if (checksum == Int64.MinValue) { throw new InvalidOperationException("Unreachable; keeps the loop from being elided."); }
            };
            Int64 rowOrientedBytes = AllocationProbe.Measure(work);
            Assert.True(rowOrientedBytes > 0, "The row-oriented comparison is only meaningful if it does allocate; if this fails, the comparison needs revisiting rather than the library.");
        }
        #endregion

        #region Private Methods
        // A five-column table whose storage has already been grown to cover every measured row, so the measured loops
        // see the steady state rather than first-touch chunk allocation. Columns are non-nullable except Amount,
        // which the null-path test needs.
        private static DataTable BuildWarmTable()
        {
            DataTable table = new DataTable("Warm");
            table.Columns.Add<Int32>("Id", false);
            table.Columns.Add<Int64>("Amount", true);
            table.Columns.Add<Boolean>("Active", false);
            table.Columns.Add<DateTime>("When", false);
            table.Columns.Add<String>("Name", false);
            DateTime timestamp = new DateTime(2026, 9, 8);
            for (Int32 rowIndex = 0; rowIndex < MEASURED_ROW_COUNT; rowIndex++)
            {
                table.Rows.Add(rowIndex, rowIndex * 3L, true, timestamp, "constant");
            }
            return table;
        }
        #endregion
    }
}
