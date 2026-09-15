///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Assertion mechanism for internal-method preconditions. Coding standard section 9 requires public methods to
//   throw on bad input and internal methods to assert instead, because an internal contract violation is a bug inside
//   this library rather than bad input from a caller.
// Assumptions: Assertions stay active in release builds. They are deliberately NOT compiled out, because a violated
//   internal precondition in a columnar store means silently reading or writing the wrong storage slot, which is far
//   more expensive to diagnose later than a single Boolean test is to execute now.
// Design Considerations: System.Diagnostics.Contracts.Contract.Assert is a no-op unless the binary is rewritten by the
//   (long unsupported) CCRewrite tool, so it cannot be used as-is. This type is the project's equivalent assertion
//   mechanism named the same way, so the coding standard reads literally against the code. Hot per-cell paths do not
//   call it at all - see the header comments on TypedColumnStorage and DataColumn.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Runtime.CompilerServices;

namespace ColumnStore.Data
{
    /// <summary>
    /// States and enforces preconditions of internal methods, whose callers are expected to satisfy them by
    /// construction. A failed assertion always indicates a defect inside ColumnStore.Data itself.
    /// </summary>
    internal static class Contract
    {
        #region Public Methods
        /// <summary>
        /// Throws when <paramref name="condition"/> is false. Use in internal methods in place of the argument
        /// validation that a public method would perform.
        /// </summary>
        /// <param name="condition">The precondition that must hold.</param>
        /// <param name="message">Description of the precondition, used as the exception message.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Assert(Boolean condition, String message)
        {
            if (condition == false) { throw new InvalidOperationException("ColumnStore.Data contract violation: " + message); }
        }
        #endregion
    }
}
