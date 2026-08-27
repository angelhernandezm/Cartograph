# Cartograph Benchmarks

BenchmarkDotNet harness comparing the two Cartograph chunk sources (memory-mapped vs pooled
`RandomAccess`) against naive managed baselines.

## Running

```bash
dotnet run -c Release --project bench/Cartograph.Benchmarks -- --filter *
```

Filter to a single suite, e.g.:

```bash
dotnet run -c Release --project bench/Cartograph.Benchmarks -- --filter *OpenTime*
```

All benchmarks use `[MemoryDiagnoser]`; report allocations, Gen2 counts, and working set from the
BenchmarkDotNet output.

## What each benchmark is meant to prove

### `OpenTimeBenchmarks` — the headline result
Open time as a function of file size (8 / 64 / 256 MiB).

- **`Open_Mapped`** should be **flat** across sizes: opening is `mmap` + header/manifest validation,
  which is O(1) regardless of payload size. No payload is touched until a page is read.
- **`Open_RandomAccess`** is also flat: it reads only the header and manifest.
- **`Baseline_ReadAllBytes`** is **linear** in file size and allocates a buffer proportional to the
  whole file. This flat-vs-linear contrast is the point of the library.

### `ScanBenchmarks` — sequential and random access
- **Sequential full scan** — pooled `RandomAccess` is often competitive or better here and is
  async-friendly.
- **Random record access** — `mmap` tends to win once the page cache is hot: no per-read syscall and
  no copy into the managed heap.

### `CosineSimilarityBenchmarks` — brute-force vector scan
`TensorPrimitives.CosineSimilarity` over `MemoryMarshal.Cast<byte, float>` of mapped pages.

- **`Mapped_CastInPlace`** casts mapped bytes to `float` spans in place — no copy into the managed
  heap, zero steady-state managed allocation proportional to the corpus.
- **`Managed_HeapVectors`** holds every vector as a `float[]` in memory; the allocation and
  working-set columns show the difference.

> This is a brute-force scan, deliberately **not** an ANN index. Cartograph is the substrate and the
> format; approximate nearest-neighbour search is explicitly out of scope (see the root README).

## Notes

- Benchmarks generate temporary artifacts under the OS temp directory during `[GlobalSetup]` and
  delete them in `[GlobalCleanup]`.
- Numbers are hardware-, OS-, and page-cache-dependent. For the open-time result, compare the
  *shape* across sizes (flat vs linear) rather than absolute nanoseconds.
