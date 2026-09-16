///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The table facade - schema, rows, and the System.Data.DataTable-shaped surface that lets existing code be
//   ported by changing a using directive rather than by being rewritten.
// Assumptions: Single-writer access; not thread safe, the same baseline as System.Data.DataTable. A table belongs to
//   at most one DataSet.
// Design Considerations: EVERY MEMBER HERE IS IN ONE OF THREE GROUPS, and which group a member is in is a deliberate
//   decision rather than an accident of how far the implementation got:
//     - Honoured: it does what System.Data.DataTable does. TableName, Columns, Rows, NewRow, Clear, Clone, Copy,
//       ImportRow, Select(), AcceptChanges, BeginInit/EndInit, BeginLoadData/EndLoadData, Namespace, Prefix,
//       MinimumCapacity, ExtendedProperties, Dispose.
//     - Refused out loud: it throws NotImplementedException, because honouring it would need machinery this library
//       deliberately does not have. Constraints and PrimaryKey need constraint enforcement; Select(filter), Compute
//       and DefaultView need an expression engine and a view layer; ReadXml/WriteXml need an XML serialiser;
//       GetChanges/RejectChanges need per-row original-value tracking, which would reintroduce exactly the per-row
//       overhead this library exists to remove. A member that silently did nothing would be far worse than one that
//       says so: the caller would get wrong answers instead of a stack trace pointing at the line to change.
//     - Meaningful because of how this library works. AcceptChanges is a NO-OP and that is CORRECT, not lazy: there
//       is no uncommitted state here, because a write goes straight into column storage. Likewise BeginLoadData and
//       EndLoadData, which in System.Data suspend constraint checking and index maintenance - neither of which
//       exists here.
//   THE EVENTS REFUSE SUBSCRIPTION rather than accepting it and never firing. Firing them would put a delegate check
//   and an EventArgs allocation on the per-row path; never firing them would leave a caller's audit or validation
//   logic silently dead. Throwing from the add accessor fails at wire-up, in the caller's own start-up code, which is
//   the only one of the three that cannot be missed.
//   See Spec/05_DesignDecisions.md section 2.8.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;
using System.Globalization;

namespace ColumnStore.Data
{
    /// <summary>
    /// A column-oriented, strongly typed in-memory table. A near drop-in alternative to
    /// <see cref="System.Data.DataTable"/> - see readme.txt for the documented behavioural differences.
    /// </summary>
    public sealed class DataTable : IDisposable
    {
        #region Private Members
        // The table's schema. Created with the table and never replaced, so cached column handles outlive any
        // number of Clear calls.
        private readonly DataColumnCollection columns;

        // Row lifecycle and iteration. Created with the table and never replaced.
        private readonly DataRowCollection rows;

        // Informational name, used in diagnostics and exception messages. Mutable, like System.Data.DataTable's.
        private String tableName;

        // XML namespace. Stored and reported; nothing in this library reads it.
        private String tableNamespace;

        // XML element prefix. Stored and reported; nothing in this library reads it.
        private String prefix;

        // The culture reported to callers. Stored and reported.
        private CultureInfo locale;

        // Row-count hint. Accepted and reported; storage grows in chunks regardless.
        private Int32 minimumCapacity;

        // Free-form user properties, created on first use.
        private PropertyCollection extendedProperties;

        // True between BeginInit and EndInit.
        private Boolean initializing;

        // The DataSet this table belongs to, or null.
        private DataSet dataSet;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates an empty, unnamed table.
        /// </summary>
        public DataTable()
            : this(String.Empty)
        {
        }

        /// <summary>
        /// Creates an empty table with the given name.
        /// </summary>
        /// <param name="tableName">The table name. Null is stored as an empty string.</param>
        public DataTable(String tableName)
        {
            this.tableName = tableName ?? String.Empty;
            this.tableNamespace = String.Empty;
            this.prefix = String.Empty;
            this.locale = CultureInfo.CurrentCulture;
            this.minimumCapacity = 0;
            this.initializing = false;
            this.dataSet = null;
            // The row collection has to exist before the column collection can ask it anything, and the column
            // collection only ever asks lazily - when a column is added - so passing "this" here is safe even though
            // the remaining fields are still being assigned.
            this.rows = new DataRowCollection(this);
            this.columns = new DataColumnCollection(this);
        }

        /// <summary>
        /// Creates an empty table with the given name and XML namespace.
        /// </summary>
        /// <param name="tableName">The table name.</param>
        /// <param name="tableNamespace">The XML namespace.</param>
        public DataTable(String tableName, String tableNamespace)
            : this(tableName)
        {
            this.tableNamespace = tableNamespace ?? String.Empty;
        }
        #endregion

        #region Public Events
        /// <summary>
        /// Raised after a cell changes. Subscription is refused - see this file's header.
        /// </summary>
        public event DataColumnChangeEventHandler ColumnChanged
        {
            add { throw BuildEventRefusal(nameof(ColumnChanged)); }
            remove { }
        }

        /// <summary>
        /// Raised before a cell changes. Subscription is refused - see this file's header.
        /// </summary>
        public event DataColumnChangeEventHandler ColumnChanging
        {
            add { throw BuildEventRefusal(nameof(ColumnChanging)); }
            remove { }
        }

        /// <summary>
        /// Raised after a row changes. Subscription is refused - see this file's header.
        /// </summary>
        public event DataRowChangeEventHandler RowChanged
        {
            add { throw BuildEventRefusal(nameof(RowChanged)); }
            remove { }
        }

        /// <summary>
        /// Raised before a row changes. Subscription is refused - see this file's header.
        /// </summary>
        public event DataRowChangeEventHandler RowChanging
        {
            add { throw BuildEventRefusal(nameof(RowChanging)); }
            remove { }
        }

        /// <summary>
        /// Raised after a row is deleted. Subscription is refused - see this file's header.
        /// </summary>
        public event DataRowChangeEventHandler RowDeleted
        {
            add { throw BuildEventRefusal(nameof(RowDeleted)); }
            remove { }
        }

        /// <summary>
        /// Raised before a row is deleted. Subscription is refused - see this file's header.
        /// </summary>
        public event DataRowChangeEventHandler RowDeleting
        {
            add { throw BuildEventRefusal(nameof(RowDeleting)); }
            remove { }
        }

        /// <summary>
        /// Raised after the table is cleared. Subscription is refused - see this file's header.
        /// </summary>
        public event DataTableClearEventHandler TableCleared
        {
            add { throw BuildEventRefusal(nameof(TableCleared)); }
            remove { }
        }

        /// <summary>
        /// Raised before the table is cleared. Subscription is refused - see this file's header.
        /// </summary>
        public event DataTableClearEventHandler TableClearing
        {
            add { throw BuildEventRefusal(nameof(TableClearing)); }
            remove { }
        }

        /// <summary>
        /// Raised when a row is created by NewRow. Subscription is refused - see this file's header.
        /// </summary>
        public event DataTableNewRowEventHandler TableNewRow
        {
            add { throw BuildEventRefusal(nameof(TableNewRow)); }
            remove { }
        }
        #endregion

        #region Public Properties
        /// <summary>The table's name.</summary>
        public String TableName
        {
            get { return this.tableName; }
            set { this.tableName = value ?? String.Empty; }
        }

        /// <summary>The table's schema.</summary>
        public DataColumnCollection Columns
        {
            get { return this.columns; }
        }

        /// <summary>The table's rows. Exposes visible (non-deleted) rows only.</summary>
        public DataRowCollection Rows
        {
            get { return this.rows; }
        }

        /// <summary>The <see cref="ColumnStore.Data.DataSet"/> this table belongs to, or null.</summary>
        public DataSet DataSet
        {
            get { return this.dataSet; }
        }

        /// <summary>
        /// Number of visible rows. Convenience alias for <c>Rows.Count</c>; not part of System.Data's surface.
        /// </summary>
        public Int32 Count
        {
            get { return this.rows.Count; }
        }

        /// <summary>XML namespace. Stored and reported; nothing in this library reads it.</summary>
        public String Namespace
        {
            get { return this.tableNamespace; }
            set { this.tableNamespace = value ?? String.Empty; }
        }

        /// <summary>XML element prefix. Stored and reported; nothing in this library reads it.</summary>
        public String Prefix
        {
            get { return this.prefix; }
            set { this.prefix = value ?? String.Empty; }
        }

        /// <summary>The culture reported to callers.</summary>
        public CultureInfo Locale
        {
            get { return this.locale; }
            set { this.locale = value ?? CultureInfo.CurrentCulture; }
        }

        /// <summary>
        /// A hint for how many rows the table will hold. Accepted and reported; column storage grows in fixed-size
        /// chunks regardless, so the hint changes nothing.
        /// </summary>
        public Int32 MinimumCapacity
        {
            get { return this.minimumCapacity; }
            set { this.minimumCapacity = value; }
        }

        /// <summary>Free-form user properties, as <see cref="System.Data.DataTable.ExtendedProperties"/>.</summary>
        public PropertyCollection ExtendedProperties
        {
            get
            {
                if (this.extendedProperties == null) { this.extendedProperties = new PropertyCollection(); }
                return this.extendedProperties;
            }
        }

        /// <summary>Always false: this library has no per-row error state.</summary>
        public Boolean HasErrors
        {
            get { return false; }
        }

        /// <summary>True once <see cref="EndInit"/> has run, or when initialisation was never begun.</summary>
        public Boolean IsInitialized
        {
            get { return this.initializing == false; }
        }

        /// <summary>
        /// Whether string comparison is case sensitive.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to true. Case sensitivity in System.Data affects filtering
        /// and sorting, neither of which exists here; column names are always matched case-insensitively.</exception>
        public Boolean CaseSensitive
        {
            get { return false; }
            set { if (value) { throw new NotImplementedException("CaseSensitive affects filtering and sorting in System.Data, and this library has neither. Column names are always matched case-insensitively (NFR-8)."); } }
        }

        /// <summary>
        /// The table's primary key.
        /// </summary>
        /// <exception cref="NotImplementedException">On assignment of a non-empty key. This library enforces no
        /// constraints, and a primary key remembered but never enforced is a trap rather than a feature.</exception>
        public DataColumn[] PrimaryKey
        {
            get { return Array.Empty<DataColumn>(); }
            set { if (value != null && value.Length > 0) { throw new NotImplementedException("Primary keys are not supported: enforcing one needs a unique index maintained on every write. Enforce key uniqueness in the database, or check it in your own code."); } }
        }

        /// <summary>
        /// The table's constraints.
        /// </summary>
        /// <exception cref="NotImplementedException">Always.</exception>
        public ConstraintCollection Constraints
        {
            get { throw new NotImplementedException("Constraints are not supported: this library enforces no uniqueness, foreign key or check constraints. Enforce them in the database, or before loading."); }
        }

        /// <summary>
        /// The table's default view.
        /// </summary>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataView DefaultView
        {
            get { throw new NotImplementedException("DataView is not supported: sorting and filtering need an expression engine this library does not have. Enumerate Rows and use LINQ, or sort in the database."); }
        }

        /// <summary>
        /// Relations in which this table is the child.
        /// </summary>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataRelationCollection ChildRelations
        {
            get { throw new NotImplementedException("Relations are not supported. Join the data before loading it, or keep the tables separate and join in your own code."); }
        }

        /// <summary>
        /// Relations in which this table is the parent.
        /// </summary>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataRelationCollection ParentRelations
        {
            get { throw new NotImplementedException("Relations are not supported. Join the data before loading it, or keep the tables separate and join in your own code."); }
        }

        /// <summary>
        /// An expression that describes rows in this table.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to a non-empty expression.</exception>
        public String DisplayExpression
        {
            get { return String.Empty; }
            set { if (String.IsNullOrEmpty(value) == false) { throw new NotImplementedException("DisplayExpression needs an expression engine this library does not have."); } }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Fills this table from an open <see cref="IDataReader"/>, matching
        /// <see cref="System.Data.DataTable.Load(IDataReader)"/>: when the table has no columns yet its schema is
        /// created from the reader's fields, and when it already has columns the rows are appended against the
        /// existing schema and no columns are created.
        /// Unlike <see cref="System.Data.DataTable"/>, every cell goes through the reader's dedicated typed getter
        /// straight into typed column storage, so nothing is boxed on the way in.
        /// </summary>
        /// <param name="reader">An open reader, positioned before the row to load first. NOT disposed by this
        /// method, exactly as <see cref="System.Data.DataTable.Load(IDataReader)"/> leaves it open.</param>
        /// <exception cref="ArgumentNullException">The reader is null.</exception>
        /// <exception cref="ArgumentException">The reader exposes no fields.</exception>
        public void Load(IDataReader reader)
        {
            LoadRowCount(reader, true);
        }

        /// <summary>
        /// Fills this table from an open <see cref="IDataReader"/>, matching
        /// <see cref="System.Data.DataTable.Load(IDataReader, LoadOption)"/>.
        /// THE LOAD OPTION IS ACCEPTED AND HAS NO EFFECT, and that is exact rather than a shortcut: all three options
        /// describe how an incoming row should be reconciled with an EXISTING row that has the same primary key, and
        /// this library has no primary keys (see <see cref="PrimaryKey"/>). <see cref="System.Data.DataTable"/>
        /// behaves the same way on a table with no primary key - every option appends - so passing any option here
        /// produces the answer System.Data would produce.
        /// </summary>
        /// <param name="reader">An open reader, positioned before the row to load first. Not disposed by this method.</param>
        /// <param name="loadOption">Accepted for source compatibility. Every value appends, as it would in
        /// <see cref="System.Data.DataTable"/> for a table with no primary key.</param>
        /// <exception cref="ArgumentNullException">The reader is null.</exception>
        /// <exception cref="ArgumentException">The reader exposes no fields.</exception>
        public void Load(IDataReader reader, LoadOption loadOption)
        {
            LoadRowCount(reader, true);
        }

        /// <summary>
        /// Fills this table from an open <see cref="IDataReader"/> and returns HOW MANY ROWS were appended. Not part
        /// of <see cref="System.Data.DataTable"/>'s surface, which returns void and leaves the caller to compare row
        /// counts - which a table that also holds deleted rows cannot do reliably.
        /// </summary>
        /// <param name="reader">An open reader, positioned before the row to load first. Not disposed by this method.</param>
        /// <param name="inferNullability">When true, and only when the schema is being created here, per-column
        /// nullability is taken from the reader's schema metadata where the provider supplies it. Best effort:
        /// providers that do not support GetSchemaTable degrade to every column being nullable.</param>
        /// <returns>The number of rows appended.</returns>
        /// <exception cref="ArgumentNullException">The reader is null.</exception>
        /// <exception cref="ArgumentException">The reader exposes no fields.</exception>
        public Int32 LoadRowCount(IDataReader reader, Boolean inferNullability)
        {
            DataTableLoader loader = new DataTableLoader();
            return loader.Load(reader, inferNullability, this);
        }

        /// <summary>
        /// Creates a detached row over this table's schema without adding it to the table. Pass it to
        /// <c>Rows.Add</c> to make it part of the table; any number of detached rows may exist at once.
        /// </summary>
        /// <returns>A view of the detached row.</returns>
        public DataRow NewRow()
        {
            return this.rows.NewRow();
        }

        /// <summary>
        /// Appends a visible, empty row. Not part of System.Data's surface: it is the one-step way to append,
        /// without NewRow's detached-row round trip.
        /// </summary>
        /// <returns>A view of the new row.</returns>
        public DataRow AddNewRow()
        {
            return this.rows.AddNewRow();
        }

        /// <summary>
        /// Removes every row and releases every column's data. The schema is preserved and every previously returned
        /// column handle stays valid (FR-11).
        /// </summary>
        public void Clear()
        {
            this.rows.Clear();
        }

        /// <summary>
        /// Returns a new table with the same schema and no rows.
        /// </summary>
        /// <returns>The cloned table.</returns>
        public DataTable Clone()
        {
            DataTable clone = new DataTable(this.tableName, this.tableNamespace);
            clone.Prefix = this.prefix;
            clone.Locale = this.locale;
            clone.MinimumCapacity = this.minimumCapacity;
            for (Int32 ordinal = 0; ordinal < this.columns.Count; ordinal++)
            {
                DataColumn source = this.columns[ordinal];
                DataColumn copy = clone.Columns.Add(source.ColumnName, source.DataType, source.AllowDBNull);
                copy.Caption = source.Caption;
                copy.ReadOnly = source.ReadOnly;
                copy.Namespace = source.Namespace;
                copy.Prefix = source.Prefix;
                copy.ColumnMapping = source.ColumnMapping;
                copy.DefaultValue = source.DefaultValue;
            }
            return clone;
        }

        /// <summary>
        /// Returns a new table with the same schema and a copy of every visible row. Deleted rows are not copied, so
        /// the copy's row indices are contiguous even when this table's are not.
        /// </summary>
        /// <returns>The copied table.</returns>
        public DataTable Copy()
        {
            DataTable copy = Clone();
            Int32 columnCount = this.columns.Count;
            foreach (DataRow sourceRow in this.rows)
            {
                DataRow targetRow = copy.Rows.AddNewRow();
                Int32 targetRowIndex = targetRow.RowIndex;
                Int32 sourceRowIndex = sourceRow.RowIndex;
                for (Int32 ordinal = 0; ordinal < columnCount; ordinal++)
                {
                    Object value = this.columns[ordinal].GetValue(sourceRowIndex);
                    copy.Columns[ordinal].SetValue(targetRowIndex, value);
                }
            }
            return copy;
        }

        /// <summary>
        /// Copies a row from another table into this one, matching columns by name. Columns this table does not have
        /// are ignored, and columns the source does not have are left null.
        /// </summary>
        /// <param name="row">The row to import.</param>
        /// <exception cref="ArgumentException">The row is not bound to a table.</exception>
        public void ImportRow(DataRow row)
        {
            DataTable sourceTable = row.Table;
            if (sourceTable == null) { throw new ArgumentException("The row is not bound to a table.", nameof(row)); }
            DataRow targetRow = this.rows.AddNewRow();
            Int32 targetRowIndex = targetRow.RowIndex;
            Int32 sourceRowIndex = row.RowIndex;
            for (Int32 ordinal = 0; ordinal < this.columns.Count; ordinal++)
            {
                DataColumn targetColumn = this.columns[ordinal];
                DataColumn sourceColumn = sourceTable.Columns[targetColumn.ColumnName];
                if (sourceColumn == null) { continue; }
                Object value = sourceColumn.GetValue(sourceRowIndex);
                targetColumn.SetValue(targetRowIndex, value);
            }
        }

        /// <summary>
        /// Returns every visible row.
        /// </summary>
        /// <returns>The rows, in row order.</returns>
        public DataRow[] Select()
        {
            DataRow[] selected = new DataRow[this.rows.Count];
            Int32 index = 0;
            foreach (DataRow row in this.rows)
            {
                selected[index] = row;
                index = index + 1;
            }
            return selected;
        }

        /// <summary>
        /// Returns the rows matching a filter expression.
        /// </summary>
        /// <param name="filterExpression">The filter.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataRow[] Select(String filterExpression)
        {
            throw new NotImplementedException("Filter expressions are not supported: parsing and evaluating them needs an expression engine this library deliberately does not have. Enumerate Rows and filter with LINQ, or filter in the database.");
        }

        /// <summary>
        /// Returns the rows matching a filter expression, in a sort order.
        /// </summary>
        /// <param name="filterExpression">The filter.</param>
        /// <param name="sort">The sort order.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataRow[] Select(String filterExpression, String sort)
        {
            throw new NotImplementedException("Filter expressions are not supported. Enumerate Rows and filter with LINQ, or filter in the database.");
        }

        /// <summary>
        /// Computes an aggregate over the rows matching a filter.
        /// </summary>
        /// <param name="expression">The aggregate expression.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public Object Compute(String expression, String filter)
        {
            throw new NotImplementedException("Compute needs an expression engine this library deliberately does not have. Resolve the typed column handle once and aggregate over it directly - that is faster than Compute was.");
        }

        /// <summary>
        /// Commits pending changes. A NO-OP here, and correctly so: a write to this library goes straight into column
        /// storage, so there is never any uncommitted state for this to commit.
        /// </summary>
        public void AcceptChanges()
        {
        }

        /// <summary>
        /// Rolls back changes since the last <see cref="AcceptChanges"/>.
        /// </summary>
        /// <exception cref="NotImplementedException">Always.</exception>
        public void RejectChanges()
        {
            throw new NotImplementedException("Rolling back needs per-row original values, which would reintroduce exactly the per-row overhead this library exists to remove. Copy the table before changing it if you need to roll back.");
        }

        /// <summary>
        /// Returns a table of the rows changed since the last <see cref="AcceptChanges"/>.
        /// </summary>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataTable GetChanges()
        {
            throw new NotImplementedException("Change tracking is not supported: it needs per-row state this library does not keep. Track the changes you care about yourself, or compare against a Copy.");
        }

        /// <summary>
        /// Begins initialisation. Paired with <see cref="EndInit"/>; nothing is deferred, because nothing here needs
        /// deferring.
        /// </summary>
        public void BeginInit()
        {
            this.initializing = true;
        }

        /// <summary>
        /// Ends initialisation.
        /// </summary>
        public void EndInit()
        {
            this.initializing = false;
        }

        /// <summary>
        /// Suspends index and constraint maintenance during a bulk load. A no-op here: there are no indexes and no
        /// constraints to suspend, which is part of why loading is fast.
        /// </summary>
        public void BeginLoadData()
        {
        }

        /// <summary>
        /// Resumes index and constraint maintenance. A no-op, for the same reason as <see cref="BeginLoadData"/>.
        /// </summary>
        public void EndLoadData()
        {
        }

        /// <summary>
        /// Removes every row, every column and every property, returning the table to its just-constructed state.
        /// </summary>
        public void Reset()
        {
            this.rows.Clear();
            this.columns.Clear();
            this.extendedProperties = null;
            this.tableName = String.Empty;
            this.tableNamespace = String.Empty;
            this.prefix = String.Empty;
        }

        /// <summary>
        /// Releases the table. A no-op: the table holds only managed memory, which the collector reclaims when the
        /// table becomes unreachable. Present so that <c>using</c> blocks written against System.Data compile.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Describes the table by name, column count and visible row count, for diagnostics and test failure
        /// messages.
        /// </summary>
        /// <returns>The description.</returns>
        public override String ToString()
        {
            String displayName = this.tableName;
            if (String.IsNullOrEmpty(displayName)) { displayName = "(unnamed)"; }
            return $"DataTable {displayName}: {this.columns.Count} columns, {this.rows.Count} rows";
        }
        #endregion

        #region Internal Methods
        /// <summary>
        /// Records the DataSet this table belongs to. Called only by <see cref="DataTableCollection"/>.
        /// </summary>
        /// <param name="owningDataSet">The owning set, or null when the table is being removed from one.</param>
        internal void AttachToDataSet(DataSet owningDataSet)
        {
            this.dataSet = owningDataSet;
        }
        #endregion

        #region Private Methods
        // Builds the refusal every event subscription raises. Kept in one place so the guidance stays identical across
        // all nine of them.
        private static NotImplementedException BuildEventRefusal(String eventName)
        {
            return new NotImplementedException($"{eventName} is not supported. Firing table events would put a delegate check and an EventArgs allocation on the per-row path, which is exactly the cost this library exists to remove - and accepting the subscription without ever firing it would leave your handler silently dead. Do the work at the call site that changes the data instead.");
        }
        #endregion
    }
}
