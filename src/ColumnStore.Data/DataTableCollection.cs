///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The tables belonging to one DataSet, with the shape of System.Data.DataTableCollection.
// Assumptions: Single-writer access; not thread safe. A table belongs to at most one DataSet at a time.
// Design Considerations: Table names are matched case-insensitively, as column names are and as System.Data does by
//   default (NFR-8). The name map is a dictionary for the same reason the column collection's is: a DataSet with many
//   tables would otherwise make ds.Tables["Name"] a linear scan, and that lookup sits inside loops in real code.
//   This collection deliberately does NOT copy the column collection's "indexer returns null for a missing name"
//   behaviour by accident - it copies it on purpose, because System.Data.DataTableCollection behaves the same way,
//   and code being ported tests the result for null.
//   A table added to a set is ATTACHED (its DataSet property starts reporting the set) and a removed one is detached.
//   Nothing else about the table changes: its rows, its schema and every cached column handle survive both.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections;
using System.Collections.Generic;

namespace ColumnStore.Data
{
    /// <summary>
    /// The collection of tables in a <see cref="DataSet"/>. Mirrors the shape of
    /// <see cref="System.Data.DataTableCollection"/>.
    /// </summary>
    public sealed class DataTableCollection : IEnumerable<DataTable>
    {
        #region Private Constants
        // Prefix System.Data uses when it has to invent a table name.
        private const String GENERATED_NAME_PREFIX = "Table";
        #endregion

        #region Private Members
        // Tables in insertion order. A table's position in this list is its index.
        private readonly List<DataTable> tables;

        // Name to index, case-insensitive.
        private readonly Dictionary<String, Int32> indicesByName;
        #endregion

        #region Block Dependencies
        // The set these tables belong to. Never null - this collection is only ever created by a DataSet.
        private readonly DataSet owningDataSet;
        #endregion

        #region Constructor / Destructor
        /// <summary>
        /// Creates an empty collection owned by a set.
        /// </summary>
        /// <param name="owningDataSet">The owning set.</param>
        internal DataTableCollection(DataSet owningDataSet)
        {
            this.tables = new List<DataTable>();
            this.indicesByName = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);
            this.owningDataSet = owningDataSet;
        }
        #endregion

        #region Public Properties
        /// <summary>Number of tables in the set.</summary>
        public Int32 Count
        {
            get { return this.tables.Count; }
        }

        /// <summary>Always false; this collection is not read only.</summary>
        public Boolean IsReadOnly
        {
            get { return false; }
        }

        /// <summary>Always false; this collection is not synchronised.</summary>
        public Boolean IsSynchronized
        {
            get { return false; }
        }

        /// <summary>An object usable to synchronise access to the collection.</summary>
        public Object SyncRoot
        {
            get { return this.tables; }
        }

        /// <summary>
        /// The table at the given index.
        /// </summary>
        /// <param name="index">Zero-based index.</param>
        /// <returns>The table.</returns>
        /// <exception cref="IndexOutOfRangeException">The index is outside the collection. This is the exception
        /// <see cref="System.Data.DataTableCollection"/> raises.</exception>
        public DataTable this[Int32 index]
        {
            get
            {
                if (index < 0 || index >= this.tables.Count) { throw new IndexOutOfRangeException($"Cannot find table {index}."); }
                return this.tables[index];
            }
        }

        /// <summary>
        /// The table with the given name, compared case-insensitively.
        /// </summary>
        /// <param name="name">The table name.</param>
        /// <returns>The table, or NULL when no table has that name - which is what
        /// <see cref="System.Data.DataTableCollection"/> returns.</returns>
        /// <exception cref="ArgumentNullException">The name is null.</exception>
        public DataTable this[String name]
        {
            get
            {
                if (name == null) { throw new ArgumentNullException(nameof(name)); }
                Int32 index = 0;
                Boolean found = this.indicesByName.TryGetValue(name, out index);
                if (found == false) { return null; }
                return this.tables[index];
            }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Adds an empty table with a generated name.
        /// </summary>
        /// <returns>The created table.</returns>
        public DataTable Add()
        {
            String generatedName = GenerateTableName();
            return Add(generatedName);
        }

        /// <summary>
        /// Adds an empty table with the given name.
        /// </summary>
        /// <param name="name">The table name. Must be unique, case-insensitively.</param>
        /// <returns>The created table.</returns>
        /// <exception cref="ArgumentException">The name is blank or already in use.</exception>
        public DataTable Add(String name)
        {
            if (String.IsNullOrWhiteSpace(name)) { throw new ArgumentException("Table name must not be blank.", nameof(name)); }
            DataTable table = new DataTable(name);
            Add(table);
            return table;
        }

        /// <summary>
        /// Adds an existing table to the set.
        /// </summary>
        /// <param name="table">The table to add.</param>
        /// <exception cref="ArgumentNullException">The table is null.</exception>
        /// <exception cref="ArgumentException">The table already belongs to a set, or its name is already in use.</exception>
        public void Add(DataTable table)
        {
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            if (table.DataSet != null) { throw new ArgumentException($"Table '{table.TableName}' already belongs to a DataSet. Remove it from that set first.", nameof(table)); }
            String name = table.TableName;
            if (String.IsNullOrWhiteSpace(name)) { name = GenerateTableName(); table.TableName = name; }
            if (this.indicesByName.ContainsKey(name)) { throw new ArgumentException($"A table named '{name}' already belongs to this DataSet.", nameof(table)); }
            Int32 index = this.tables.Count;
            this.tables.Add(table);
            this.indicesByName[name] = index;
            table.AttachToDataSet(this.owningDataSet);
        }

        /// <summary>
        /// Adds several existing tables.
        /// </summary>
        /// <param name="tablesToAdd">The tables to add. A null entry is skipped, as System.Data does.</param>
        /// <exception cref="ArgumentNullException">The array is null.</exception>
        public void AddRange(DataTable[] tablesToAdd)
        {
            if (tablesToAdd == null) { throw new ArgumentNullException(nameof(tablesToAdd)); }
            for (Int32 index = 0; index < tablesToAdd.Length; index++)
            {
                DataTable table = tablesToAdd[index];
                if (table == null) { continue; }
                Add(table);
            }
        }

        /// <summary>
        /// Removes the named table from the set. The table itself is unchanged apart from no longer reporting a
        /// DataSet, so its rows and every cached column handle stay valid.
        /// </summary>
        /// <param name="name">The table name.</param>
        /// <exception cref="ArgumentException">No table has that name.</exception>
        public void Remove(String name)
        {
            DataTable table = this[name];
            if (table == null) { throw new ArgumentException($"Table '{name}' does not belong to this DataSet.", nameof(name)); }
            Remove(table);
        }

        /// <summary>
        /// Removes a table from the set.
        /// </summary>
        /// <param name="table">The table to remove.</param>
        /// <exception cref="ArgumentNullException">The table is null.</exception>
        /// <exception cref="ArgumentException">The table does not belong to this set.</exception>
        public void Remove(DataTable table)
        {
            if (table == null) { throw new ArgumentNullException(nameof(table)); }
            Int32 index = this.tables.IndexOf(table);
            if (index < 0) { throw new ArgumentException($"Table '{table.TableName}' does not belong to this DataSet.", nameof(table)); }
            RemoveAt(index);
        }

        /// <summary>
        /// Removes the table at the given index.
        /// </summary>
        /// <param name="index">Zero-based index.</param>
        /// <exception cref="IndexOutOfRangeException">The index is outside the collection.</exception>
        public void RemoveAt(Int32 index)
        {
            DataTable table = this[index];
            this.tables.RemoveAt(index);
            table.AttachToDataSet(null);
            ReindexAll();
        }

        /// <summary>
        /// True when the table could be removed. This library imposes no relation constraints, so the only reason a
        /// table cannot be removed is that it does not belong here.
        /// </summary>
        /// <param name="table">The table to test.</param>
        /// <returns>True when the table belongs to this set.</returns>
        public Boolean CanRemove(DataTable table)
        {
            if (table == null) { return false; }
            return this.tables.Contains(table);
        }

        /// <summary>
        /// True when a table with that name belongs to this set.
        /// </summary>
        /// <param name="name">The table name.</param>
        /// <returns>True when the table exists. A null or blank name gives false rather than throwing.</returns>
        public Boolean Contains(String name)
        {
            if (String.IsNullOrEmpty(name)) { return false; }
            return this.indicesByName.ContainsKey(name);
        }

        /// <summary>
        /// Returns the index of the named table, or -1.
        /// </summary>
        /// <param name="name">The table name.</param>
        /// <returns>The zero-based index, or -1.</returns>
        public Int32 IndexOf(String name)
        {
            if (String.IsNullOrEmpty(name)) { return -1; }
            Int32 index = 0;
            Boolean found = this.indicesByName.TryGetValue(name, out index);
            if (found == false) { return -1; }
            return index;
        }

        /// <summary>
        /// Returns the index of a table, or -1 when it does not belong to this set.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The zero-based index, or -1.</returns>
        public Int32 IndexOf(DataTable table)
        {
            if (table == null) { return -1; }
            return this.tables.IndexOf(table);
        }

        /// <summary>
        /// Copies the tables into an array.
        /// </summary>
        /// <param name="destination">The destination array.</param>
        /// <param name="destinationIndex">Where in the destination to start writing.</param>
        public void CopyTo(DataTable[] destination, Int32 destinationIndex)
        {
            this.tables.CopyTo(destination, destinationIndex);
        }

        /// <summary>
        /// Removes every table from the set, detaching each one.
        /// </summary>
        public void Clear()
        {
            for (Int32 index = 0; index < this.tables.Count; index++)
            {
                this.tables[index].AttachToDataSet(null);
            }
            this.tables.Clear();
            this.indicesByName.Clear();
        }

        /// <summary>
        /// Returns an enumerator over the tables, in order.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public IEnumerator<DataTable> GetEnumerator()
        {
            return this.tables.GetEnumerator();
        }
        #endregion

        #region Private Methods
        // Non-generic enumeration, required by IEnumerable.
        IEnumerator IEnumerable.GetEnumerator()
        {
            IEnumerator<DataTable> enumerator = GetEnumerator();
            return enumerator;
        }

        // Invents a unique table name, matching System.Data's "Table1", "Table2" pattern.
        private String GenerateTableName()
        {
            Int32 candidateNumber = this.tables.Count + 1;
            String candidate = GENERATED_NAME_PREFIX + candidateNumber.ToString();
            while (this.indicesByName.ContainsKey(candidate))
            {
                candidateNumber = candidateNumber + 1;
                candidate = GENERATED_NAME_PREFIX + candidateNumber.ToString();
            }
            return candidate;
        }

        // Rebuilds the name map after a removal. O(tables), and only on collection changes.
        private void ReindexAll()
        {
            this.indicesByName.Clear();
            for (Int32 index = 0; index < this.tables.Count; index++)
            {
                this.indicesByName[this.tables[index].TableName] = index;
            }
        }
        #endregion
    }
}
