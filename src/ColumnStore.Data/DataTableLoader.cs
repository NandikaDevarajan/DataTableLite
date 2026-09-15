///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Loads rows from any ADO.NET IDataReader - SqlDataReader included - into a ColumnStore.Data table, using the
//   reader's dedicated typed getters straight into typed column storage rather than the boxing GetValue path. This is
//   the engine behind DataTable.Load, which is the System.Data-shaped way in.
// Assumptions: The reader is positioned before the first row and is owned by the caller (this type never disposes it).
//   Single-writer access to the target table while loading. The reader's field names are unique; SQL Server enforces
//   that for a plain SELECT, and a duplicate name would be reported as a duplicate column by DataColumnCollection.
// Design Considerations: The whole point of this type is where the type dispatch happens. A naive loader asks "what
//   type is field 3?" once per row per field - a million-row load then performs ten million type comparisons and
//   boxes every value on the way through GetValue. Here, the schema walk happens once per Load call and produces one
//   pre-bound delegate per field that has already captured the ordinal and the cast DataColumn<T>. The per-row loop
//   invokes delegates and nothing else (NFR-6, NFR-7).
//   There is one binding factory per supported type rather than a single generic factory holding a
//   Func<IDataReader, Int32, T> getter. The generic form is much shorter, but it costs a SECOND indirect call per
//   cell - the binding is invoked, and the getter is then invoked through it - and removing that hop measured 4-20%
//   of the row loop. Writing reader.GetInt32(ordinal) into the lambda removes it and leaves a direct interface call
//   the JIT can see. The repetition below is deliberate and measured; see Spec/05_DesignDecisions.md section 2.5.
//   Nullability is resolved at schema time too: a column that cannot hold a null gets a binding with no IsDBNull call
//   in it at all. The "does not allow null values" diagnostic survives that, but the guard recovering it wraps the
//   WHOLE load rather than each cell - see LoadRows. A per-cell try/catch was tried and rejected: against a reader
//   whose IsDBNull is cheap, which is every real provider, it cost more than the test it removed.
//   Dispatch is on the TABLE column's type rather than the reader field's, per 03_Design.md section 4.1, so that a
//   pre-declared schema is authoritative - which is also what makes a deliberately widened column work, and what
//   makes a mismatched one fail at the provider's typed getter rather than silently converting per row.
//   Char and byte arrays deliberately take the Object fallback: SqlDataReader does not implement GetChar at all, and
//   there is no typed getter for a byte array (GetBytes fills a caller-supplied buffer, a different shape entirely).
//   Choosing the fallback once at schema time still satisfies the "no per-row type decision" rule.
//   Nullability inference is best-effort by design: GetSchemaTable is optional in ADO.NET and several providers throw
//   or return null, so a failure there degrades to "every column nullable" rather than failing the load.
//   THIS TYPE LIVES IN THE CORE ASSEMBLY, not the SQL Server one, because nothing in it is SQL Server specific: it
//   takes an IDataReader and nothing else. Putting it here is what lets DataTable.Load exist without dragging a
//   database driver into every application that only ever wanted an in-memory table - and it is what lets the
//   .NET Framework build take no package dependency for loading at all.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace ColumnStore.Data
{
    /// <summary>
    /// Populates a <see cref="DataTable"/> from an ADO.NET data reader.
    /// </summary>
    public sealed class DataTableLoader
    {
        #region Private Constants
        // Name of the schema-table column that reports per-field nullability. Defined by ADO.NET's
        // IDataReader.GetSchemaTable contract.
        private const String ALLOW_DBNULL_COLUMN = "AllowDBNull";

        // Returned by the culprit search when no field bound to a non-nullable column is holding a null.
        private const Int32 NO_CULPRIT = -1;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a loader. The type is stateless - one instance can load any number of readers into any number of
        /// tables, and holding a shared instance is safe as long as no two threads load at once.
        /// </summary>
        public DataTableLoader()
        {
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Reads every remaining row from <paramref name="reader"/> into <paramref name="table"/>. When the table has
        /// no columns yet, its schema is created from the reader's fields; when it already has columns, rows are
        /// appended using the existing schema and no columns are created (FR-18).
        /// </summary>
        /// <param name="reader">An open reader, positioned before the row to load first. Not disposed by this method.</param>
        /// <param name="inferNullability">When true and the table's schema is being created here, per-column
        /// nullability is taken from the reader's schema metadata where the provider supplies it.</param>
        /// <param name="table">The table to populate.</param>
        /// <returns>The number of rows appended.</returns>
        /// <exception cref="ArgumentNullException">The reader or the table is null.</exception>
        /// <exception cref="ArgumentException">The reader exposes no fields.</exception>
        public Int32 Load(IDataReader reader, Boolean inferNullability, DataTable table)
        {
            LoadPlan plan = PrepareLoad(reader, inferNullability, table);
            try
            {
                return LoadRows(reader, table, plan.FieldBindings);
            }
            catch (Exception loadFailure) when (FindNullRequiredField(reader, plan.RequiredOrdinals) != NO_CULPRIT)
            {
                throw DescribeNullInRequiredField(reader, plan, loadFailure);
            }
        }

        /// <summary>
        /// Asynchronous counterpart to <see cref="Load"/>, for a <see cref="DbDataReader"/>. Only the row fetch is
        /// asynchronous - once a row has been fetched its cells are already buffered, so the per-cell path is the same
        /// synchronous, non-boxing typed path <see cref="Load"/> uses.
        /// </summary>
        /// <param name="reader">An open reader, positioned before the row to load first. Not disposed by this method.</param>
        /// <param name="inferNullability">As <see cref="Load"/>.</param>
        /// <param name="table">The table to populate.</param>
        /// <param name="cancellationToken">Cancellation token, observed between rows.</param>
        /// <returns>The number of rows appended.</returns>
        /// <exception cref="ArgumentNullException">The reader or the table is null.</exception>
        public async Task<Int32> LoadAsync(DbDataReader reader, Boolean inferNullability, DataTable table, CancellationToken cancellationToken)
        {
            LoadPlan plan = PrepareLoad(reader, inferNullability, table);
            try
            {
                return await LoadRowsAsync(reader, table, plan.FieldBindings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception loadFailure) when (FindNullRequiredField(reader, plan.RequiredOrdinals) != NO_CULPRIT)
            {
                throw DescribeNullInRequiredField(reader, plan, loadFailure);
            }
        }
        #endregion

        #region Private Methods
        // The row loop, deliberately in a method of its own with NO exception handling in it. The guard that recovers
        // the "does not allow null values" diagnostic lives in the caller, entered once per load instead of once per
        // cell, so this loop stays a plain sequence of calls with no EH region for the JIT to work around.
        private static Int32 LoadRows(IDataReader reader, DataTable table, Action<IDataReader, Int32>[] fieldBindings)
        {
            Int32 loadedRowCount = 0;
            while (reader.Read())
            {
                DataRow row = table.Rows.AddNewRow();
                Int32 physicalRowIndex = row.RowIndex;
                for (Int32 bindingIndex = 0; bindingIndex < fieldBindings.Length; bindingIndex++)
                {
                    Action<IDataReader, Int32> binding = fieldBindings[bindingIndex];
                    binding(reader, physicalRowIndex);
                }
                loadedRowCount = loadedRowCount + 1;
            }
            return loadedRowCount;
        }

        // Asynchronous row loop. Same shape, same reason for being its own method; only the row fetch awaits.
        private static async Task<Int32> LoadRowsAsync(DbDataReader reader, DataTable table, Action<IDataReader, Int32>[] fieldBindings, CancellationToken cancellationToken)
        {
            Int32 loadedRowCount = 0;
            Boolean hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            while (hasRow)
            {
                DataRow row = table.Rows.AddNewRow();
                Int32 physicalRowIndex = row.RowIndex;
                for (Int32 bindingIndex = 0; bindingIndex < fieldBindings.Length; bindingIndex++)
                {
                    Action<IDataReader, Int32> binding = fieldBindings[bindingIndex];
                    binding(reader, physicalRowIndex);
                }
                loadedRowCount = loadedRowCount + 1;
                hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            return loadedRowCount;
        }

        // Finds a field bound to a non-nullable column that is currently holding a null, or NO_CULPRIT when there is
        // none. A failed load leaves the reader on the offending row, so the culprit can be found here rather than
        // tracked per cell. This runs inside an exception filter - before the stack unwinds - so returning NO_CULPRIT
        // leaves the provider's own exception to propagate untouched, with its original stack.
        private static Int32 FindNullRequiredField(IDataReader reader, Int32[] requiredOrdinals)
        {
            for (Int32 index = 0; index < requiredOrdinals.Length; index++)
            {
                if (reader.IsDBNull(requiredOrdinals[index])) { return index; }
            }
            return NO_CULPRIT;
        }

        // Builds the error SetNull would have raised, had the binding still been testing each cell. Cold path only:
        // the search is simply repeated here rather than smuggled out of the filter that already ran it.
        private static Exception DescribeNullInRequiredField(IDataReader reader, LoadPlan plan, Exception loadFailure)
        {
            Int32 culpritIndex = FindNullRequiredField(reader, plan.RequiredOrdinals);
            if (culpritIndex == NO_CULPRIT) { return loadFailure; }
            String columnName = plan.RequiredNames[culpritIndex];
            return new InvalidOperationException($"Column '{columnName}' does not allow null values.", loadFailure);
        }

        // Validates the inputs, creates the schema when the table has none, and builds the per-field bindings. This
        // is the entire schema-time cost of a load, shared by the synchronous and asynchronous entry points.
        private static LoadPlan PrepareLoad(IDataReader reader, Boolean inferNullability, DataTable table)
        {
            if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            Int32 fieldCount = reader.FieldCount;
            if (fieldCount <= 0) { throw new ArgumentException("The reader exposes no fields to load.", nameof(reader)); }
            if (table.Columns.Count == 0) { CreateSchemaFromReader(reader, inferNullability, table); }
            LoadPlan plan = BuildFieldBindings(reader, table);
            return plan;
        }

        // Creates one table column per reader field, using the reader's own field names and types.
        private static void CreateSchemaFromReader(IDataReader reader, Boolean inferNullability, DataTable table)
        {
            Int32 fieldCount = reader.FieldCount;
            Boolean[] allowNull = ReadNullability(reader, fieldCount, inferNullability);
            for (Int32 ordinal = 0; ordinal < fieldCount; ordinal++)
            {
                String fieldName = reader.GetName(ordinal);
                Type fieldType = reader.GetFieldType(ordinal);
                table.Columns.Add(fieldName, fieldType, allowNull[ordinal]);
            }
        }

        // Returns per-field nullability, defaulting every field to nullable. GetSchemaTable is optional in ADO.NET:
        // providers may return null, omit AllowDBNull, or throw outright, and none of those is a reason to fail a
        // load - a column that is merely more permissive than the source costs nothing.
        private static Boolean[] ReadNullability(IDataReader reader, Int32 fieldCount, Boolean inferNullability)
        {
            Boolean[] allowNull = new Boolean[fieldCount];
            for (Int32 ordinal = 0; ordinal < fieldCount; ordinal++)
            {
                allowNull[ordinal] = true;
            }
            if (inferNullability == false) { return allowNull; }
            try
            {
                System.Data.DataTable schemaTable = reader.GetSchemaTable();
                if (schemaTable == null) { return allowNull; }
                Boolean hasNullabilityColumn = schemaTable.Columns.Contains(ALLOW_DBNULL_COLUMN);
                if (hasNullabilityColumn == false) { return allowNull; }
                ApplySchemaNullability(schemaTable, fieldCount, allowNull);
            }
            catch (Exception schemaFailure) when (schemaFailure is NotSupportedException || schemaFailure is InvalidOperationException || schemaFailure is NotImplementedException)
            {
                // Provider does not support schema metadata. Every column stays nullable.
            }
            return allowNull;
        }

        // Copies AllowDBNull out of a schema table into the per-field array, ignoring rows whose value is missing or
        // not a Boolean rather than trusting a partially populated schema table.
        private static void ApplySchemaNullability(System.Data.DataTable schemaTable, Int32 fieldCount, Boolean[] allowNull)
        {
            Int32 rowCount = schemaTable.Rows.Count;
            Int32 lastOrdinal = Math.Min(fieldCount, rowCount);
            for (Int32 ordinal = 0; ordinal < lastOrdinal; ordinal++)
            {
                Object nullabilityValue = schemaTable.Rows[ordinal][ALLOW_DBNULL_COLUMN];
                if (nullabilityValue is Boolean allowsNull) { allowNull[ordinal] = allowsNull; }
            }
        }

        // Builds one binding per reader field that maps to a table column, dropping fields the table does not have a
        // column for. Compacting the array here means the per-row loop has no null check to perform. The ordinals and
        // names of the fields bound to NON-nullable columns are collected alongside, for the cold-path culprit search.
        private static LoadPlan BuildFieldBindings(IDataReader reader, DataTable table)
        {
            Int32 fieldCount = reader.FieldCount;
            Action<IDataReader, Int32>[] candidateBindings = new Action<IDataReader, Int32>[fieldCount];
            Int32[] requiredOrdinals = new Int32[fieldCount];
            String[] requiredNames = new String[fieldCount];
            Int32 boundFieldCount = 0;
            Int32 requiredFieldCount = 0;
            for (Int32 ordinal = 0; ordinal < fieldCount; ordinal++)
            {
                String fieldName = reader.GetName(ordinal);
                Int32 columnOrdinal = table.Columns.IndexOf(fieldName);
                if (columnOrdinal < 0) { continue; }
                IDataColumn column = table.Columns[columnOrdinal];
                Action<IDataReader, Int32> binding = CreateFieldBinding(ordinal, column);
                candidateBindings[boundFieldCount] = binding;
                boundFieldCount = boundFieldCount + 1;
                if (column.AllowDBNull) { continue; }
                requiredOrdinals[requiredFieldCount] = ordinal;
                requiredNames[requiredFieldCount] = column.ColumnName;
                requiredFieldCount = requiredFieldCount + 1;
            }
            Array.Resize(ref candidateBindings, boundFieldCount);
            Array.Resize(ref requiredOrdinals, requiredFieldCount);
            Array.Resize(ref requiredNames, requiredFieldCount);
            return new LoadPlan(candidateBindings, requiredOrdinals, requiredNames);
        }

        // Chooses the binding for one field: a dedicated typed getter where the column's type has one, the Object
        // fallback otherwise. Split by category to keep each method's branch count low.
        private static Action<IDataReader, Int32> CreateFieldBinding(Int32 ordinal, IDataColumn column)
        {
            Action<IDataReader, Int32> numericBinding = TryCreateNumericBinding(ordinal, column);
            if (numericBinding != null) { return numericBinding; }
            Action<IDataReader, Int32> otherBinding = TryCreateNonNumericBinding(ordinal, column);
            if (otherBinding != null) { return otherBinding; }
            Action<IDataReader, Int32> fallbackBinding = CreateFallbackBinding(ordinal, column);
            return fallbackBinding;
        }

        // Bindings for the numeric types IDataRecord has dedicated getters for.
        private static Action<IDataReader, Int32> TryCreateNumericBinding(Int32 ordinal, IDataColumn column)
        {
            Type dataType = column.DataType;
            if (dataType == typeof(Int32)) { return CreateInt32Binding(ordinal, (DataColumn<Int32>)column); }
            if (dataType == typeof(Int64)) { return CreateInt64Binding(ordinal, (DataColumn<Int64>)column); }
            if (dataType == typeof(Int16)) { return CreateInt16Binding(ordinal, (DataColumn<Int16>)column); }
            if (dataType == typeof(Byte)) { return CreateByteBinding(ordinal, (DataColumn<Byte>)column); }
            if (dataType == typeof(Decimal)) { return CreateDecimalBinding(ordinal, (DataColumn<Decimal>)column); }
            if (dataType == typeof(Double)) { return CreateDoubleBinding(ordinal, (DataColumn<Double>)column); }
            if (dataType == typeof(Single)) { return CreateSingleBinding(ordinal, (DataColumn<Single>)column); }
            return null;
        }

        // Bindings for the remaining types IDataRecord has dedicated getters for.
        private static Action<IDataReader, Int32> TryCreateNonNumericBinding(Int32 ordinal, IDataColumn column)
        {
            Type dataType = column.DataType;
            if (dataType == typeof(Boolean)) { return CreateBooleanBinding(ordinal, (DataColumn<Boolean>)column); }
            if (dataType == typeof(DateTime)) { return CreateDateTimeBinding(ordinal, (DataColumn<DateTime>)column); }
            if (dataType == typeof(Guid)) { return CreateGuidBinding(ordinal, (DataColumn<Guid>)column); }
            if (dataType == typeof(String)) { return CreateStringBinding(ordinal, (DataColumn<String>)column); }
            return null;
        }

        // Binding for an Int32 column. The nullable form tests the cell; the non-nullable form does not, because the
        // schema has already answered that question. Every binding below repeats these two shapes for the same two
        // reasons: the nullability branch is resolved once, and the getter is written in rather than called through.
        private static Action<IDataReader, Int32> CreateInt32Binding(Int32 ordinal, DataColumn<Int32> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetInt32(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetInt32(ordinal));
            };
        }

        // Binding for an Int64 column.
        private static Action<IDataReader, Int32> CreateInt64Binding(Int32 ordinal, DataColumn<Int64> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetInt64(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetInt64(ordinal));
            };
        }

        // Binding for an Int16 column.
        private static Action<IDataReader, Int32> CreateInt16Binding(Int32 ordinal, DataColumn<Int16> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetInt16(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetInt16(ordinal));
            };
        }

        // Binding for a Byte column.
        private static Action<IDataReader, Int32> CreateByteBinding(Int32 ordinal, DataColumn<Byte> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetByte(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetByte(ordinal));
            };
        }

        // Binding for a Decimal column.
        private static Action<IDataReader, Int32> CreateDecimalBinding(Int32 ordinal, DataColumn<Decimal> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetDecimal(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetDecimal(ordinal));
            };
        }

        // Binding for a Double column.
        private static Action<IDataReader, Int32> CreateDoubleBinding(Int32 ordinal, DataColumn<Double> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetDouble(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetDouble(ordinal));
            };
        }

        // Binding for a Single column. IDataRecord spells this getter GetFloat.
        private static Action<IDataReader, Int32> CreateSingleBinding(Int32 ordinal, DataColumn<Single> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetFloat(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetFloat(ordinal));
            };
        }

        // Binding for a Boolean column.
        private static Action<IDataReader, Int32> CreateBooleanBinding(Int32 ordinal, DataColumn<Boolean> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetBoolean(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetBoolean(ordinal));
            };
        }

        // Binding for a DateTime column.
        private static Action<IDataReader, Int32> CreateDateTimeBinding(Int32 ordinal, DataColumn<DateTime> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetDateTime(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetDateTime(ordinal));
            };
        }

        // Binding for a Guid column.
        private static Action<IDataReader, Int32> CreateGuidBinding(Int32 ordinal, DataColumn<Guid> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetGuid(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetGuid(ordinal));
            };
        }

        // Binding for a String column. Returns a reference, so there is nothing to box either way.
        private static Action<IDataReader, Int32> CreateStringBinding(Int32 ordinal, DataColumn<String> column)
        {
            if (column.AllowDBNull == false)
            {
                return (reader, rowIndex) => column.Set(rowIndex, reader.GetString(ordinal));
            }
            return (reader, rowIndex) =>
            {
                if (reader.IsDBNull(ordinal)) { column.SetNull(rowIndex); return; }
                column.Set(rowIndex, reader.GetString(ordinal));
            };
        }

        // Binding for a column whose type has no dedicated reader getter. Boxes on the non-null path because the
        // reader's own API leaves no alternative (NFR-7 permits exactly this), but the decision to take this path was
        // still made once, at schema time. There is no IsDBNull test here at all: GetValue already reports a null as
        // DBNull.Value and SetValue already routes DBNull and null to SetNull, so testing first would ask the reader
        // the same question twice - and a non-nullable column still raises its own named error out of SetNull.
        private static Action<IDataReader, Int32> CreateFallbackBinding(Int32 ordinal, IDataColumn column)
        {
            return (reader, rowIndex) =>
            {
                Object value = reader.GetValue(ordinal);
                column.SetValue(rowIndex, value);
            };
        }
        #endregion

        #region Private Types
        // Everything one Load call resolves up front: the per-field bindings the row loop invokes, plus the ordinals
        // and names of the fields bound to non-nullable columns, which only the cold-path culprit search ever reads.
        // Parallel arrays rather than a list of objects, so a load that never fails never touches the last two.
        private readonly struct LoadPlan
        {
            #region Constructor / Destructor
            // Captures the three arrays BuildFieldBindings produced.
            public LoadPlan(Action<IDataReader, Int32>[] fieldBindings, Int32[] requiredOrdinals, String[] requiredNames)
            {
                this.FieldBindings = fieldBindings;
                this.RequiredOrdinals = requiredOrdinals;
                this.RequiredNames = requiredNames;
            }
            #endregion

            #region Public Properties
            /// <summary>One binding per reader field that maps to a table column, in reader-field order.</summary>
            public Action<IDataReader, Int32>[] FieldBindings { get; }

            /// <summary>Reader ordinals of the fields bound to non-nullable columns.</summary>
            public Int32[] RequiredOrdinals { get; }

            /// <summary>Column names matching <see cref="RequiredOrdinals"/>, in the same order.</summary>
            public String[] RequiredNames { get; }
            #endregion
        }
        #endregion
    }
}
