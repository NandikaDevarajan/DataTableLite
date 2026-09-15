///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: A named container for several tables, with the shape of System.Data.DataSet, so that code holding a
//   DataSet of results can be ported by changing a using directive.
// Assumptions: Single-writer access; not thread safe. A table belongs to at most one set.
// Design Considerations: THIS IS A CONTAINER, NOT A RELATIONAL ENGINE, and the split is deliberate. What a DataSet is
//   used for in practice - hold several result sets, name them, hand them around, copy them - is fully supported.
//   What needs relational machinery - Relations, EnforceConstraints, Merge, GetChanges, the XML surface - is refused
//   out loud, for the reason given on each member.
//   Merge deserves its own note, because it is the one people miss. Merging two sets means matching rows by primary
//   key, and there are no keys here (DataTable.PrimaryKey explains why). A Merge that appended rows without matching
//   them would look like it worked and would quietly duplicate every row that should have been updated - the worst
//   possible failure. Refusing is the only honest option.
//   AcceptChanges is a no-op for the same reason it is on DataTable: writes go straight into column storage, so there
//   is no pending state to commit. HasChanges therefore answers false rather than throwing, because that answer is
//   correct rather than approximate.
//   See Spec/05_DesignDecisions.md section 2.8.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Data;
using System.Globalization;

namespace ColumnStore.Data
{
    /// <summary>
    /// A named collection of <see cref="DataTable"/>s. A near drop-in alternative to
    /// <see cref="System.Data.DataSet"/> for the container role; see the class header for what is refused.
    /// </summary>
    public sealed class DataSet : IDisposable
    {
        #region Private Members
        // The tables in this set. Created with the set and never replaced.
        private readonly DataTableCollection tables;

        // The set's name.
        private String dataSetName;

        // XML namespace. Stored and reported; nothing in this library reads it.
        private String dataSetNamespace;

        // XML element prefix. Stored and reported.
        private String prefix;

        // The culture reported to callers.
        private CultureInfo locale;

        // Free-form user properties, created on first use.
        private PropertyCollection extendedProperties;

        // True between BeginInit and EndInit.
        private Boolean initializing;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates an empty set named "NewDataSet", matching System.Data's default.
        /// </summary>
        public DataSet()
            : this("NewDataSet")
        {
        }

        /// <summary>
        /// Creates an empty set with the given name.
        /// </summary>
        /// <param name="dataSetName">The set name. Null is stored as an empty string.</param>
        public DataSet(String dataSetName)
        {
            this.dataSetName = dataSetName ?? String.Empty;
            this.dataSetNamespace = String.Empty;
            this.prefix = String.Empty;
            this.locale = CultureInfo.CurrentCulture;
            this.initializing = false;
            this.tables = new DataTableCollection(this);
        }
        #endregion

        #region Public Properties
        /// <summary>The set's name.</summary>
        public String DataSetName
        {
            get { return this.dataSetName; }
            set { this.dataSetName = value ?? String.Empty; }
        }

        /// <summary>The tables in this set.</summary>
        public DataTableCollection Tables
        {
            get { return this.tables; }
        }

        /// <summary>XML namespace. Stored and reported; nothing in this library reads it.</summary>
        public String Namespace
        {
            get { return this.dataSetNamespace; }
            set { this.dataSetNamespace = value ?? String.Empty; }
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

        /// <summary>Free-form user properties, as <see cref="System.Data.DataSet.ExtendedProperties"/>.</summary>
        public PropertyCollection ExtendedProperties
        {
            get
            {
                if (this.extendedProperties == null) { this.extendedProperties = new PropertyCollection(); }
                return this.extendedProperties;
            }
        }

        /// <summary>Always false: no table in this library keeps per-row error state.</summary>
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
        /// <exception cref="NotImplementedException">Set to true, for the reason
        /// <see cref="DataTable.CaseSensitive"/> gives.</exception>
        public Boolean CaseSensitive
        {
            get { return false; }
            set { if (value) { throw new NotImplementedException("CaseSensitive affects filtering and sorting in System.Data, and this library has neither. Names are always matched case-insensitively (NFR-8)."); } }
        }

        /// <summary>
        /// Whether constraints are enforced when data changes.
        /// </summary>
        /// <exception cref="NotImplementedException">Set to true. There are no constraints here to enforce - see
        /// <see cref="DataTable.Constraints"/>.</exception>
        public Boolean EnforceConstraints
        {
            get { return false; }
            set { if (value) { throw new NotImplementedException("Constraints are not supported, so they cannot be enforced. Enforce them in the database, or before loading."); } }
        }

        /// <summary>
        /// The relations between tables in this set.
        /// </summary>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataRelationCollection Relations
        {
            get { throw new NotImplementedException("Relations are not supported: navigating them needs parent/child indexes maintained on every write. Join the data before loading it, or join in your own code."); }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Removes every row from every table in the set. Schemas and cached column handles are preserved.
        /// </summary>
        public void Clear()
        {
            for (Int32 index = 0; index < this.tables.Count; index++)
            {
                this.tables[index].Clear();
            }
        }

        /// <summary>
        /// Returns a new set with the same schema - every table cloned, no rows.
        /// </summary>
        /// <returns>The cloned set.</returns>
        public DataSet Clone()
        {
            DataSet clone = BuildEmptyCopy();
            for (Int32 index = 0; index < this.tables.Count; index++)
            {
                clone.Tables.Add(this.tables[index].Clone());
            }
            return clone;
        }

        /// <summary>
        /// Returns a new set with the same schema and a copy of every visible row of every table.
        /// </summary>
        /// <returns>The copied set.</returns>
        public DataSet Copy()
        {
            DataSet copy = BuildEmptyCopy();
            for (Int32 index = 0; index < this.tables.Count; index++)
            {
                copy.Tables.Add(this.tables[index].Copy());
            }
            return copy;
        }

        /// <summary>
        /// Commits pending changes across every table. A NO-OP, and correctly so - see
        /// <see cref="DataTable.AcceptChanges"/>.
        /// </summary>
        public void AcceptChanges()
        {
        }

        /// <summary>
        /// True when any table holds uncommitted changes. Always FALSE, and that answer is exact rather than a
        /// placeholder: a write lands in column storage immediately, so nothing is ever pending.
        /// </summary>
        /// <returns>False.</returns>
        public Boolean HasChanges()
        {
            return false;
        }

        /// <summary>
        /// Rolls back changes since the last <see cref="AcceptChanges"/>.
        /// </summary>
        /// <exception cref="NotImplementedException">Always.</exception>
        public void RejectChanges()
        {
            throw new NotImplementedException("Rolling back needs per-row original values, which would reintroduce exactly the per-row overhead this library exists to remove. Copy the set before changing it if you need to roll back.");
        }

        /// <summary>
        /// Returns a set of the rows changed since the last <see cref="AcceptChanges"/>.
        /// </summary>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public DataSet GetChanges()
        {
            throw new NotImplementedException("Change tracking is not supported: it needs per-row state this library does not keep. Track the changes you care about yourself, or compare against a Copy.");
        }

        /// <summary>
        /// Merges another set into this one.
        /// </summary>
        /// <param name="dataSet">The set to merge.</param>
        /// <exception cref="NotImplementedException">Always.</exception>
        public void Merge(DataSet dataSet)
        {
            throw new NotImplementedException("Merge is not supported: matching rows needs primary keys, and this library has none (see DataTable.PrimaryKey). A merge that appended rows instead of matching them would silently duplicate every row it should have updated, so it is refused rather than approximated. Append explicitly with DataTable.ImportRow if that is what you want.");
        }

        /// <summary>
        /// Merges a table into this set.
        /// </summary>
        /// <param name="table">The table to merge.</param>
        /// <exception cref="NotImplementedException">Always.</exception>
        public void Merge(DataTable table)
        {
            throw new NotImplementedException("Merge is not supported - see the DataSet.Merge(DataSet) message. Append explicitly with DataTable.ImportRow if that is what you want.");
        }

        /// <summary>
        /// Writes the set as XML.
        /// </summary>
        /// <param name="fileName">The destination.</param>
        /// <exception cref="NotImplementedException">Always.</exception>
        public void WriteXml(String fileName)
        {
            throw new NotImplementedException("The XML surface is not supported: this library has no serialiser, and adding one would pull an XML dependency into a data-structure assembly. Enumerate the tables and serialise them with your own writer.");
        }

        /// <summary>
        /// Reads the set from XML.
        /// </summary>
        /// <param name="fileName">The source.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public XmlReadMode ReadXml(String fileName)
        {
            throw new NotImplementedException("The XML surface is not supported. Read the XML with your own reader and populate the tables from it.");
        }

        /// <summary>
        /// Returns the set as XML.
        /// </summary>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public String GetXml()
        {
            throw new NotImplementedException("The XML surface is not supported. Enumerate the tables and serialise them with your own writer.");
        }

        /// <summary>
        /// Returns the set's XML schema.
        /// </summary>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotImplementedException">Always.</exception>
        public String GetXmlSchema()
        {
            throw new NotImplementedException("The XML surface is not supported. Build the schema from Tables and Columns if you need one.");
        }

        /// <summary>
        /// Begins initialisation. Paired with <see cref="EndInit"/>; nothing is deferred.
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
        /// Removes every table and every property, returning the set to its just-constructed state.
        /// </summary>
        public void Reset()
        {
            this.tables.Clear();
            this.extendedProperties = null;
        }

        /// <summary>
        /// Releases the set. A no-op: it holds only managed memory. Present so that <c>using</c> blocks written
        /// against System.Data compile.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Describes the set by name and table count, for diagnostics and test failure messages.
        /// </summary>
        /// <returns>The description.</returns>
        public override String ToString()
        {
            String displayName = this.dataSetName;
            if (String.IsNullOrEmpty(displayName)) { displayName = "(unnamed)"; }
            return $"DataSet {displayName}: {this.tables.Count} tables";
        }
        #endregion

        #region Private Methods
        // Creates an empty set carrying this one's identity and settings but none of its tables, which is the part
        // Clone and Copy share.
        private DataSet BuildEmptyCopy()
        {
            DataSet copy = new DataSet(this.dataSetName);
            copy.Namespace = this.dataSetNamespace;
            copy.Prefix = this.prefix;
            copy.Locale = this.locale;
            return copy;
        }
        #endregion
    }
}
