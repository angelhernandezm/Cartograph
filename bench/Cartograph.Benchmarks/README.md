# Cartograph Benchmarks

BenchmarkDotNet harness comparing the two built-in Cartograph chunk sources (memory-mapped vs
pooled `RandomAccess`) against naive managed baselines. A third kind, `ChunkSourceKind.Custom`,
covers caller-supplied `IChunkSource` implementations and is not benchmarked here because its cost
is defined entirely by the backing store.

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
  whole file.

> **Read that contrast as a floor, not as the pitch.** `File.ReadAllBytes` is not what a competent
> engineer writes for a large file, so beating it proves little on its own. The fair opponent is a
> hand-rolled flat container — payloads written contiguously, an index at the tail, read with
> `RandomAccess.Read` into a reused buffer — which is exactly what `harnesses/Cartograph.Baseline`
> implements as its `naive-stream` mode. Measured, that opponent opens a 600 MiB container in
> **7.7 ms against the mapped reader's 17.6 ms** — it wins. Open time is not where Cartograph
> separates; read allocation is (**6.9 KB vs ~268 MiB** to verify the same 600 MiB). Quote that.

Note also that open reads each live segment's record directory (24 bytes per record), so open cost
is constant in *payload* size and linear in *record count*. The sweep above varies file size at a
fixed record size, which is the intended comparison; a sweep that varies record count would not be
flat, and should not be expected to be.

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

- **No BenchmarkDotNet output is committed here.** Everything above describes what this suite is
  built to measure, not a published result. Run it yourself and compare shapes.
- **Measured A/B numbers do exist**, from the harness pair rather than from this suite: see
  `harnesses/Cartograph.Baseline/README.md` for pack/open/verify times, allocation and peak working
  set across Cartograph and five plain-.NET containers, on a 24.54 MiB and a 600 MiB tree. Read its
  conclusions before quoting anything from here — notably that a hand-rolled index-only reader opens
  *faster* than `Artifact.Open`, and that the real separation is in read allocation, not open time.
- Benchmarks generate temporary artifacts under the OS temp directory during `[GlobalSetup]` and
  delete them in `[GlobalCleanup]`.
- Numbers are hardware-, OS-, and page-cache-dependent. For the open-time result, compare the
  *shape* across sizes (flat vs linear) rather than absolute nanoseconds.
