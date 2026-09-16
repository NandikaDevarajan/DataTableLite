# ColumnStore.Data — Coding Standards

This document defines the coding conventions for the ColumnStore.Data codebase. It applies to all C# source files in the project, including test projects unless otherwise noted. When in doubt, consistency with this document takes priority over personal preference or IDE defaults.

---

## 1. File Organization

- **One class or interface per file.** No exceptions for small/related types — even a tiny helper interface gets its own file. This is a deliberate trade-off in favor of searchability and readability over file count.
- The file name must match the type name exactly (e.g. `DataColumn.cs` contains only `DataColumn<T>`; `IDataColumn.cs` contains only `IDataColumn`). If `IDataColumn` and `IDataColumn<T>` are logically paired but are two distinct types, prefer splitting them into `IDataColumn.cs` and `IDataColumnOfT.cs` (or an equivalent unambiguous name) rather than combining them, unless the pairing is genuinely inseparable — when in doubt, split.
- Nested/private helper types (e.g. a `struct Enumerator` inside `DataRowCollection`) are the one exception to "one type per file," since they are not independently discoverable or reusable types — they stay nested inside their owning class's file.

## 2. File Header Comments

Every file must open with a banner comment block, and that block must be the very first thing in the file — above everything, including the `using` statements:

```csharp
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

namespace ColumnStore.Data
{
    // ...
}
```

- **Ordering is fixed:** header comment block first, `using` statements immediately below it, then the `namespace` declaration. Nothing — not even a `using` statement — may appear above the header block.
- The banner uses exactly this style: a full-width line of `/` characters, an `// Author:` line, one or more `// Purpose:` (or related) lines, then a closing full-width line of `/` characters.
- **Treat this section as living documentation, not a changelog stamp.** Beyond author and purpose, use it to record:
  - **Assumptions** the type relies on (e.g. "assumes single-writer access; not thread-safe").
  - **Design considerations** that explain *why* the type looks the way it does, especially where the choice isn't obvious from the code alone.
  - Cross-references to the requirements/design docs where relevant, so the reasoning doesn't have to be reconstructed from memory during a future review.
- Update the header when the type's purpose or key assumptions materially change — it should reflect the current design, not the original one.

Example, expanded:
```csharp
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Chunked, typed column storage. Backs every DataColumn<T> except bool, which uses BitmapColumnStorage.
// Assumptions: Single-writer access; not thread-safe. Row indices are always >= 0.
// Design Considerations: Growth happens by appending whole chunks rather than doubling a single contiguous
//   array, to bound any single allocation's size below the LOH threshold (~85,000 bytes) and to avoid O(n)
//   copy-on-grow. See 03_Design.md section 1.2 for the chunk-sizing rationale.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

namespace ColumnStore.Data
{
    // ...
}
```

## 3. Namespaces and `using` Statements

- `using` statements sit directly below the file header comment block (§2) and above the `namespace` declaration — never above the header, never inside the namespace block.
- All `using` statements are sorted alphabetically.
- Remove every unused `using` before committing — no dead imports.
- System namespaces (`System.*`) are grouped together at the top; project/third-party namespaces follow, also alphabetically sorted. Keep a single blank line between the two groups if both are present.
- One namespace per file, matching the project's folder/assembly structure.

## 4. Class Layout — Required Regions

Every class, in this order, using named `#region` blocks:

1. **Private Constants**
2. **Private Members**
3. **Block Dependencies** *(injected collaborators the class relies on but does not own; omit this region if the class has none)*
4. **Constructor / Destructor**
5. **Public Properties**
6. **Public Methods**
7. **Private Methods**

Region names may be adjusted to fit the class (e.g. "Internal Methods" if the class exposes internal-only members), but the **relative order** above must be preserved. Do not interleave — e.g. don't place a private helper method between two public methods; move it to the Private Methods region and let the public region read as a clean contract.

**No blank line immediately after a `#region` tag.** The first member starts on the very next line:

```csharp
namespace ColumnStore.Data
{
    public sealed class TypedColumnStorage<T> : IDataColumnStorage<T>
    {
        #region Private Constants
        // Number of rows stored per chunk. Kept as a power of two so index math
        // can use shift/mask instead of division/modulo.
        private const Int32 CHUNK_SIZE = 128;
        #endregion

        #region Private Members
        // Backing chunks. Grows lazily, one chunk at a time, never reallocated as a whole.
        private readonly List<T[]> storageChunks;
        #endregion

        #region Constructor / Destructor
        // Initializes an empty, zero-chunk storage instance.
        public TypedColumnStorage()
        {
            this.storageChunks = new List<T[]>();
        }
        #endregion

        #region Public Properties
        // The CLR type this storage instance holds.
        public Type DataType => typeof(T);
        #endregion

        #region Public Methods
        // Returns the value at rowIndex. Throws IndexOutOfRangeException for a
        // negative index or an index beyond any allocated chunk.
        public T Get(Int32 rowIndex)
        {
            // ... implementation
        }
        #endregion

        #region Private Methods
        // (example placeholder — private helpers go here)
        #endregion
    }
}
```

**Constructors are `public` by default.** Unless a specific, documented design reason requires restricting construction (e.g. factory-only construction, called out explicitly in the file's header comment per §2), declare constructors `public` so the type can be instantiated directly.

## 5. Naming Conventions

| Element | Convention | Example |
|---|---|---|
| Constants | `UPPER_CASE` with underscores | `CHUNK_SIZE`, `LOH_THRESHOLD_BYTES` |
| Local variables, parameters | camelCase | `rowIndex`, `maxValue` |
| Private fields | camelCase, accessed via `this.` | `this.storageChunks` |
| Methods, properties | PascalCase | `CalculateTotal`, `RowIndex` |
| Classes, interfaces, structs | PascalCase; interfaces prefixed `I` | `DataColumn<T>`, `IDataColumn` |

Additional rules:
- **No `m_` prefix, no leading underscore, on any field.** Access private/instance members through the `this.` qualifier instead of a naming prefix — `this.storageChunks`, not `_storageChunks` or `m_storageChunks`.
- **`var` is reserved for anonymous types only.** For every named type, spell out the type explicitly, even when it's "obvious" from the right-hand side.
  ```csharp
  // Correct
  DataColumn<Int32> quantityColumn = table.Columns.GetColumn<Int32>("Quantity");

  // Not acceptable
  var quantityColumn = table.Columns.GetColumn<Int32>("Quantity");

  // Acceptable — genuinely anonymous type
  var summary = new { Count = total, Average = average };
  ```

## 6. Statement and Whitespace Formatting

- **Braces are always required** around a control-flow body, even for a single statement. However, when the body is a single statement, the whole construct may be written **compactly on one line** instead of expanded across multiple lines:
  ```csharp
  // Compact form — single statement body, acceptable
  if (reader==null) {throw new ArgumentNullException(nameof(reader));}
  ```
  Reserve the multi-line block form for bodies with more than one statement:
  ```csharp
  if (rowIndex < 0)
  {
      LogInvalidIndex(rowIndex);
      throw new IndexOutOfRangeException($"Row Index: {rowIndex}");
  }
  ```
- **Don't add blank lines between adjacent statements or control blocks by default.** Sequential logic reads as one continuous block unless a blank line genuinely separates two distinct steps worth calling out. For example:
  ```csharp
  // Preferred — no forced blank line between the if and the for loop
  if (inferNullability) {TryReadNullability(reader, allowNull);}
  for (Int32 ordinal = 0; ordinal < reader.FieldCount; ordinal++)
  {
      table.Columns.Add(reader.GetName(ordinal), reader.GetFieldType(ordinal), allowNull[ordinal]);
  }
  ```
  rather than inserting a blank line after the `if` before the `for` just out of habit.
- **Don't call a function inside another function's argument list.** Assign the inner call's result to a clearly named local variable first, then pass that variable along:
  ```csharp
  // Avoid
  ProcessRow(BuildRow(reader, ordinal));

  // Prefer
  DataRow row = BuildRow(reader, ordinal);
  ProcessRow(row);
  ```
  This keeps every intermediate value inspectable (in a debugger, in a log statement added later) and keeps each line readable without mentally unwinding nested calls.

## 7. Type Aliases

Use the .NET Base Class Library (BCL) type names instead of the C# language keyword aliases, everywhere: declarations, casts, `typeof`, parameter types, return types.

| Keyword | Use Instead |
|---|---|
| `object` | `Object` |
| `string` | `String` |
| `bool` | `Boolean` |
| `byte` | `Byte` |
| `sbyte` | `SByte` |
| `short` | `Int16` |
| `ushort` | `UInt16` |
| `int` | `Int32` |
| `uint` | `UInt32` |
| `long` | `Int64` |
| `ulong` | `UInt64` |
| `float` | `Single` |
| `double` | `Double` |
| `decimal` | `Decimal` |
| `char` | `Char` |

```csharp
// Correct
public Int32 Count { get; private set; }
public Boolean AllowNull { get; }

// Not acceptable
public int Count { get; private set; }
public bool AllowNull { get; }
```
This applies consistently across the codebase; do not mix keyword and BCL-name styles within the same file or across sibling files for the same concept.

## 8. Comments Requirement

**Every private member, constant, method, and property must have a comment**, in addition to the file-level header from §2. The comment should explain intent/purpose, not restate the signature:

```csharp
// Number of rows stored per chunk. Kept as a power of two so index math
// can use shift/mask instead of division/modulo.
private const Int32 CHUNK_SIZE = 128;

// Translates a logical (visible, post-deletion) row position to its
// physical storage slot. See 03_Design.md section 1.5 for the algorithm.
private Int32 ToPhysicalIndex(Int32 logicalIndex)
{
    // ...
}
```
Public members should also be commented (XML doc comments, `///`, are appropriate for public API surface so IntelliSense/generated docs pick them up); the requirement in this section is a **minimum bar that explicitly includes private members**, which are easy to leave undocumented but are exactly where "why does this exist" tends to get lost over time.

## 9. Input Validation

- **Public and internal methods must validate their inputs.**
  - **Public methods:** invalid input throws an appropriate exception (`ArgumentNullException`, `ArgumentOutOfRangeException`, `InvalidOperationException`, etc.) with a message specific enough to act on.
  - **Internal methods:** use `Contract.Assert` (or the project's equivalent assertion mechanism) to state the precondition, since internal callers are expected to already satisfy it by construction — an internal-method contract violation indicates a bug in this codebase, not bad external input.
- Validate at the top of the method, before any other logic — fail fast.

```csharp
// Public: throws on bad input. Single-statement body — compact form per section 6.
public void Set(Int32 rowIndex, T value)
{
    if (rowIndex < 0) {throw new IndexOutOfRangeException($"Row Index: {rowIndex}");}
    // ...
}

// Internal: asserts the precondition instead of throwing a public-facing exception.
internal void SetInternal(Int32 physicalRowIndex, T value)
{
    Contract.Assert(physicalRowIndex >= 0, "physicalRowIndex must be non-negative.");
    // ...
}
```

## 10. Method Size and Complexity

- **A single method's implementation must not exceed one page** (as a practical rule of thumb: roughly what's visible without scrolling in a normal editor window — treat this as a strong signal, not a hard line-count).
- Keep an eye on **cyclomatic complexity** specifically, not just line count — a short method with deeply nested/branching conditionals is still a violation of this principle even if it fits on a page. If a method needs many branches, extract named private helper methods for each branch's logic.

## 11. Initialization

- **All initialization happens in the constructor**, not at the point of field declaration.
  ```csharp
  // Correct
  private readonly List<T[]> storageChunks;

  public TypedColumnStorage()
  {
      this.storageChunks = new List<T[]>();
  }

  // Not acceptable
  private readonly List<T[]> storageChunks = new List<T[]>();
  ```
  This keeps all initialization logic visible in one place, which matters especially for classes with multiple constructor overloads that need to initialize members conditionally or differently.

## 12. Expression-Bodied Members

- **Expression-bodied syntax is reserved for simple property getters only.** It must never be used for methods — every method, regardless of how small, uses a full body with braces and an explicit `return`.
  ```csharp
  // Acceptable — expression-bodied property getter
  public Type DataType => typeof(T);

  // Also acceptable — traditional property body; prefer this once the getter
  // needs more than a single trivial expression, or a comment inside the body
  public Type DataType
  {
      get { return typeof(T); }
  }

  // Not acceptable — expression-bodied method
  public Int32 GetRowCount() => this.Rows.Count;

  // Correct — methods always use a full body
  public Int32 GetRowCount()
  {
      return this.Rows.Count;
  }
  ```

---

## 13. Quick-Reference Checklist

Before submitting code for review, confirm:

- [ ] One class/interface per file; file name matches type name.
- [ ] File-level author/purpose banner is the very first thing in the file — `using` statements come directly below it, never above.
- [ ] `using` statements sorted, unused ones removed.
- [ ] Regions present, in order: Private Constants → Private Members → Block Dependencies (if any) → Constructor/Destructor → Public Properties → Public Methods → Private Methods. No blank line immediately after a `#region` tag.
- [ ] Constructors are `public` unless a documented design reason restricts them.
- [ ] Constants are `UPPER_CASE`; no `m_`/underscore-prefixed fields; `this.` used for instance member access.
- [ ] Braces used on every `if`/`for`/`while`/`else`; single-statement bodies may use the compact one-line form.
- [ ] No unnecessary blank lines between adjacent statements or control blocks.
- [ ] No function call nested inside another function call's arguments — use a named local variable instead.
- [ ] No `var` except for genuinely anonymous types.
- [ ] BCL type names used (`Int32`, `String`, `Boolean`, ...) instead of C# keyword aliases.
- [ ] Every private member, constant, method, and property has a comment explaining intent.
- [ ] Public/internal methods validate inputs (exceptions for public, `Contract.Assert` for internal).
- [ ] No method exceeds roughly a page; cyclomatic complexity kept low via extraction.
- [ ] All initialization happens in the constructor, not at field declaration.
- [ ] Expression-bodied syntax used only for simple property getters, never for methods.
- [ ] Don't use this. for method calls or function calls
- [ ] Don't have extra line 
