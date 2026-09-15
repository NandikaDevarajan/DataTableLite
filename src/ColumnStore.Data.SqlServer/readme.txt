================================================================================
WHAT MOVED, AND WHY
================================================================================
DataTableLoader now lives in the CORE assembly (ColumnStore.Data), not here.
Nothing in it was ever SQL Server specific - it takes an IDataReader and nothing
else - and putting it here forced every application that only wanted to fill an
in-memory table to reference a database driver. It is reached as
table.Load(reader), matching System.Data.DataTable.Load.

This assembly is therefore about WRITES: table-valued parameters and bulk copy,
the two operations that genuinely need SqlDataRecord and SqlMetaData.

  AsStructuredParameter(name, tableTypeName, table[, metadata])
                                One line to a ready-to-use TVP: SqlDbType
                                .Structured, TypeName set, value streaming.
  AsSqlDataRecords(table, metadata)
                                The record stream underneath it, when a caller
                                wants to build the parameter themselves.
  AsDataReader(table)           A DbDataReader over the table - for SqlBulkCopy,
                                and accepted as a Structured value too. It is a
                                DbDataReader rather than a bare IDataReader
                                precisely because SQL Server accepts the former
                                as a TVP value and refuses the latter.

ColumnStore.Data - SQL Server I/O adapter layer fundamentals
================================================================================

WHY THIS IS A SEPARATE ASSEMBLY
--------------------------------------------------------------------------------
The core ColumnStore.Data assembly has no SQL Server package reference and never
will (FR-12, 02_Architecture.md rule 4). Everything in this folder depends
inwards on the core - never the other way round - so a caller who only wants an
in-memory columnar table does not drag Microsoft.Data.SqlClient, its native SNI
runtime, and its transitive dependencies into their application.

THE SAME RULE AS EVERYWHERE ELSE: SCHEMA-TIME COST, RUNTIME ZERO-COST
--------------------------------------------------------------------------------
Both directions of SQL Server I/O are naturally boxing machines:

  * Reading, if you use IDataReader.GetValue(i), returns Object - a box per cell.
  * Writing, if you use SqlDataRecord.SetValue(i, o), takes Object - a box per
    cell.

Both adapters avoid that the same way. Once per Load call, or once per adapter
construction, they walk the schema and build one small delegate per field that
has already captured:

  * the reader/record field index,
  * the already-cast DataColumn<T> for that field, and
  * which typed getter or setter to call (GetInt32/SetInt32, GetDateTime/
    SetDateTime, and so on).

After that, the per-row loop just invokes pre-built delegates. Nothing looks at
a Type, nothing casts, and nothing boxes on the non-null path. A field whose
type has no dedicated typed accessor (Char, custom provider types) falls back to
the Object path - but even that fallback is chosen once, at schema time, not
re-decided per row.

WHAT LIVES HERE
--------------------------------------------------------------------------------
  DataTableLoader        IDataReader -> DataTable. Creates the schema from the
                         reader when the table has none, optionally inferring
                         nullability from GetSchemaTable(), then streams rows
                         through the pre-built typed bindings. Two things the
                         binding resolves once so the row loop never has to:
                         the getter (written into the lambda, not reached
                         through a captured delegate - that hop cost 4-20% of
                         the loop) and the column's nullability (a column that
                         cannot hold a null gets a binding with no IsDBNull in
                         it at all). The named "does not allow null values"
                         error survives the second of those, recovered by an
                         exception filter that wraps the WHOLE load rather than
                         each cell - one EH region per load, none per row.
                         Repeatable: a second Load against a table that already
                         has a schema appends rows using the existing columns.

  DataTableSqlWriter     DataTable -> SQL Server, in both supported shapes:
                           AsSqlDataRecords  for a table-valued parameter
                                             (SqlDbType.Structured)
                           AsDataReader      for SqlBulkCopy.WriteToServer
                         Deleted rows are skipped by both, because both walk the
                         table's own row enumerator.

  DataTableDataReader    The minimal IDataReader over a DataTable that
                         AsDataReader hands back. Schema questions delegate to
                         DataColumnCollection; IsDBNull delegates to
                         IDataColumn.IsNull, which is exactly why IsNull lives
                         on the non-generic column interface.

THINGS WORTH KNOWING BEFORE YOU USE THEM
--------------------------------------------------------------------------------
  * A ColumnStore.Data.DataTable cannot be handed to SqlParameter.Value directly
    the way System.Data.DataTable can - ADO.NET only understands its own type.
    AsSqlDataRecords is the bridge, and it streams rather than materialising.
  * AsSqlDataRecords reuses ONE SqlDataRecord instance, repopulating it per row.
    That is the documented streaming pattern for table-valued parameters and it
    is what keeps a million-row TVP allocation-free. If you buffer the sequence
    (.ToList()) you will get a list of N references to the same record - use
    the sequence exactly once, streaming, as ADO.NET does.
  * TVP metadata (SqlMetaData[]) is caller-supplied, because precision, scale
    and length cannot be derived from a CLR type alone - decimal(18,2) and
    decimal(38,6) are both Decimal. InferSqlMetaData is offered as an explicitly
    best-guess convenience; read its documentation before trusting it with money
    or with fixed-length keys.
  * The loader dispatches on the TABLE column's type, not the reader field's, so
    a deliberately widened column (reader Int32 into an Int64 column) needs the
    provider to honour the widened typed getter. Where it will not, declare the
    column with the reader's own type.

See ../../Spec/03_Design.md section 4 for the specified design and section 6.5 /
6.6 for the test plan these adapters are verified against.
