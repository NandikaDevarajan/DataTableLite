///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Names the two table implementations the compatibility suite runs against.
// Assumptions: Used only as xunit theory data.
// Design Considerations: The suite selects an implementation by this enum and builds the harness inside the test,
//   rather than passing harness instances as theory data. Theory data is discovered ahead of execution and may be
//   reused, so a harness supplied that way could carry state from one test case into another - which in a suite
//   whose entire job is comparing observable state would produce failures that look like real behavioural
//   differences. An enum is also serialisable by xunit, so the test cases stay individually runnable.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
namespace ColumnStore.Data.Tests.Compatibility
{
    /// <summary>
    /// Which table implementation a compatibility test case should drive.
    /// </summary>
    public enum TableImplementation
    {
        /// <summary>The column-oriented table under test.</summary>
        Lite,

        /// <summary>The framework's row-oriented table.</summary>
        Framework
    }
}
