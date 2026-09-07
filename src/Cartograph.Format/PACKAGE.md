# Cartograph.Format

**A self-describing artifact container that opens without reading its payload, and streams records without copying them.**

Cartograph.Format defines the `.ctg` artifact: a 64-byte header, an append-only segment manifest,
and per-record directories carrying XxHash3 checksums. Opening one maps the file and validates the
header and manifest, then each live segment's record directory — it never reads the payload. Open
cost therefore tracks the number of *records*, not the number of bytes. Measured on a 51.26 GiB
artifact holding 263 records: opening reads 6,584 bytes, 0.000012% of the file.

Records come back as `ReadOnlySequence<byte>` over the mapped pages, so reading a vector, a document
or a column chunk costs no managed allocation proportional to its size.

> Independent open-source project; not affiliated with or endorsed by Microsoft.

## Install

```
dotnet add package Cartograph.Format
```

## Quick start

```csharp
using System.Buffers;
using System.Runtime.InteropServices;
using Cartograph.Format;

// --- Build once ---
var writer = new SegmentedArtifactWriter();
SegmentBuilder segment = writer.AddSegment();

float[] embedding = GetEmbedding();                   // e.g. 768 dims
segment.AddRecord(MemoryMarshal.AsBytes<float>(embedding));
segment.AddRecord("a log line, a column chunk, a document…"u8);

writer.Save("corpus.ctg");

// --- Map instantly ---
using Artifact artifact = Artifact.Open("corpus.ctg");   // mmap + validate, no payload read

ArtifactSegment records = artifact.Segments[0];
for (int i = 0; i < records.RecordCount; i++) {
    using RecordLease record = records.ReadRecord(i);     // zero-copy, checksum-verified
    ReadOnlySequence<byte> bytes = record.Sequence;

    // Vectors: cast mapped pages to floats in place, no heap copy.
    if (record.IsSingleSegment) {
        ReadOnlySpan<float> vec = MemoryMarshal.Cast<byte, float>(record.FirstSpan);
        // … TensorPrimitives.CosineSimilarity(vec, query) …
    }
}
```

## Choosing how it reads

The read strategy is a single option; nothing else in your code changes.

```csharp
// Pooled RandomAccess reads instead of mmap — async-friendly, good for large sequential scans.
using var artifact = Artifact.Open("corpus.ctg",
    new ArtifactOpenOptions { ChunkSource = ChunkSourceKind.RandomAccess });
```

| Option | Default | Effect |
| ------ | ------- | ------ |
| `ChunkSource` | `Mapped` | `Mapped` for zero-copy random access, `RandomAccess` for pooled sequential reads |
| `WindowSize` | 256 MiB | Size of each mapping window, rounded up to the OS allocation granularity |
| `VerifyChecksums` | `true` | Validate each record's XxHash3 as it is read |

## Writing more than fits in memory

`SegmentedArtifactWriter` buffers records and lays them out on `Save`. When the payload is larger
than memory, `StreamingArtifactWriter` writes records straight through as they arrive, so packing
memory stays flat:

```csharp
using var writer = new StreamingArtifactWriter("huge.ctg");

using (StreamingSegment segment = writer.BeginSegment()) {
    foreach (var doc in EnumerateDocuments()) {
        segment.AppendRecord(doc.Bytes);        // or AppendRecord(stream) — length need not be known
    }
}                                               // disposing the segment publishes it

writer.Complete();                              // required: writes the manifest and patches the header
```

`Complete()` is not optional. A writer disposed without it leaves a zeroed placeholder header, so a
crashed ingest is deliberately not mistaken for a finished artifact. One segment is open at a time,
and the destination must be seekable.

## Integrity

Every record carries an XxHash3 checksum, and the header carries its own. A truncated, torn or
corrupted artifact fails at `Open` — or at the specific record — with `CartographFormatException`,
rather than handing back silently wrong bytes. Bounds are validated against the mapping before any
pointer arithmetic happens.

## Requirements

* .NET 10 or later
* Windows, Linux or macOS

## The Cartograph stack

This package sits in the **middle** layer.

```
Cartograph                  the memory-mapping substrate
  └── Cartograph.Format     ← you are here
        └── Cartograph.Catalog   pack a folder of files, extract them back
```

**Dependencies pulled in automatically:**

| Package | Version | Why |
| ------- | ------- | --- |
| [`Cartograph`](https://www.nuget.org/packages/Cartograph) | `0.1.0-alpha` | Memory-mapped windows, slices and leases |
| `System.IO.Hashing` | `9.0.0` | XxHash3 for header and per-record checksums |

| Package | Adds | Depends on |
| ------- | ---- | ---------- |
| [`Cartograph`](https://www.nuget.org/packages/Cartograph) | Memory-mapped windows, slices, leases | *nothing* |
| [`Cartograph.Format`](https://www.nuget.org/packages/Cartograph.Format) | Artifact container, records, integrity | `Cartograph`, `System.IO.Hashing` |
| [`Cartograph.Catalog`](https://www.nuget.org/packages/Cartograph.Catalog) | File catalog, extract, verify | `Cartograph.Format`, `Cartograph`, `System.IO.Hashing` |

All three are versioned and released together.

## Documentation

Full documentation, format specification and benchmarks live in the repository:
[https://github.com/angelhernandezm/Cartograph](https://github.com/angelhernandezm/Cartograph)

## License

MIT © Angel Hernandez
