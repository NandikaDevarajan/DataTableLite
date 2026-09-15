///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The non-generic base every column derives from, and the type a caller names when writing
//   System.Data.DataTable-shaped code: "DataColumn column = table.Columns["Quantity"];".
// Assumptions: One column belongs to at most one table at a time. Ordinal and Table are maintained by
//   DataColumnCollection, which is the only thing allowed to set them.
// Design Considerations: This type exists for ONE reason - source compatibility. System.Data.DataTable code names
//   DataColumn constantly, and a library whose only column type is DataColumn<T> cannot be swapped in by changing a
//   using directive. So the generic column now derives from this, and every ordinal-driven consumer can keep working
//   through IDataColumn exactly as before.
//   THE PROPERTIES HERE FALL INTO THREE GROUPS, deliberately and visibly:
//     - Honoured: ColumnName, Caption, DataType, AllowDBNull, Ordinal, Table, DefaultValue, ReadOnly, Namespace,
//       Prefix, ColumnMapping, ExtendedProperties. These behave as System.Data.DataColumn behaves.
//     - Accepted only at their default: Unique, AutoIncrement, AutoIncrementSeed, AutoIncrementStep, MaxLength,
//       Expression, DateTimeMode. Reading one gives the default; setting one to anything else throws
//       NotImplementedException rather than storing a value this library would then silently ignore. A constraint
//       that is remembered but never enforced is worse than one that is refused out loud.
//     - Structural: SetOrdinal, which reorders the owning collection.
//   AllowDBNull IS SETTABLE, like System.Data's, and setting it does real work. Turning it on materialises the null
//   bitmap for a column that had none, with the watermark placed past every row that already exists so those rows
//   keep reading as values rather than suddenly reading as null. Turning it off scans for nulls first and refuses if
//   any exist, which is what System.Data does.
//   THE Table BACK-REFERENCE IS A DELIBERATE EXCEPTION to 02_Architecture rule 2 ("a layer depends only on layers
//   below it"). System.Data exposes DataColumn.Table and real code uses it, so the reference has to exist. It is a
//   field the collection assigns, nothing in the column's hot path reads it, and the STORAGE layer beneath is still
//   free of any upward reference - which is where that rule earns its keep. See Spec/05_DesignDecisions.md 2.8.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;

namespace ColumnStore.Data
{
    /// <summary>
    /// One column of a <see cref="DataTable"/>, without compile-time knowledge of its element type. Mirrors the shape
    /// of <see cref="System.Data.DataColumn"/> so that code written against it compiles unchanged.
    /// </summary>
    public abstract class DataColumn : IDataColumn
    {
        #region Private Constants
        // The value System.Data.DataColumn.MaxLength carries when no maximum is imposed.
        private const Int32 NO_MAXIMUM_LENGTH = -1;
        #endregion

        #region Private Members
        // The column's name. Settable, like System.Data's; the owning collection re-keys its name map on a change.
        private String columnName;

        // The display caption. Null until set, at which point it shadows the column name.
        private String caption;

        // The value handed to cells of this column in rows created after DefaultValue was set, or DBNull.Value when
        // the column has no default. Held as Object because the base class does not know T.
        private Object defaultValue;

        // True when writes through the compatibility surface are refused.
        private Boolean readOnly;

        // XML namespace and prefix. Stored and reported; nothing in this library reads them.
        private String columnNamespace;

        // XML element prefix.
        private String prefix;

        // How the column maps to XML. Stored and reported only.
        private MappingType columnMapping;

        // Free-form properties, exactly as System.Data exposes them. Created on first use.
        private PropertyCollection extendedProperties;

        // The owning collection's position for this column, or -1 while the column belongs to no table.
        private Int32 ordinal;

        // The owning table, or null while the column belongs to none.
        private DataTable table;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates a detached column. Internal by design: columns are created through
        /// <see cref="DataColumnCollection.Add{T}(String, Boolean)"/> or <see cref="DataColumnFactory"/>, which is
        /// what guarantees a Boolean column can never be built on byte-per-row storage (FR-3).
        /// </summary>
        /// <param name="columnName">The column name.</param>
        internal DataColumn(String columnName)
        {
            this.columnName = columnName;
            this.caption = null;
            this.defaultValue = DBNull.Value;
            this.readOnly = false;
            this.columnNamespace = String.Empty;
            this.prefix = String.Empty;
            this.columnMapping = MappingType.Element;
            this.ordinal = -1;
            this.table = null;
        }
        #endregion

        #region Public Properties
        /// <summary>
        /// The column's name. Changing it re-keys the owning table's name lookup; the new name must be unique within
        /// that table, compared case-insensitively.
        /// </summary>
        /// <exception cref="ArgumentException">The new name is blank, or already used by another column.</exception>
        public String ColumnName
        {
            get { return this.columnName; }
            set
            {
                String newName = value ?? String.Empty;
                if (String.Equals(this.columnName, newName, StringComparison.Ordinal)) { return; }
                if (this.table != null)
                {
                    this.table.Columns.RenameColumn(this, newName);
                    return;
                }
                if (String.IsNullOrWhiteSpace(newName)) { throw new ArgumentException("Column name must not be blank.", nameof(value)); }
                this.columnName = newName;
            }
        }

        /// <summary>The display caption. Defaults to <see cref="ColumnName"/> until explicitly set.</summary>
        public String Caption
        {
            get { return this.caption ?? this.columnName; }
            set { this.caption = value; }
        }

        /// <summary>
        /// The CLR type of the column's values. Never a <see cref="System.Nullable{T}"/> type - nullability is
        /// expressed by <see cref="AllowDBNull"/>. Read-only: the storage was chosen for this type when the column
        /// was created, so changing it is not a property assignment but a new column.
        /// </summary>
        /// <exception cref="NotImplementedException">Always, on assignment.</exception>
        public Type DataType
        {
            get { return GetDataType(); }
            set { throw new NotImplementedException("A column's DataType is fixed when the column is created, because its storage is chosen from it. Add a new column of the required type and copy the values across."); }
        }

        /// <summary>
        /// Whether cells of this column may be null. Turning it on materialises null tracking for a column that had
        /// none; turning it off is refused while any cell is actually null, exactly as
        /// <see cref="System.Data.DataColumn.AllowDBNull"/> is.
        /// </summary>
        /// <exception cref="InvalidOperationException">Set to false while the column holds a null.</exception>
        public Boolean AllowDBNull
        {
            get { return GetAllowDBNull(); }
            set { SetAllowDBNull(value); }
        }

        /// <summary>The column's position in its table's schema, or -1 while it belongs to no table.</summary>
        public Int32 Ordinal
        {
            get { return this.ordinal; }
        }

        /// <summary>The table this column belongs to, or null while it belongs to none.</summary>
        public DataTable Table
        {
            get { return this.table; }
        }

        /// <summary>
        /// The value written into this column's cell when a row is created. <see cref="DBNull.Value"/> - the default -
        /// leaves new cells null, which is both what <see cref="System.Data.DataColumn"/> does for a nullable column
        /// and what costs nothing per row.
        /// </summary>
        /// <exception cref="ArgumentException">The value is not of this column's type and cannot be converted to it.</exception>
        public Object DefaultValue
        {
            get { return this.defaultValue; }
            set
            {
                Object newDefault = value ?? DBNull.Value;
                if (newDefault is DBNull)
                {
                    this.defaultValue = DBNull.Value;
                    NotifyDefaultValueChanged();
                    return;
                }
                ValidateDefaultValue(newDefault);
                this.defaultValue = newDefault;
                NotifyDefaultValueChanged();
            }
        }

        /// <summary>True when writes through the row compatibility surface are refused.</summary>
        public Boolean ReadOnly
        {
            get { return this.readOnly; }
            set { this.readOnly = value; }
        }

        /// <summary>XML namespace. Stored and reported; nothing in this library reads it.</summary>
        public String Namespace
        {
            get { return this.columnNamespace; }
            set { this.columnNamespace = value ?? String.Empty; }
        }

        /// <summary>XML element prefix. Stored and reported; nothing in this library reads it.</summary>
        public String Prefix
        {
            get { return this.prefix; }
            set { this.prefix = value ?? String.Empty; }
        }

        /// <summary>How the column maps to XML. Stored and reported; this library writes no XML.</summary>
        public MappingType ColumnMapping
        {
            get { return this.columnMapping; }
            set { this.columnMapping = value; }
        }

        /// <summary>Free-form user properties, as <see cref="System.Data.DataColumn.ExtendedProperties"/>.</summary>
        public PropertyCollection ExtendedProperties
        {
            get
            {
                if (this.extendedProperties == null) { this.extendedProperties = new PropertyCollection(); }
                return this.extendedProperties;
            }
        }

        /// <summary>
        /// Whether values must be unique. Uniqueness enforcement would mean an index per column maintained on every
        /// write, which this library does not have.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to true.</exception>
        public Boolean Unique
        {
            get { return false; }
            set { if (value) { throw new NotImplementedException("Unique columns are not supported: enforcing uniqueness needs a per-column index maintained on every write. Check uniqueness in your own code before loading."); } }
        }

        /// <summary>
        /// Whether the column generates its own values.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to true.</exception>
        public Boolean AutoIncrement
        {
            get { return false; }
            set { if (value) { throw new NotImplementedException("AutoIncrement columns are not supported. Assign the values yourself, or let the database generate them."); } }
        }

        /// <summary>
        /// The first generated value for an auto-incrementing column.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to anything other than zero.</exception>
        public Int64 AutoIncrementSeed
        {
            get { return 0L; }
            set { if (value != 0L) { throw new NotImplementedException("AutoIncrement columns are not supported."); } }
        }

        /// <summary>
        /// The step between generated values for an auto-incrementing column.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to anything other than one.</exception>
        public Int64 AutoIncrementStep
        {
            get { return 1L; }
            set { if (value != 1L) { throw new NotImplementedException("AutoIncrement columns are not supported."); } }
        }

        /// <summary>
        /// The maximum length of a text value. Not enforced, and therefore refused rather than silently ignored.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to anything other than -1.</exception>
        public Int32 MaxLength
        {
            get { return NO_MAXIMUM_LENGTH; }
            set { if (value != NO_MAXIMUM_LENGTH) { throw new NotImplementedException("MaxLength is not enforced by this library, so it is refused rather than stored and ignored. Validate lengths before loading, or let the database reject them."); } }
        }

        /// <summary>
        /// A computed-column expression.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to a non-empty expression.</exception>
        public String Expression
        {
            get { return String.Empty; }
            set { if (String.IsNullOrEmpty(value) == false) { throw new NotImplementedException("Computed columns are not supported: there is no expression engine in this library. Compute the values into an ordinary column instead."); } }
        }

        /// <summary>
        /// How <see cref="DateTime"/> values are serialised.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to anything other than <see cref="DataSetDateTime.UnspecifiedLocal"/>.</exception>
        public DataSetDateTime DateTimeMode
        {
            get { return DataSetDateTime.UnspecifiedLocal; }
            set { if (value != DataSetDateTime.UnspecifiedLocal) { throw new NotImplementedException("DateTimeMode is an XML serialisation concern, and this library writes no XML. DateTime values are stored and returned exactly as given."); } }
        }
        #endregion

        #region Internal Properties
        /// <summary>
        /// True when a value other than <see cref="DBNull.Value"/> has been set as this column's default, so the row
        /// layer knows whether creating a row has to write anything into this column at all.
        /// </summary>
        internal Boolean HasDefaultValue
        {
            get { return this.defaultValue != null && this.defaultValue is DBNull == false; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Moves the column to a different position in its table's schema.
        /// </summary>
        /// <param name="newOrdinal">The position to move to.</param>
        /// <exception cref="ArgumentException">The column belongs to no table.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The position is outside the schema.</exception>
        public void SetOrdinal(Int32 newOrdinal)
        {
            if (this.table == null) { throw new ArgumentException("Column must belong to a table before its ordinal can be changed.", nameof(newOrdinal)); }
            this.table.Columns.MoveColumn(this, newOrdinal);
        }

        /// <summary>
        /// Returns the cell value as an <see cref="Object"/>, or null when the cell is null. Boxes value types.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>The boxed value, or null.</returns>
        public abstract Object GetValue(Int32 rowIndex);

        /// <summary>
        /// Sets the cell from an <see cref="Object"/>. Null and <see cref="DBNull"/> set the cell to null.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <param name="value">The value to store.</param>
        public abstract void SetValue(Int32 rowIndex, Object value);

        /// <summary>
        /// True when the cell holds no value. Always false for a column that does not allow null.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        /// <returns>True when the cell is null.</returns>
        public abstract Boolean IsNull(Int32 rowIndex);

        /// <summary>
        /// Copies one cell of this column from one physical row slot to another, WITHOUT boxing - the typed
        /// implementation reads and writes T directly. Used wherever a row has to be reproduced at a different slot,
        /// which the Object-based <see cref="GetValue"/>/<see cref="SetValue"/> pair would otherwise make allocate
        /// once per cell.
        /// </summary>
        /// <param name="sourceRowIndex">Physical row index to copy from.</param>
        /// <param name="targetRowIndex">Physical row index to copy to.</param>
        public abstract void CopyCell(Int32 sourceRowIndex, Int32 targetRowIndex);

        /// <summary>
        /// Releases all of this column's data. Name, type and nullability are unaffected.
        /// </summary>
        public abstract void Clear();

        /// <summary>
        /// Returns the column's name, matching <see cref="System.Data.DataColumn.ToString"/>.
        /// </summary>
        /// <returns>The column name.</returns>
        public override String ToString()
        {
            return this.columnName;
        }
        #endregion

        #region Internal Methods
        /// <summary>
        /// Records the column's position and owning table. Called only by <see cref="DataColumnCollection"/>, which
        /// owns both facts.
        /// </summary>
        /// <param name="owningTable">The owning table, or null when the column is being detached.</param>
        /// <param name="newOrdinal">The column's position, or -1 when detaching.</param>
        internal void Attach(DataTable owningTable, Int32 newOrdinal)
        {
            this.table = owningTable;
            this.ordinal = newOrdinal;
        }

        /// <summary>
        /// Overwrites the stored name without validation. Called only by <see cref="DataColumnCollection"/>, which has
        /// already validated the new name and re-keyed its own map.
        /// </summary>
        /// <param name="newName">The validated new name.</param>
        internal void OverwriteName(String newName)
        {
            this.columnName = newName;
        }

        /// <summary>
        /// Writes this column's default value into one cell. Called by the row layer only when
        /// <see cref="HasDefaultValue"/> is true somewhere in the schema.
        /// </summary>
        /// <param name="rowIndex">Physical row index.</param>
        internal void ApplyDefaultValue(Int32 rowIndex)
        {
            if (HasDefaultValue == false) { return; }
            SetValue(rowIndex, this.defaultValue);
        }
        #endregion

        #region Protected Methods
        /// <summary>
        /// Returns the column's element type. Implemented by the generic column, which is the only thing that knows it.
        /// </summary>
        /// <returns>The CLR type of the column's values.</returns>
        protected abstract Type GetDataType();

        /// <summary>
        /// Returns whether cells may be null.
        /// </summary>
        /// <returns>True when nulls are allowed.</returns>
        protected abstract Boolean GetAllowDBNull();

        /// <summary>
        /// Turns null tracking on or off.
        /// </summary>
        /// <param name="allowNull">Whether nulls should be allowed.</param>
        protected abstract void SetAllowDBNull(Boolean allowNull);

        /// <summary>
        /// Rejects a default value that is not of this column's type and cannot be converted to it, so the failure is
        /// reported when the default is set rather than on the first row that uses it.
        /// </summary>
        /// <param name="value">The proposed default.</param>
        protected abstract void ValidateDefaultValue(Object value);
        #endregion

        #region Private Methods
        // Lets the owning table know that this column's default changed, so it can keep its "any defaults at all"
        // flag accurate and leave row creation free of per-column work when no column has one.
        private void NotifyDefaultValueChanged()
        {
            if (this.table == null) { return; }
            this.table.Columns.RefreshDefaultValueState();
        }
        #endregion
    }
}
