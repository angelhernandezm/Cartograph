# Cartograph

**Opening costs the same whether the file is 2 MB or 50 GB — and reading it copies nothing.**

Cartograph is the memory-mapped substrate underneath the Cartograph artifact stack. It maps a file
into memory in fixed-size windows and hands you records as `ReadOnlySequence<byte>` pointing
straight at the mapped pages — no deserialization pass, no managed buffer proportional to your
payload, and no read amplification for the parts you never touch.

Opening is `mmap` plus validation, so its cost tracks the number of *records*, not the number of
bytes. Measured on a 51.26 GiB artifact (263 records): opening reads a 64-byte header, a 208-byte
manifest and 6,312 bytes of record directory — **6,584 bytes in total, 0.000012% of the file**. The
other 55 GB is never touched until something asks for it.

Reading is where the second difference shows: verifying a 600 MiB artifact allocates **6.9 KB** of
managed memory, against roughly 268 MiB for a hand-rolled flat file doing identical work.

Because the pages are shared by the operating system, N processes reading the same file cost one
physical copy, not N.

> Independent open-source project; not affiliated with or endorsed by Microsoft.

## Install

```
dotnet add package Cartograph
```

## Quick start

```csharp
using System.Buffers;
using Cartograph;

// Map the file. Constant time: nothing is read until you touch it.
using MappedFile file = MappedFile.OpenRead("corpus.bin");

Console.WriteLine($"{file.Length} bytes across {file.WindowCount} window(s)");

// Slice any region. The bytes are the mapped pages themselves, not a copy.
using MappedSlice slice = file.Slice(offset: 0, length: 4096);
ReadOnlySequence<byte> bytes = slice.Sequence;

foreach (ReadOnlyMemory<byte> chunk in bytes) {
    Consume(chunk.Span);
}
```

Prefer pooled `RandomAccess` reads over mapping — better for long sequential scans, and
async-friendly — behind the same interface:

```csharp
using IChunkSource source = RandomAccessChunkSource.Open("corpus.bin");
using ChunkLease lease = source.Read(offset: 0, length: 4096);

ReadOnlySequence<byte> data = lease.Sequence;
```

Swap `RandomAccessChunkSource.Open` for `MappedChunkSource.Open` and nothing downstream changes —
that is the point of the interface. Which one wins depends on your access pattern, so measure it.

## What you get

| Type | Purpose |
| ---- | ------- |
| `MappedFile` | Maps a file as fixed-size windows; slice any byte range on demand |
| `MappedSlice` | A `ReadOnlySequence<byte>` over mapped pages, valid until disposed |
| `MappedSegment` | A single mapped window, leasable so it stays resident while in use |
| `IChunkSource` | Extension point — map, pool, or bring your own storage |
| `ChunkLease` | Keeps the backing memory alive for the lifetime of a read |
| `NativePrefetch` | Advises the OS to fault pages in ahead of use |

Anything past the end of a mapping is rejected before it reaches native code, so a malformed offset
raises an exception instead of reading unrelated memory.

## Lifetime rule

A slice, lease or segment is valid **only until it is disposed**. The memory it exposes belongs to
the mapping, not to the GC, so reading a `ReadOnlySequence<byte>` after disposal is undefined
behaviour. Keep the `using` in scope for as long as you touch the bytes.

## Requirements

* .NET 10 or later
* Windows, Linux or macOS — the mapping layer is portable, with platform-specific prefetch hints
  applied only where available

## The Cartograph stack

This package is the **core** and has **no dependencies** — it is the substrate the others build on.

```
Cartograph                  ← you are here (no dependencies)
  └── Cartograph.Format     the .ctg container: header, segments, checksums
        └── Cartograph.Catalog   pack a folder of files, extract them back
```

Install a higher layer and this one comes with it automatically; you never need to reference it
explicitly unless you are building your own format directly on the mapping primitives.

| Package | Adds | Depends on |
| ------- | ---- | ---------- |
| [`Cartograph`](https://www.nuget.org/packages/Cartograph) | Memory-mapped windows, slices, leases | *nothing* |
| [`Cartograph.Format`](https://www.nuget.org/packages/Cartograph.Format) | Artifact container, records, integrity | `Cartograph`, `System.IO.Hashing` |
| [`Cartograph.Catalog`](https://www.nuget.org/packages/Cartograph.Catalog) | File catalog, extract, verify | `Cartograph.Format`, `Cartograph`, `System.IO.Hashing` |

All three are versioned and released together.

## Documentation

Full documentation, benchmarks and design notes live in the repository:
[https://github.com/angelhernandezm/Cartograph](https://github.com/angelhernandezm/Cartograph)

## License

MIT © Angel Hernandez
