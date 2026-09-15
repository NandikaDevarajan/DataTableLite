///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Describes one production table's column shape - how many columns of each CLR type, and how many of them
//   are nullable - and builds both a System.Data source table and a matching ColumnStore.Data schema from it.
// Assumptions: Nullable columns are taken from the front of the column list. Which particular columns are nullable
//   does not affect the measurement; how MANY are does, because a nullable columnar column costs a second write per
//   cell for its has-value bit.
// Design Considerations: The source table is built once per row count and read repeatedly through
//   DataTableReader, so both implementations are handed byte-identical input and whatever the reader costs it costs
//   them equally. Nulls are seeded at a fixed rate rather than randomly, so the two implementations see exactly the
//   same null pattern and a run is reproducible.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// The column-type composition of one production table, and the builders that materialise it.
    /// </summary>
    public sealed class TableShape
    {
        #region Private Constants
        // Null rate meaning "no nulls at all", for a shape whose nullable columns happen always to be populated.
        public const Int32 NO_NULLS = 0;
        #endregion

        #region Private Members
        // The table's name, for reporting.
        private readonly String name;

        // Number of nullable columns, taken from the front of the column list.
        private readonly Int32 nullableColumnCount;

        // The CLR type of each column, in ordinal order.
        private readonly List<Type> columnTypes;

        // One cell in this many is written as null, in every nullable column, or NO_NULLS for none. Fixed rather than
        // random so a run is reproducible and both implementations see the same pattern.
        private readonly Int32 nullEveryNthCell;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates an empty shape.
        /// </summary>
        /// <param name="name">The table's name.</param>
        /// <param name="nullableColumnCount">How many of its columns are nullable.</param>
        /// <param name="nullEveryNthCell">One cell in this many is null in every nullable column, or
        /// <see cref="NO_NULLS"/> for a shape whose nullable columns are always populated.</param>
        public TableShape(String name, Int32 nullableColumnCount, Int32 nullEveryNthCell)
        {
            this.name = name;
            this.nullableColumnCount = nullableColumnCount;
            this.columnTypes = new List<Type>();
            this.nullEveryNthCell = nullEveryNthCell;
        }
        #endregion

        #region Public Properties
        /// <summary>The table's name.</summary>
        public String Name
        {
            get { return this.name; }
        }

        /// <summary>Total number of columns declared so far.</summary>
        public Int32 ColumnCount
        {
            get { return this.columnTypes.Count; }
        }

        /// <summary>Number of nullable columns.</summary>
        public Int32 NullableColumnCount
        {
            get { return this.nullableColumnCount; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Declares a number of columns of one CLR type.
        /// </summary>
        /// <param name="columnType">The columns' CLR type.</param>
        /// <param name="count">How many to declare.</param>
        public void Add(Type columnType, Int32 count)
        {
            for (Int32 index = 0; index < count; index++)
            {
                this.columnTypes.Add(columnType);
            }
        }

        /// <summary>
        /// Builds and fills the System.Data source table that both implementations will read from.
        /// </summary>
        /// <param name="rowCount">Rows to generate.</param>
        /// <param name="namePool">Shared string pool for String columns.</param>
        /// <returns>The populated source table.</returns>
        public System.Data.DataTable BuildSourceTable(Int32 rowCount, String[] namePool)
        {
            System.Data.DataTable table = new System.Data.DataTable(this.name);
            for (Int32 ordinal = 0; ordinal < this.columnTypes.Count; ordinal++)
            {
                System.Data.DataColumn column = table.Columns.Add("c" + ordinal.ToString(), this.columnTypes[ordinal]);
                column.AllowDBNull = ordinal < this.nullableColumnCount;
            }
            table.BeginLoadData();
            for (Int32 rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                Object[] values = new Object[this.columnTypes.Count];
                for (Int32 ordinal = 0; ordinal < values.Length; ordinal++)
                {
                    values[ordinal] = BuildCellValue(ordinal, rowIndex, namePool);
                }
                table.Rows.Add(values);
            }
            table.EndLoadData();
            return table;
        }

        /// <summary>
        /// Builds the matching ColumnStore.Data schema, with no rows, so a load can skip schema inference.
        /// </summary>
        /// <returns>An empty columnar table with this shape's columns.</returns>
        public DataTable BuildLiteSchema()
        {
            DataTable table = new DataTable(this.name);
            for (Int32 ordinal = 0; ordinal < this.columnTypes.Count; ordinal++)
            {
                Boolean allowNull = ordinal < this.nullableColumnCount;
                table.Columns.Add("c" + ordinal.ToString(), this.columnTypes[ordinal], allowNull);
            }
            return table;
        }

        /// <summary>
        /// Builds the matching ColumnStore.Data schema with an explicit storage chunk row count, so the effect of chunk
        /// size on a wide table's load can be measured.
        /// </summary>
        /// <param name="chunkRowCount">Rows per storage chunk. Must be a positive power of two.</param>
        /// <returns>An empty columnar table with this shape's columns.</returns>
        public DataTable BuildLiteSchema(Int32 chunkRowCount)
        {
            DataTable table = new DataTable(this.name);
            for (Int32 ordinal = 0; ordinal < this.columnTypes.Count; ordinal++)
            {
                Boolean allowNull = ordinal < this.nullableColumnCount;
                AddTypedColumn(table, "c" + ordinal.ToString(), this.columnTypes[ordinal], allowNull, chunkRowCount);
            }
            return table;
        }
        #endregion

        #region Private Methods
        // Adds one column with an explicit chunk size. The generic Add<T> overload is the only route that accepts a
        // chunk row count, so the shape's runtime Type has to be dispatched to it here - once per column, at schema
        // time, which is exactly where the library expects type dispatch to happen.
        private static void AddTypedColumn(DataTable table, String columnName, Type columnType, Boolean allowNull, Int32 chunkRowCount)
        {
            if (columnType == typeof(Int32)) { table.Columns.Add<Int32>(columnName, allowNull, chunkRowCount); return; }
            if (columnType == typeof(Int64)) { table.Columns.Add<Int64>(columnName, allowNull, chunkRowCount); return; }
            if (columnType == typeof(String)) { table.Columns.Add<String>(columnName, allowNull, chunkRowCount); return; }
            if (columnType == typeof(Boolean)) { table.Columns.Add<Boolean>(columnName, allowNull, chunkRowCount); return; }
            if (columnType == typeof(Decimal)) { table.Columns.Add<Decimal>(columnName, allowNull, chunkRowCount); return; }
            if (columnType == typeof(DateTime)) { table.Columns.Add<DateTime>(columnName, allowNull, chunkRowCount); return; }
            if (columnType == typeof(Guid)) { table.Columns.Add<Guid>(columnName, allowNull, chunkRowCount); return; }
            if (columnType == typeof(Byte[])) { table.Columns.Add<Byte[]>(columnName, allowNull, chunkRowCount); return; }
            throw new NotSupportedException($"Column type {columnType.FullName} has no explicit-chunk-size add path in this benchmark.");
        }

        // Produces one cell's value, returning null at a fixed rate in nullable columns so both implementations see
        // the same null pattern.
        private Object BuildCellValue(Int32 ordinal, Int32 rowIndex, String[] namePool)
        {
            Boolean isNullable = ordinal < this.nullableColumnCount;
            Boolean anyNullsAtAll = this.nullEveryNthCell != NO_NULLS;
            Boolean isNullCell = isNullable && anyNullsAtAll && (rowIndex + ordinal) % this.nullEveryNthCell == 0;
            if (isNullCell) { return DBNull.Value; }
            Type columnType = this.columnTypes[ordinal];
            if (columnType == typeof(Int32)) { return rowIndex + ordinal; }
            if (columnType == typeof(Int64)) { return (Int64)(rowIndex * 3L + ordinal); }
            if (columnType == typeof(String)) { return namePool[(rowIndex + ordinal) % namePool.Length]; }
            if (columnType == typeof(Boolean)) { return (rowIndex + ordinal) % 2 == 0; }
            if (columnType == typeof(Decimal)) { return 19.99m + ordinal; }
            if (columnType == typeof(DateTime)) { return new DateTime(2026, 1, 1).AddMinutes(rowIndex); }
            if (columnType == typeof(Guid)) { return Guid.Empty; }
            if (columnType == typeof(Byte[])) { return new Byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }; }
            throw new NotSupportedException($"Shape '{this.name}' declares column type {columnType.FullName}, which the value generator does not cover.");
        }
        #endregion
    }
}
