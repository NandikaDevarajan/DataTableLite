///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Selects how FakeDataReader responds to GetSchemaTable, so DataTableLoader's nullability inference can be
//   tested against every real-world provider behaviour.
// Assumptions: These four cases cover what ADO.NET providers actually do. GetSchemaTable is optional in the
//   IDataReader contract, and providers variously honour it, return null, throw, or return a table missing columns.
// Design Considerations: In its own file per coding standard section 1 - an enum is a distinct, independently
//   discoverable type, not a nested helper. Public rather than internal only because xunit [Theory] data appears in
//   public test method signatures, and a public method cannot take a less accessible parameter type.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
namespace ColumnStore.Data.Tests.Support
{
    /// <summary>
    /// How a <see cref="FakeDataReader"/> should respond to a schema-metadata request.
    /// </summary>
    public enum SchemaTableBehaviour
    {
        /// <summary>Return a schema table carrying per-field AllowDBNull.</summary>
        Supported,

        /// <summary>Return null, as some providers do.</summary>
        ReturnsNull,

        /// <summary>Throw NotSupportedException, as some providers do.</summary>
        Throws,

        /// <summary>Return a schema table that omits the AllowDBNull column.</summary>
        MissingNullabilityColumn
    }
}
