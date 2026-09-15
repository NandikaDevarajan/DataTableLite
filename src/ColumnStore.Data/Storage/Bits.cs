///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Please read readme.txt in this folder to understand the fundamentals behind this approach
// Purpose: The four bit operations the storage layer depends on, in one place, so that the same source compiles
//   against .NET Framework 4.8 - which has none of them - and against .NET 8 and .NET 10, which have all four as
//   single machine instructions.
// Assumptions: Every operand is treated as unsigned. Zero is a legitimate input to PopCount, TrailingZeroCount and
//   Log2, and each returns what System.Numerics.BitOperations returns for it, so the two implementations are
//   interchangeable rather than merely similar.
// Design Considerations: WHY A WRAPPER RATHER THAN #if AT EVERY CALL SITE. These operations sit inside the deletion
//   index and the chunk-address calculation - the two places the library can least afford to become hard to read.
//   Conditional compilation scattered through them would have to be re-verified on both frameworks every time either
//   is touched. One wrapper means the call sites stay identical on every target and there is exactly one file where
//   "does .NET Framework have this?" is ever asked.
//   THE MODERN PATH COSTS NOTHING. On .NET 8 and .NET 10 each method is an AggressiveInlining forward to
//   System.Numerics.BitOperations, which the JIT turns into POPCNT / TZCNT / LZCNT where the hardware has them. The
//   wrapper disappears entirely; it is not an abstraction the runtime has to pay for.
//   THE .NET FRAMEWORK PATH IS THE TEXTBOOK SOFTWARE FALLBACK, not a clever one. PopCount is the standard SWAR
//   reduction; TrailingZeroCount isolates the lowest set bit and takes its logarithm; Log2 is a de Bruijn multiply
//   and table lookup. All three are branch-free and constant-time. They are slower than the instructions they stand
//   in for - which is a real, measurable cost on .NET Framework, paid only on deletion-heavy work - and they are
//   correct, which matters more than fast on a framework being supported rather than targeted.
//   BMI2 IS NOT USED HERE. RowDeletionTracker's FindNthAliveBit reaches for Bmi2.ParallelBitDeposit when the hardware
//   offers it, and System.Runtime.Intrinsics does not exist on .NET Framework at all. That one call site keeps its
//   own conditional, because there the fallback is a different algorithm rather than a different implementation of
//   the same one.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Runtime.CompilerServices;

#if !NETFRAMEWORK
using System.Numerics;
#endif

namespace ColumnStore.Data.Storage
{
    /// <summary>
    /// Bit operations used by the storage layer, uniform across every target framework.
    /// </summary>
    internal static class Bits
    {
#if NETFRAMEWORK
        #region Private Constants
        // De Bruijn sequence for 32-bit floor-log2. Multiplying an isolated single bit by this constant and taking
        // the top five bits of the product yields a unique index per bit position, which the table below maps to that
        // position. The standard trick, and the reason Log2 needs no loop.
        private const UInt32 DE_BRUIJN_SEQUENCE = 0x07C4ACDDU;

        // Masks used by the SWAR population count: alternating 1-bit, 2-bit and 4-bit field selectors, then the
        // multiplier that sums all eight byte-lanes into the top byte.
        private const UInt64 PAIR_MASK = 0x5555555555555555UL;
        private const UInt64 NIBBLE_MASK = 0x3333333333333333UL;
        private const UInt64 BYTE_MASK = 0x0F0F0F0F0F0F0F0FUL;
        private const UInt64 LANE_SUM_MULTIPLIER = 0x0101010101010101UL;
        #endregion

        #region Private Members
        // Bit position for each De Bruijn hash value. Indexed by the top five bits of the multiply.
        private static readonly Int32[] DeBruijnBitPosition = new Int32[32]
        {
             0,  9,  1, 10, 13, 21,  2, 29,
            11, 14, 16, 18, 22, 25,  3, 30,
             8, 12, 20, 28, 15, 17, 24,  7,
            19, 27, 23,  6, 26,  5,  4, 31
        };
        #endregion
#endif

        #region Public Methods
        /// <summary>
        /// Number of set bits in a 64-bit word.
        /// </summary>
        /// <param name="word">The word to count.</param>
        /// <returns>The population count, 0 to 64.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Int32 PopCount(UInt64 word)
        {
#if NETFRAMEWORK
            // SWAR: sum bits pairwise, then in nibbles, then in bytes, then add the byte lanes with one multiply.
            UInt64 counted = word - ((word >> 1) & PAIR_MASK);
            counted = (counted & NIBBLE_MASK) + ((counted >> 2) & NIBBLE_MASK);
            counted = (counted + (counted >> 4)) & BYTE_MASK;
            return (Int32)((counted * LANE_SUM_MULTIPLIER) >> 56);
#else
            return BitOperations.PopCount(word);
#endif
        }

        /// <summary>
        /// Number of zero bits below the lowest set bit of a 64-bit word, or 64 when the word is zero - matching
        /// <c>System.Numerics.BitOperations.TrailingZeroCount</c> exactly, including for zero.
        /// </summary>
        /// <param name="word">The word to inspect.</param>
        /// <returns>The trailing zero count, 0 to 64.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Int32 TrailingZeroCount(UInt64 word)
        {
#if NETFRAMEWORK
            if (word == 0UL) { return 64; }
            // Isolate the lowest set bit, then take its position. Two's complement negation of an unsigned value is
            // written as (~word + 1) to stay inside UInt64 arithmetic.
            UInt64 lowestSetBit = word & (~word + 1UL);
            UInt32 lowHalf = (UInt32)lowestSetBit;
            if (lowHalf != 0U) { return Log2(lowHalf); }
            return 32 + Log2((UInt32)(lowestSetBit >> 32));
#else
            return BitOperations.TrailingZeroCount(word);
#endif
        }

        /// <summary>
        /// True when the value is a positive power of two.
        /// </summary>
        /// <param name="value">The value to test.</param>
        /// <returns>True for 1, 2, 4, 8 and so on; false for zero and for negatives.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Boolean IsPow2(Int32 value)
        {
#if NETFRAMEWORK
            return value > 0 && (value & (value - 1)) == 0;
#else
            return BitOperations.IsPow2(value);
#endif
        }

        /// <summary>
        /// Floor of the base-2 logarithm, or 0 when the value is zero - matching <c>System.Numerics.BitOperations.Log2</c>
        /// exactly, including for zero.
        /// </summary>
        /// <param name="value">The value to take the logarithm of.</param>
        /// <returns>The floor of log2, 0 to 31.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Int32 Log2(UInt32 value)
        {
#if NETFRAMEWORK
            if (value == 0U) { return 0; }
            // Smear the highest set bit down over every lower bit, producing a value of the form 2^(n+1) - 1, whose
            // De Bruijn hash identifies n.
            UInt32 smeared = value;
            smeared = smeared | (smeared >> 1);
            smeared = smeared | (smeared >> 2);
            smeared = smeared | (smeared >> 4);
            smeared = smeared | (smeared >> 8);
            smeared = smeared | (smeared >> 16);
            UInt32 hash = (smeared * DE_BRUIJN_SEQUENCE) >> 27;
            return DeBruijnBitPosition[hash];
#else
            return BitOperations.Log2(value);
#endif
        }

        /// <summary>
        /// Combines two hash codes into one. Stands in for <c>System.HashCode.Combine</c>, which .NET Framework does
        /// not have.
        /// </summary>
        /// <param name="firstHash">The first hash code.</param>
        /// <param name="secondHash">The second hash code.</param>
        /// <returns>The combined hash code.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Int32 CombineHashes(Int32 firstHash, Int32 secondHash)
        {
#if NETFRAMEWORK
            // The multiply-and-add form used throughout the framework's own value types. Unchecked because hash
            // arithmetic is expected to wrap.
            unchecked
            {
                Int32 combined = (int)2166136261;
                combined = (combined * 16777619) ^ firstHash;
                combined = (combined * 16777619) ^ secondHash;
                return combined;
            }
#else
            return HashCode.Combine(firstHash, secondHash);
#endif
        }

        /// <summary>
        /// True when <typeparamref name="T"/> is a reference type or a value type that contains references, and so
        /// must be overwritten rather than abandoned in place when a cell is nulled. Stands in for
        /// <c>RuntimeHelpers.IsReferenceOrContainsReferences</c>, which .NET Framework does not have.
        /// </summary>
        /// <typeparam name="T">The type to inspect.</typeparam>
        /// <returns>True when the type can keep an object alive.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Boolean HoldsReferences<T>()
        {
#if NETFRAMEWORK
            // A static readonly field on a generic type is computed once per closed instantiation and then read as a
            // constant, which is the closest .NET Framework gets to the JIT-time constant the modern intrinsic is.
            return ReferenceHolding<T>.Value;
#else
            return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#endif
        }
        #endregion

#if NETFRAMEWORK
        #region Nested Types
        // Per-instantiation cache for HoldsReferences. Separate generic type so the static constructor runs once for
        // each T rather than once for the whole of Bits.
        private static class ReferenceHolding<T>
        {
            #region Public Members
            // Whether T is a reference type, or a struct with any field that transitively holds one.
            public static readonly Boolean Value = Compute(typeof(T));
            #endregion

            #region Private Methods
            // Walks a value type's instance fields looking for anything that can reference the heap. Recursion
            // terminates because a struct cannot contain itself, and the result is cached per T.
            private static Boolean Compute(Type candidateType)
            {
                if (candidateType.IsValueType == false) { return true; }
                if (candidateType.IsPrimitive || candidateType.IsEnum || candidateType.IsPointer) { return false; }
                System.Reflection.FieldInfo[] fields = candidateType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                for (Int32 fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
                {
                    Type fieldType = fields[fieldIndex].FieldType;
                    if (fieldType == candidateType) { continue; }
                    if (Compute(fieldType)) { return true; }
                }
                return false;
            }
            #endregion
        }
        #endregion
#endif
    }
}
