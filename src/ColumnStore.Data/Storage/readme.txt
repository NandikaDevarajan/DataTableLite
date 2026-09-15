ColumnStore.Data - Storage layer fundamentals
================================================================================

WHAT THIS LAYER IS
--------------------------------------------------------------------------------
The rawest layer in the library: "index in, value out" and "index in, value
stored". Nothing here knows about column names, table schemas or row semantics,
and nothing here references ADO.NET. That isolation is what makes this layer
independently testable and reusable.

MEMORY SHAPE - WHY CHUNKS INSTEAD OF ONE BIG ARRAY
--------------------------------------------------------------------------------
The obvious columnar layout is one T[] per column that doubles when it fills up.
That is a bad fit for large tables for two reasons:

  1. Large Object Heap. Any array of 85,000 bytes or more is allocated on the
     LOH, which is not compacted by default and is only collected on a gen2
     collection. A million-row Int32 column would be a 4 MB LOH array; ten of
     them fragment the LOH badly.

  2. Copy-on-grow. Doubling a 4 MB array means allocating 8 MB and copying 4 MB.
     Those periodic large copies show up as latency spikes while loading.

So a column's values live in a List<T[]> of fixed-size chunks:

     rowIndex ->  chunkIndex   = rowIndex >> chunkShift
                  indexInChunk = rowIndex &  chunkMask

Chunks are appended, never reallocated, and only chunks actually touched are
allocated. ChunkSizing picks a power-of-two row count per chunk so the index
math is a shift and a mask instead of a division and a modulo.

How BIG a chunk should be is a genuine trade, and ChunkSizing offers two
answers. The DEFAULT is a fixed 128 rows, which keeps a small table cheap: a
hundred-row, ten-column table costs about 12 KB rather than the ~560 KB a
32 KB-per-chunk sizing would demand. The LOH-SAFE rule, LohSafeRowCount<T>(),
computes the 16 KB - 64 KB figure NFR-3 asks for, sized per element type; pass
it to Columns.Add<T>(name, allowNull, chunkRowCount) for a column that will hold
a very large number of rows, where fewer, larger chunks scan faster and cost
less per-array overhead. Neither setting comes close to the LOH threshold, so
what varies between them is the NUMBER of allocations, not their size.
The benchmark's chunk-size sweep measures both, and the numbers are in
../../../README.md under "Chunk size".

BITS, NOT BYTES, FOR BOOLEANS AND NULLS
--------------------------------------------------------------------------------
A Boolean in a T[] costs one byte per row - eight times what it needs.
BitmapColumnStorage packs 64 rows into one UInt64, and is used twice through one
mechanism:

  * as the storage for a Boolean column (1 bit per row, not 1 byte), and
  * as the null-tracking side channel for any nullable column of any type.

Null tracking as a bitmap is what lets a nullable Int32 column cost 4 bytes and
one bit per row, instead of the 8-16 bytes per row a Nullable<Int32>[] or a
boxed Object[] would cost, with no in-band sentinel value stealing a valid value
from the column's range.

The sense of that bit matters. A SET bit means the cell IS NULL, not that it has
a value. Real data is overwhelmingly non-null, so writing a value should not have
to write to the bitmap at all - and it does not: DataColumn<T>.Set reads the bit
and clears it only if it was set. A nullable column that never actually receives
a null therefore never grows its word list, costing nothing in memory and nothing
in stores. What makes that safe is a per-column watermark in DataColumn<T>: rows
above the highest row ever written are null by definition, so an untouched cell
still reads as null without any bit having been written for it.

Reading an untouched high row index from a bitmap returns false rather than
throwing: an untouched row is "not null" / false by definition, and callers of
the null side channel legitimately probe rows whose value chunk exists but whose
null word has never been written.

LOGICAL DELETION
--------------------------------------------------------------------------------
Physically removing a row from a columnar store means shifting every subsequent
value of every column down by one - O(rows x columns) per delete. Instead,
RowDeletionTracker marks the row's physical slot in a tombstone bitmap
(1 = deleted) and answers two questions:

     ToPhysicalIndex(logicalIndex)   the storage slot behind the i-th *visible*
                                     row, skipping tombstones
     ToLogicalIndex(physicalIndex)   the reverse

Doing that naively means counting alive bits from the start of the bitmap on
every lookup - O(rows / 64) per call. So the tracker keeps a per-word alive
count with a Fenwick (binary indexed) tree over it, in AliveRowPrefixIndex:
O(log words) to record a deletion, O(log words) to translate an index, and no
rescanning. When nothing has been deleted the translation short-circuits to the
identity, so the common case costs nothing at all.

Physical slots are never reused after a deletion. "Physical index is permanent"
keeps every cached DataRow handle and every stored index valid for the lifetime
of the table.

WHAT LIVES HERE
--------------------------------------------------------------------------------
  IDataColumnStorage<T>     Get / Set / Clear. The extensibility seam - a future
                            sparse or memory-mapped storage plugs in here
                            without the Column or Row layers changing.
  TypedColumnStorage<T>     Chunked T[] growth. Backs every column except bool.
  BitmapColumnStorage       Bit-packed UInt64 words. Booleans and null flags.
  ChunkSizing               Chunk row-count calculation: the tunable default (128 rows)
                            and the 16-64 KB LOH-safe rule NFR-3 describes.
  DataColumnStorageFactory  The single decision point for "which storage does
                            this T get" - the guarantee behind bit-packed bools.
  RowDeletionTracker        Tombstones plus logical/physical index translation.
  AliveRowPrefixIndex       Fenwick tree that makes that translation O(log n).

See ../../../Spec/03_Design.md section 1 for the specified algorithms and
section 6.1 for the test plan this layer is verified against.
