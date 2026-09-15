///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: A PROTOTYPE, not shipped code. Compiles a whole load - the row loop included - into a single delegate with
//   System.Linq.Expressions, so a row costs one indirect call into straight-line typed code instead of one indirect
//   call per column. Lives in the benchmark project so the technique can be re-measured, not depended on.
// Assumptions: The target table's schema already matches the reader's fields; unlike DataTableLoader this prototype
//   does not create a schema, infer nullability, or produce the "does not allow null values" diagnostic.
// Design Considerations: The shape here is deliberate. The columns are NOT captured as constants: they are resolved
//   out of the table parameter into locals in the prologue, OUTSIDE the row loop. Capturing them would be marginally
//   faster but would bind the compiled delegate to one DataTable instance, which makes it uncacheable - and caching
//   is the entire question, because Expression.Compile costs 12-15 ms against 2-6 microseconds to build the ordinary
//   per-column bindings. Resolving into locals costs one cast per column per LOAD rather than per row, so a cached
//   delegate keeps the throughput win while being reusable for any table of the same shape.
//   Why this is not in the library: it would need a bounded schema-keyed cache, a non-codegen fallback for NativeAOT
//   and any other runtime where Expression.Compile cannot emit (RuntimeFeature.IsDynamicCodeCompiled), a duplicate of
//   every loader test to keep the two paths behaviourally identical, and it would add 12-15 ms to the first load of
//   each distinct schema. See Spec/05_DesignDecisions.md section 2.7 for the measurements behind that call.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq.Expressions;
using System.Reflection;

namespace ColumnStore.Data.Benchmarks
{
    /// <summary>
    /// Builds one compiled delegate that performs an entire load.
    /// </summary>
    public static class CompiledRowLoaderPrototype
    {
        #region Public Methods
        /// <summary>
        /// Emits and compiles a loader for the shape of <paramref name="reader"/> and <paramref name="table"/>.
        /// </summary>
        /// <param name="reader">A reader whose field names and ordinals define the shape. Not read from here.</param>
        /// <param name="table">A table whose columns define the destination types and nullability.</param>
        /// <param name="specialiseToReaderType">When true the emitted code calls the reader's concrete type rather
        /// than <see cref="IDataReader"/>, turning interface calls into ordinary virtual ones.</param>
        /// <returns>A delegate that loads every remaining row and returns how many it loaded.</returns>
        public static Func<IDataReader, DataTable, Int32> Build(IDataReader reader, DataTable table, Boolean specialiseToReaderType)
        {
            if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            Type readerType = specialiseToReaderType ? reader.GetType() : typeof(IDataReader);
            ParameterExpression readerParameter = Expression.Parameter(typeof(IDataReader), "reader");
            ParameterExpression tableParameter = Expression.Parameter(typeof(DataTable), "table");
            ParameterExpression typedReader = Expression.Variable(readerType, "typedReader");
            ParameterExpression rowIndex = Expression.Variable(typeof(Int32), "rowIndex");
            ParameterExpression loadedRowCount = Expression.Variable(typeof(Int32), "loadedRowCount");
            ParameterExpression rowCollection = Expression.Variable(typeof(DataRowCollection), "rows");

            List<ParameterExpression> locals = new List<ParameterExpression> { typedReader, rowIndex, loadedRowCount, rowCollection };
            List<Expression> prologue = new List<Expression>();
            Expression readerAsTyped = specialiseToReaderType ? (Expression)Expression.Convert(readerParameter, readerType) : readerParameter;
            prologue.Add(Expression.Assign(typedReader, readerAsTyped));
            prologue.Add(Expression.Assign(loadedRowCount, Expression.Constant(0)));
            prologue.Add(Expression.Assign(rowCollection, Expression.Property(tableParameter, "Rows")));

            List<Expression> cells = BuildCells(reader, table, tableParameter, typedReader, readerType, rowIndex, locals, prologue);
            Expression loop = BuildLoop(cells, typedReader, readerType, rowCollection, rowIndex, loadedRowCount);

            List<Expression> body = new List<Expression>();
            body.AddRange(prologue);
            body.Add(loop);
            body.Add(loadedRowCount);
            BlockExpression block = Expression.Block(typeof(Int32), locals, body);
            return Expression.Lambda<Func<IDataReader, DataTable, Int32>>(block, readerParameter, tableParameter).Compile();
        }
        #endregion

        #region Private Methods
        // Emits one statement per bound field, and adds to the prologue the local that hoists that field's column
        // handle out of the row loop. Fields the table has no column for are skipped, exactly as the real loader does.
        private static List<Expression> BuildCells(IDataReader reader, DataTable table, ParameterExpression tableParameter, ParameterExpression typedReader, Type readerType, ParameterExpression rowIndex, List<ParameterExpression> locals, List<Expression> prologue)
        {
            MemberExpression columnCollection = Expression.Property(tableParameter, "Columns");
            PropertyInfo columnIndexer = typeof(DataColumnCollection).GetProperty("Item", new Type[] { typeof(Int32) });
            MethodInfo isDbNull = FindAccessor(readerType, "IsDBNull");
            List<Expression> cells = new List<Expression>();
            Int32 fieldCount = reader.FieldCount;
            for (Int32 ordinal = 0; ordinal < fieldCount; ordinal++)
            {
                Int32 columnOrdinal = table.Columns.IndexOf(reader.GetName(ordinal));
                if (columnOrdinal < 0) { continue; }
                IDataColumn column = table.Columns[columnOrdinal];
                Type columnType = column.GetType();
                ParameterExpression columnLocal = Expression.Variable(columnType, "column" + ordinal);
                locals.Add(columnLocal);
                IndexExpression fetched = Expression.MakeIndex(columnCollection, columnIndexer, new Expression[] { Expression.Constant(columnOrdinal) });
                prologue.Add(Expression.Assign(columnLocal, Expression.Convert(fetched, columnType)));
                cells.Add(BuildCell(column, columnLocal, columnType, typedReader, readerType, isDbNull, rowIndex, ordinal));
            }
            return cells;
        }

        // Emits the read-and-store for one field: a bare store for a non-nullable column, a null test around it
        // otherwise. The same two shapes the ordinary per-column bindings use, for the same reasons.
        private static Expression BuildCell(IDataColumn column, ParameterExpression columnLocal, Type columnType, ParameterExpression typedReader, Type readerType, MethodInfo isDbNull, ParameterExpression rowIndex, Int32 ordinal)
        {
            ConstantExpression ordinalConstant = Expression.Constant(ordinal, typeof(Int32));
            String getterName = GetterName(column.DataType);
            if (getterName == null)
            {
                MethodInfo getValue = FindAccessor(readerType, "GetValue");
                MethodInfo setValue = typeof(IDataColumn).GetMethod("SetValue", new Type[] { typeof(Int32), typeof(Object) });
                MethodCallExpression boxed = Expression.Call(typedReader, getValue, ordinalConstant);
                return Expression.Call(Expression.Convert(columnLocal, typeof(IDataColumn)), setValue, rowIndex, boxed);
            }
            MethodInfo getter = FindAccessor(readerType, getterName);
            MethodInfo set = columnType.GetMethod("Set", new Type[] { typeof(Int32), column.DataType });
            Expression store = Expression.Call(columnLocal, set, rowIndex, Expression.Call(typedReader, getter, ordinalConstant));
            if (column.AllowDBNull == false) { return store; }
            MethodInfo setNull = columnType.GetMethod("SetNull", new Type[] { typeof(Int32) });
            return Expression.IfThenElse(
                Expression.Call(typedReader, isDbNull, ordinalConstant),
                Expression.Call(columnLocal, setNull, rowIndex),
                store);
        }

        // Wraps the emitted cell statements in "while (reader.Read())", reserving one row per iteration.
        private static Expression BuildLoop(List<Expression> cells, ParameterExpression typedReader, Type readerType, ParameterExpression rowCollection, ParameterExpression rowIndex, ParameterExpression loadedRowCount)
        {
            MethodInfo addNewRow = typeof(DataRowCollection).GetMethod("AddNewRow", Type.EmptyTypes);
            MethodInfo read = readerType.GetMethod("Read", Type.EmptyTypes) ?? typeof(IDataReader).GetMethod("Read", Type.EmptyTypes);
            LabelTarget exitLabel = Expression.Label("exit");
            List<Expression> rowBody = new List<Expression>();
            rowBody.Add(Expression.Assign(rowIndex, Expression.Property(Expression.Call(rowCollection, addNewRow), "RowIndex")));
            rowBody.AddRange(cells);
            rowBody.Add(Expression.PostIncrementAssign(loadedRowCount));
            return Expression.Loop(
                Expression.IfThenElse(
                    Expression.Call(typedReader, read),
                    Expression.Block(rowBody),
                    Expression.Break(exitLabel)),
                exitLabel);
        }

        // Type.GetMethod on an interface does not search its base interfaces, so IDataReader has to fall back to
        // IDataRecord, where every per-ordinal accessor is actually declared.
        private static MethodInfo FindAccessor(Type readerType, String methodName)
        {
            MethodInfo found = readerType.GetMethod(methodName, new Type[] { typeof(Int32) });
            if (found != null) { return found; }
            return typeof(IDataRecord).GetMethod(methodName, new Type[] { typeof(Int32) });
        }

        // The reader accessor for a column type, or null for a type that has to take the boxing Object fallback.
        private static String GetterName(Type dataType)
        {
            if (dataType == typeof(Int32)) { return "GetInt32"; }
            if (dataType == typeof(Int64)) { return "GetInt64"; }
            if (dataType == typeof(Int16)) { return "GetInt16"; }
            if (dataType == typeof(Byte)) { return "GetByte"; }
            if (dataType == typeof(Decimal)) { return "GetDecimal"; }
            if (dataType == typeof(Double)) { return "GetDouble"; }
            if (dataType == typeof(Single)) { return "GetFloat"; }
            if (dataType == typeof(Boolean)) { return "GetBoolean"; }
            if (dataType == typeof(DateTime)) { return "GetDateTime"; }
            if (dataType == typeof(Guid)) { return "GetGuid"; }
            if (dataType == typeof(String)) { return "GetString"; }
            return null;
        }
        #endregion
    }
}
