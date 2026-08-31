# Cartograph

Portable memory-mapped data artifacts for .NET. Opening is constant-time regardless of size, memory stays flat, and processes share one physical copy. Records stream as ReadOnlySequence with no heap copies — vectors, logs, columns, documents. RAG over corpora larger than RAM is the first demo.

---

> [!WARNING]
> **Experimental — unstable file format.** Cartograph is `0.1.0-alpha`. The on-disk format is
> versioned but **not yet stable**; artifacts written by one pre-release build may not open in the
> next. Do not use it for data you cannot regenerate.

## Build once, map instantly

The **file is the format**. Opening an artifact is `mmap` + header validation — there is no
deserialization step, so **open cost is O(1) regardless of file size**. From there:

- **Constant-time open.** You pay for the header and manifest, never the payload. A 200 GB artifact
  opens as fast as a 2 MB one.
- **Flat memory.** Pages arrive on demand from the OS. A dataset larger than RAM degrades
  gracefully — clean file-backed pages are just reclaimable page cache — instead of throwing
  `OutOfMemoryException`.
- **Cross-process page sharing.** Multiple processes mapping the same read-only artifact share
  **one** physical copy through the OS page cache.
- **Records without heap copies.** Records stream out as `ReadOnlySequence<byte>` with no copy into
  the managed heap, so any record type works: vectors, logs, columns, documents.

### A note on allocation

Cartograph eliminates allocation **proportional to payload size**, not all allocation. The memory
manager, sequence segments, leases, and metadata are themselves managed objects — so the honest
claim is **zero steady-state managed allocation**. What that buys you is control over total
GC-visible working set and the bulk-buffer churn that comes from documents, decoded strings, and
serialization buffers. (It is *not* about the Large Object Heap: a 1536-dim float32 embedding is only
~6 KB and never reaches the 85 KB LOH threshold.)

The same discipline now applies to *writing*: records appended with `AddFileRecord` are streamed
during `Save` rather than buffered, so packing no longer scales its memory with payload size. See
[Writing artifacts larger than memory](#writing-artifacts-larger-than-memory).

## Quickstart

```csharp
using System.Buffers;
using System.Runtime.InteropServices;
using Cartograph.Format;

// --- Build once ---
var writer = new SegmentedArtifactWriter();
var segment = writer.AddSegment();

float[] embedding = GetEmbedding();               // e.g. 768 dims
segment.AddRecord(MemoryMarshal.AsBytes<float>(embedding));
segment.AddRecord("a log line, a column chunk, a document…"u8);

writer.Save("corpus.ctg");

// --- Map instantly (constant-time open) ---
using Artifact artifact = Artifact.Open("corpus.ctg");   // mmap + validate, no payload read

ArtifactSegment records = artifact.Segments[0];
for (int i = 0; i < records.RecordCount; i++)
{
    using RecordLease record = records.ReadRecord(i);    // zero-copy, checksum-verified
    ReadOnlySequence<byte> bytes = record.Sequence;

    // Vectors: cast mapped pages to floats in place (no heap copy).
    if (record.IsSingleSegment)
    {
        ReadOnlySpan<float> vec = MemoryMarshal.Cast<byte, float>(record.FirstSpan);
        // … TensorPrimitives.CosineSimilarity(vec, query) …
    }
}
```

Swap the read strategy without touching the rest of your code:

```csharp
// Pooled RandomAccess reads instead of mmap (async-friendly, good for large sequential scans):
using var artifact = Artifact.Open("corpus.ctg",
    new ArtifactOpenOptions { ChunkSource = ChunkSourceKind.RandomAccess });
```

### Bring your own storage

`IChunkSource` is a supported extension point, so an artifact does not have to live on a local
disk. Implement it over HTTP range requests, S3 or Azure Blob, an encrypted container, or an
in-memory buffer, and hand the instance to `Artifact.Open`:

```csharp
sealed class MyChunkSource : IChunkSource
{
    public long Length => /* total artifact size, fixed for the source's lifetime */;

    public ChunkLease Read(long offset, int length)
    {
        // Fetch exactly [offset, offset + length) — short reads are a contract violation.
        // `owner` is disposed with the lease; pass null when the memory needs no cleanup.
        return new ChunkLease(sequence, owner);
    }

    public ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken ct = default) => /* … */;
    public void Dispose() { }
}

// ownsSource: false keeps the source alive after the artifact is disposed.
using var source = new MyChunkSource(/* … */);
using Artifact artifact = await Artifact.OpenAsync(source, ownsSource: false);
```

Opening stays O(1): only the header, the manifest, and each segment's record directory are read —
never the payload. So a 200 GB artifact behind a range-capable HTTP server opens in a handful of
small requests, and records fault in on demand. Cartograph ships no transport code and takes no
dependency on any client library; the contract implementations must honour (exact-length reads,
absolute offsets, stable length, thread safety, lease lifetime) is documented on `IChunkSource`.

Structural validation and per-record checksums still apply to custom sources, so a buggy or hostile
source produces a clean `CartographFormatException` rather than silent corruption.

## Architecture

Cartograph is layered so the substrate is usable on its own and the format builds on top of it.

```mermaid
flowchart TD
    A["Your app: RAG, logs, columns, documents"] --> F
    F["Cartograph.Format: header, manifest, segments, records, checksums"] --> S
    S["Cartograph substrate: mapped views, leases, arbitrary-length ReadOnlySequence"] --> OS
    OS["OS: mmap + page cache"]
```

### `Cartograph` — the mapped-memory substrate (Layer 0/1)

Exposes arbitrary-length memory-mapped files as lifetime-safe `Memory<byte>` /
`ReadOnlySequence<byte>` with no managed copies.

- **`MappedMemoryManager`** — a `MemoryManager<byte>` over a mapped view (adds `PointerOffset` to the
  acquired base pointer; `Pin` takes no GC handle since mapped memory is address-stable).
- **`ViewLease`** — ref-counted lease guarding a view. Unmapping a view while spans are live is a
  *segfault, not an exception*; a view is unmapped only when the last lease is released, and
  use-after-dispose throws `ObjectDisposedException`.
- **`MappedSegment`** — one mapped window, aligned to the OS allocation granularity (64 KiB on
  Windows, distinct from the 4 KiB page size).
- **`MappedSequenceSegment` / `MappedSequence`** — stitch multiple windows into one
  `ReadOnlySequence<byte>`. Because `Span`/`Memory` lengths are `int`, files above ~2 GiB *require*
  multiple views; this segmented sequence is the piece that does not otherwise exist in .NET.
- **`IChunkSource`** — pluggable reads, with `MappedChunkSource` (mmap) and
  `RandomAccessChunkSource` (pooled `System.IO.RandomAccess`) shipped so the two can be benchmarked
  head-to-head. It is also a public extension point: implement it to back an artifact with any
  store — HTTP range requests, object storage, an encrypted container — and pass it to
  `Artifact.Open` / `Artifact.OpenAsync`.
- **`NativePrefetch`** — optional `PrefetchVirtualMemory` (Windows) / `madvise(MADV_WILLNEED)`
  (Linux) helpers to avoid unpredictable mid-query page-fault stalls.

### `Cartograph.Format` — the artifact format

What makes "save it and reload it later" work.

- **Header** with magic `CTGH`, version, endianness marker, and a header checksum — unrecognized
  files are refused, never read as garbage.
- **Relative offsets only.** The base address differs on every map, so absolute pointers are never
  stored.
- **Explicit alignment.** Segments, directories, and record payloads are padded to 64 bytes so
  `MemoryMarshal.Cast<byte, float>` and SIMD loads are always legal.
- **Append-only immutable segments + an atomic manifest** — writers append new segments then swap
  the manifest; readers holding leases finish on old segments before those are unmapped. This gives
  crash consistency without a WAL (modeled on Lucene's `MMapDirectory` and Tantivy's immutable
  segments).
- **Per-record checksums** (XxHash3) and **bounds validation of every offset** against segment
  length — a truncated or malformed artifact produces a clean `CartographFormatException`, never an
  out-of-bounds read.

### Writing artifacts larger than memory

Records can be appended two ways:

```csharp
SegmentedArtifactWriter writer = new();
SegmentBuilder segment = writer.AddSegment();

segment.AddRecord(bytes);                          // buffered: held on the managed heap until Save
segment.AddFileRecord(path);                       // streamed: read during Save, never buffered
segment.AddFileRecord(path, offset, length);       // streamed: one byte range of a file
```

`AddFileRecord` records only the path, offset, and length. The bytes are read during `Save` through
a single pooled 1 MiB buffer, so **packing cost is independent of total payload size** — an artifact
covering hundreds of gigabytes is written with the same steady-state memory as one covering a few
kilobytes.

This works because the layout is derived from record *lengths* alone, which are known up front.

**Segment region order.** A record's checksum lives in the segment's record directory. For buffered
records the bytes are already in hand, so the directory is written first. A streamed record has no
cheap checksum, so segments containing one are written **payload-first** — payload region, then
directory — which lets a single pass both emit and checksum every record. Reading a streamed file
twice would otherwise double the I/O. Both orders are legal: `DirectoryOffset` and `PayloadOffset`
are independent 64-bit fields in the segment descriptor, and readers follow them rather than
assuming an order. `ArtifactSegment.IsPayloadFirst` reports which layout a segment uses.

**Size limits.** Offsets and lengths are 64-bit on disk, so an artifact has no practical size cap.
A *single record* is capped at `ArtifactFormat.MaxRecordLength` (`int.MaxValue`, ~2.1 GB) because a
record is surfaced as one `ChunkLease` from `IChunkSource.Read(long offset, int length)`, whose
length is a 32-bit `int`. Inputs larger than that are split across consecutive records and presented
by the application as one logical object — see the harness for that pattern. Exceeding the cap
throws at `AddFileRecord`, not at open time.

## Non-goals

Cartograph is a substrate and a file format. It is explicitly **not**:

- **An ANN / vector-index engine.** No HNSW, no IVF. That space is well served by
  **Faiss**, **USearch**, and **sqlite-vec**; building another is out of scope.
- **A database.** No query engine, transactions, or secondary indexes.
- **A serialization framework.** Records are opaque bytes; you choose their meaning.
- **A server or transport.** Cartograph reads artifacts; it does not serve them. An artifact is an
  immutable file, so any range-capable HTTP server, object store, or CDN already serves it correctly
  — and immutability makes `ETag` / `Cache-Control: immutable` trivially safe. Remote *reading* is
  supported through a custom `IChunkSource`.

RAG over corpora larger than RAM is the first intended *demo*, not the definition of the library.

## Requirements

- .NET 10 SDK (targets `net10.0`).

## Layout

```
Cartograph.sln
src/Cartograph/               the mapped-memory substrate (Layer 0/1)
src/Cartograph.Format/        artifact format: header, manifest, segments, records
tests/Cartograph.Tests/       xUnit tests
bench/Cartograph.Benchmarks/  BenchmarkDotNet harness (see its README)
harnesses/Cartograph.Harness/ end-to-end console harness (see its README)
```

## Try it end to end

`harnesses/Cartograph.Harness` packs a folder tree into an artifact, closes it, reopens it from
nothing but a path, and verifies every record against the files still on disk. It needs no
arguments to do something useful:

```bash
dotnet run --project harnesses/Cartograph.Harness -c Release -- demo --list
```

Or point it at a real folder:

```bash
dotnet run --project harnesses/Cartograph.Harness -c Release -- roundtrip ./src --out src.ctg
```

It is also the shortest tour of the API: the harness writes its own catalog as record `0`, so
reopening recovers the whole directory structure in a single read and then addresses any file
directly by index.

## License

MIT © Angel Hernandez
