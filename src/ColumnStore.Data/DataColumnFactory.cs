///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: Turns a runtime Type into a DataColumn<T>. This is the one and only place in the library where reflection
//   is permitted, and it runs exactly once per column.
// Assumptions: Called at schema-definition time only - never per row, never per cell. Callers that know the element
//   type at compile time should use DataColumnCollection.Add<T> instead and skip this type entirely.
// Design Considerations: The architecture's governing rule is "schema-time cost, runtime zero-cost" (02_Architecture
//   section 1). Concentrating MakeGenericType and Activator.CreateInstance here is how that rule is enforced: there
//   is nowhere else for per-row type dispatch to hide.
//   Every common column type is handled by an explicit fast path that constructs DataColumn<T> directly, so the
//   typical schema is built with no reflection at all - the reflective fallback exists for genuinely arbitrary types
//   (enums, custom structs, provider-specific types). Boolean is first in that list, per 03_Design.md section 2.3,
//   though the bit-packing guarantee itself comes from DataColumnStorageFactory and holds on the reflective path too.
//   Nullable<T> is unwrapped rather than rejected: callers naturally write typeof(Int32?) for a nullable column, and a
//   column of Nullable<Int32> would defeat the whole point by storing eight bytes per row plus its own null flag,
//   instead of four bytes and one bit.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Reflection;

namespace ColumnStore.Data
{
    /// <summary>
    /// Creates typed columns from a runtime <see cref="Type"/>.
    /// </summary>
    public static class DataColumnFactory
    {
        #region Private Constants
        // Binding flags for locating DataColumn<T>'s internal constructor. Construction is deliberately factory-only -
        // see DataColumn.cs's header for the FR-3 reasoning behind that restriction.
        private const BindingFlags COLUMN_CONSTRUCTOR_BINDING = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        #endregion

        #region Public Methods
        /// <summary>
        /// Creates a column of the given runtime type. A <see cref="System.Nullable{T}"/> type is unwrapped to its
        /// underlying type and forces <paramref name="allowNull"/> to true.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <param name="dataType">The CLR type of the column's values.</param>
        /// <param name="allowNull">Whether cells may be null.</param>
        /// <returns>The created column, as the type-erased interface.</returns>
        /// <exception cref="ArgumentNullException">The name or type is null.</exception>
        /// <exception cref="ArgumentException">The name is blank, or the type cannot back a column.</exception>
        public static DataColumn Create(String name, Type dataType, Boolean allowNull)
        {
            if (name == null) { throw new ArgumentNullException(nameof(name)); }
            if (dataType == null) { throw new ArgumentNullException(nameof(dataType)); }
            if (String.IsNullOrWhiteSpace(name)) { throw new ArgumentException("Column name must not be blank.", nameof(name)); }
            Type effectiveType = dataType;
            Boolean effectiveAllowNull = allowNull;
            Type nullableUnderlyingType = Nullable.GetUnderlyingType(dataType);
            if (nullableUnderlyingType != null)
            {
                effectiveType = nullableUnderlyingType;
                effectiveAllowNull = true;
            }
            ValidateColumnType(effectiveType, name);
            DataColumn wellKnownColumn = TryCreateWellKnownColumn(name, effectiveType, effectiveAllowNull);
            if (wellKnownColumn != null) { return wellKnownColumn; }
            DataColumn reflectedColumn = CreateByReflection(name, effectiveType, effectiveAllowNull);
            return reflectedColumn;
        }
        #endregion

        #region Private Methods
        // Rejects types that cannot sensibly back a column before reflection is attempted, so the failure names the
        // column and the offending type instead of surfacing as a TypeLoadException from MakeGenericType.
        private static void ValidateColumnType(Type dataType, String name)
        {
            if (dataType == typeof(void)) { throw new ArgumentException($"Column '{name}' cannot be of type void.", nameof(dataType)); }
            if (dataType.IsGenericTypeDefinition) { throw new ArgumentException($"Column '{name}' cannot be of open generic type {dataType.FullName}.", nameof(dataType)); }
            if (dataType.IsPointer) { throw new ArgumentException($"Column '{name}' cannot be of pointer type {dataType.FullName}.", nameof(dataType)); }
            if (dataType.IsByRef) { throw new ArgumentException($"Column '{name}' cannot be of by-ref type {dataType.FullName}.", nameof(dataType)); }
        }

        // Constructs the column directly for the types that make up practically every real schema, so building a
        // schema costs no reflection at all. Split in two by category purely to keep each method's branch count low.
        private static DataColumn TryCreateWellKnownColumn(String name, Type dataType, Boolean allowNull)
        {
            DataColumn numericColumn = TryCreateNumericColumn(name, dataType, allowNull);
            if (numericColumn != null) { return numericColumn; }
            DataColumn otherColumn = TryCreateNonNumericColumn(name, dataType, allowNull);
            return otherColumn;
        }

        // Fast paths for the numeric primitives.
        private static DataColumn TryCreateNumericColumn(String name, Type dataType, Boolean allowNull)
        {
            if (dataType == typeof(Int32)) { return new DataColumn<Int32>(name, allowNull); }
            if (dataType == typeof(Int64)) { return new DataColumn<Int64>(name, allowNull); }
            if (dataType == typeof(Decimal)) { return new DataColumn<Decimal>(name, allowNull); }
            if (dataType == typeof(Double)) { return new DataColumn<Double>(name, allowNull); }
            if (dataType == typeof(Single)) { return new DataColumn<Single>(name, allowNull); }
            if (dataType == typeof(Int16)) { return new DataColumn<Int16>(name, allowNull); }
            if (dataType == typeof(Byte)) { return new DataColumn<Byte>(name, allowNull); }
            if (dataType == typeof(SByte)) { return new DataColumn<SByte>(name, allowNull); }
            if (dataType == typeof(UInt16)) { return new DataColumn<UInt16>(name, allowNull); }
            if (dataType == typeof(UInt32)) { return new DataColumn<UInt32>(name, allowNull); }
            if (dataType == typeof(UInt64)) { return new DataColumn<UInt64>(name, allowNull); }
            return null;
        }

        // Fast paths for Boolean (first, per the design's explicit call-out) and the remaining common types.
        private static DataColumn TryCreateNonNumericColumn(String name, Type dataType, Boolean allowNull)
        {
            if (dataType == typeof(Boolean)) { return new DataColumn<Boolean>(name, allowNull); }
            if (dataType == typeof(String)) { return new DataColumn<String>(name, allowNull); }
            if (dataType == typeof(DateTime)) { return new DataColumn<DateTime>(name, allowNull); }
            if (dataType == typeof(DateTimeOffset)) { return new DataColumn<DateTimeOffset>(name, allowNull); }
            if (dataType == typeof(TimeSpan)) { return new DataColumn<TimeSpan>(name, allowNull); }
            if (dataType == typeof(Guid)) { return new DataColumn<Guid>(name, allowNull); }
            if (dataType == typeof(Char)) { return new DataColumn<Char>(name, allowNull); }
            if (dataType == typeof(Byte[])) { return new DataColumn<Byte[]>(name, allowNull); }
            if (dataType == typeof(Object)) { return new DataColumn<Object>(name, allowNull); }
            return null;
        }

        // The reflective fallback, for element types outside the well-known set: enums, custom structs, provider
        // types. One MakeGenericType and one Activator.CreateInstance per column, never per row.
        private static DataColumn CreateByReflection(String name, Type dataType, Boolean allowNull)
        {
            Type openColumnType = typeof(DataColumn<>);
            Type closedColumnType = openColumnType.MakeGenericType(dataType);
            Object[] constructorArguments = new Object[] { name, allowNull };
            Object createdColumn = Activator.CreateInstance(closedColumnType, COLUMN_CONSTRUCTOR_BINDING, null, constructorArguments, null);
            return (DataColumn)createdColumn;
        }
        #endregion
    }
}
