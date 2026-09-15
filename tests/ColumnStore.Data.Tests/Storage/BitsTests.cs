///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Author: Nandika
// Purpose: Proves that the bit operations the storage layer depends on return the same answers on every target
//   framework - which matters because .NET Framework 4.8 has none of them and uses a separate software
//   implementation, while .NET 8 and .NET 10 forward to single machine instructions.
// Assumptions: These tests run on every framework the library targets. That is the whole point: the same assertions
//   exercise the intrinsic path on .NET 8 and .NET 10 and the software path on .NET Framework, so a divergence fails
//   here rather than as a wrong row count somewhere far away.
// Design Considerations: EVERY EXPECTATION IS COMPUTED INDEPENDENTLY, by a naive loop written out in this file, and
//   never by the operation under test or by System.Numerics.BitOperations. Comparing the fast implementation against
//   the framework's would prove nothing on .NET Framework, where the framework's does not exist; comparing it against
//   itself would prove nothing anywhere. A bit-by-bit loop is obviously correct on inspection, which is exactly what
//   a reference implementation has to be.
//   THE INPUTS ARE EXHAUSTIVE WHERE THEY CAN BE. Every single-bit word, every power of two, and every bit position
//   are all small enough to enumerate completely, so the cases most likely to be wrong - zero, the lowest bit, the
//   highest bit, the 32-bit boundary the software TrailingZeroCount splits on - are covered by construction rather
//   than by remembering to add them.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

using ColumnStore.Data.Storage;
using Xunit;

namespace ColumnStore.Data.Tests.Storage
{
    /// <summary>
    /// Tests for <see cref="Bits"/>.
    /// </summary>
    public sealed class BitsTests
    {
        #region Public Methods
        /// <summary>
        /// PopCount agrees with a bit-by-bit count for zero, for every single-bit word, for the all-ones word, and
        /// for a spread of mixed patterns.
        /// </summary>
        [Fact]
        public void PopCountMatchesACountingLoop()
        {
            Assert.Equal(0, Bits.PopCount(0UL));
            Assert.Equal(64, Bits.PopCount(UInt64.MaxValue));
            for (Int32 bitPosition = 0; bitPosition < 64; bitPosition++)
            {
                UInt64 singleBit = 1UL << bitPosition;
                Assert.Equal(1, Bits.PopCount(singleBit));
                Assert.Equal(63, Bits.PopCount(~singleBit));
                // A word with every bit at or below this position set.
                UInt64 lowRun = bitPosition == 63 ? UInt64.MaxValue : (1UL << (bitPosition + 1)) - 1UL;
                Assert.Equal(bitPosition + 1, Bits.PopCount(lowRun));
            }
            foreach (UInt64 pattern in MixedPatterns())
            {
                Assert.Equal(CountBitsOneByOne(pattern), Bits.PopCount(pattern));
            }
        }

        /// <summary>
        /// TrailingZeroCount agrees with a scanning loop, including the 64 it must return for zero and the 32-bit
        /// boundary the software implementation splits the word on.
        /// </summary>
        [Fact]
        public void TrailingZeroCountMatchesAScanningLoop()
        {
            Assert.Equal(64, Bits.TrailingZeroCount(0UL));
            for (Int32 bitPosition = 0; bitPosition < 64; bitPosition++)
            {
                UInt64 singleBit = 1UL << bitPosition;
                Assert.Equal(bitPosition, Bits.TrailingZeroCount(singleBit));
                // Anything set above the lowest bit must not change the answer.
                UInt64 withHighNoise = singleBit | (0xF000000000000000UL & ~((singleBit << 1) - 1UL));
                Assert.Equal(bitPosition, Bits.TrailingZeroCount(withHighNoise));
            }
            foreach (UInt64 pattern in MixedPatterns())
            {
                Assert.Equal(ScanTrailingZeros(pattern), Bits.TrailingZeroCount(pattern));
            }
        }

        /// <summary>
        /// IsPow2 is true for exactly the powers of two, and false for zero, for negatives and for Int32.MinValue -
        /// which has a single bit set and would fool a naive (value and value minus one) test.
        /// </summary>
        [Fact]
        public void IsPow2IsTrueForExactlyThePowersOfTwo()
        {
            Assert.False(Bits.IsPow2(0));
            Assert.False(Bits.IsPow2(-1));
            Assert.False(Bits.IsPow2(-8));
            Assert.False(Bits.IsPow2(Int32.MinValue));
            for (Int32 exponent = 0; exponent < 31; exponent++)
            {
                Int32 powerOfTwo = 1 << exponent;
                Assert.True(Bits.IsPow2(powerOfTwo), $"2^{exponent} = {powerOfTwo} was not recognised as a power of two.");
                if (powerOfTwo > 2) { Assert.False(Bits.IsPow2(powerOfTwo - 1)); }
                if (powerOfTwo > 1) { Assert.False(Bits.IsPow2(powerOfTwo + 1)); }
            }
        }

        /// <summary>
        /// Log2 agrees with a shifting loop for every power of two, for the value just below and just above each, and
        /// returns zero for zero as the framework's own does.
        /// </summary>
        [Fact]
        public void Log2MatchesAShiftingLoop()
        {
            Assert.Equal(0, Bits.Log2(0U));
            Assert.Equal(0, Bits.Log2(1U));
            Assert.Equal(31, Bits.Log2(UInt32.MaxValue));
            for (Int32 exponent = 0; exponent < 32; exponent++)
            {
                UInt32 powerOfTwo = 1U << exponent;
                Assert.Equal(exponent, Bits.Log2(powerOfTwo));
                Assert.Equal(FloorLog2ByShifting(powerOfTwo), Bits.Log2(powerOfTwo));
                if (powerOfTwo > 1U)
                {
                    Assert.Equal(exponent - 1, Bits.Log2(powerOfTwo - 1U));
                    Assert.Equal(exponent, Bits.Log2(powerOfTwo + 1U));
                }
            }
        }

        /// <summary>
        /// CombineHashes is deterministic, order sensitive, and does not collapse the common small-integer pairs onto
        /// one another. It is not required to match System.HashCode - the two frameworks legitimately differ - only
        /// to be a usable hash within one process.
        /// </summary>
        [Fact]
        public void CombineHashesIsStableAndOrderSensitive()
        {
            Assert.Equal(Bits.CombineHashes(17, 42), Bits.CombineHashes(17, 42));
            Assert.NotEqual(Bits.CombineHashes(17, 42), Bits.CombineHashes(42, 17));
            System.Collections.Generic.HashSet<Int32> seenHashes = new System.Collections.Generic.HashSet<Int32>();
            Int32 collisions = 0;
            for (Int32 first = 0; first < 100; first++)
            {
                for (Int32 second = 0; second < 100; second++)
                {
                    if (seenHashes.Add(Bits.CombineHashes(first, second)) == false) { collisions = collisions + 1; }
                }
            }
            Assert.Equal(0, collisions);
        }

        /// <summary>
        /// HoldsReferences answers for the cases the storage layer actually asks about: a reference type must be
        /// overwritten when a cell is nulled, and a plain value type need not be. The struct-containing-a-reference
        /// case is the one a naive IsValueType check would get wrong.
        /// </summary>
        [Fact]
        public void HoldsReferencesDistinguishesTypesThatCanKeepAnObjectAlive()
        {
            Assert.True(Bits.HoldsReferences<String>());
            Assert.True(Bits.HoldsReferences<Object>());
            Assert.True(Bits.HoldsReferences<Int32[]>());
            Assert.False(Bits.HoldsReferences<Int32>());
            Assert.False(Bits.HoldsReferences<Boolean>());
            Assert.False(Bits.HoldsReferences<Decimal>());
            Assert.False(Bits.HoldsReferences<DateTime>());
            Assert.False(Bits.HoldsReferences<Guid>());
            Assert.False(Bits.HoldsReferences<DayOfWeek>());
            Assert.True(Bits.HoldsReferences<StructHoldingAReference>());
            Assert.False(Bits.HoldsReferences<StructHoldingOnlyValues>());
            Assert.True(Bits.HoldsReferences<StructHoldingAStructHoldingAReference>());
        }
        #endregion

        #region Private Methods
        // Counts set bits one at a time. Obviously correct on inspection, which is what a reference implementation
        // has to be.
        private static Int32 CountBitsOneByOne(UInt64 word)
        {
            Int32 count = 0;
            for (Int32 bitPosition = 0; bitPosition < 64; bitPosition++)
            {
                if ((word & (1UL << bitPosition)) != 0UL) { count = count + 1; }
            }
            return count;
        }

        // Finds the lowest set bit by scanning upward, returning 64 when there is none.
        private static Int32 ScanTrailingZeros(UInt64 word)
        {
            for (Int32 bitPosition = 0; bitPosition < 64; bitPosition++)
            {
                if ((word & (1UL << bitPosition)) != 0UL) { return bitPosition; }
            }
            return 64;
        }

        // Floor of log2, by shifting right until nothing is left.
        private static Int32 FloorLog2ByShifting(UInt32 value)
        {
            if (value == 0U) { return 0; }
            Int32 exponent = -1;
            UInt32 remaining = value;
            while (remaining != 0U)
            {
                remaining = remaining >> 1;
                exponent = exponent + 1;
            }
            return exponent;
        }

        // A spread of words chosen to exercise sparse, dense, alternating and boundary-straddling patterns.
        private static UInt64[] MixedPatterns()
        {
            return new UInt64[]
            {
                0UL,
                1UL,
                2UL,
                0xFFFFFFFFUL,
                0x100000000UL,
                0xFFFFFFFF00000000UL,
                0xAAAAAAAAAAAAAAAAUL,
                0x5555555555555555UL,
                0x0123456789ABCDEFUL,
                0xFEDCBA9876543210UL,
                0x8000000000000000UL,
                UInt64.MaxValue
            };
        }
        #endregion

        #region Nested Types
        // These fixtures exist for their SHAPE, never for their values: HoldsReferences inspects the field types and
        // nothing ever reads or writes an instance. CS0649 is therefore correct and irrelevant here, and is silenced
        // over this region only.
#pragma warning disable CS0649
        // A value type that transitively keeps an object alive, which is the case a plain IsValueType test misses.
        private struct StructHoldingAReference
        {
            public Int32 Count;
            public String Label;
        }

        // A value type that keeps nothing alive.
        private struct StructHoldingOnlyValues
        {
            public Int32 Count;
            public Double Weight;
        }

        // One level deeper, to prove the walk recurses rather than inspecting only the outermost fields.
        private struct StructHoldingAStructHoldingAReference
        {
            public Int32 Count;
            public StructHoldingAReference Inner;
        }
#pragma warning restore CS0649
        #endregion
    }
}
