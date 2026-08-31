# Cartograph Baseline

A console application that solves exactly the same problem as the [Cartograph
harness](../Cartograph.Harness/README.md) — pack a folder tree into a container, close it, reopen it
from nothing but a path, and prove every byte survived — but using **only .NET primitives**. It has
**no reference to Cartograph at all**. That is the whole point: it is the control group. Run the same
tree through both programs, read the CSV each emits, and the difference is attributable to the
artifact format rather than to the machine, the framework version or the JIT.

To keep the comparison honest the two programs share **no assembly**: `RunMetrics.cs`, `DemoTree.cs`
and `ConsoleReport.cs` are duplicated verbatim (only the namespace differs) so the baseline shares no
JIT behaviour or allocation profile with the library under test.

## Quick start

Generate a synthetic tree, pack it with the streaming flat container, read it back and clean up:

```bash
dotnet run --project harnesses/Cartograph.Baseline -c Release -- demo --container naive-stream --list
```

Profile every container mode over an identical seeded tree and collect the CSV:

```bash
for m in zip-store zip-deflate naive naive-stream loose; do
  dotnet run --project harnesses/Cartograph.Baseline -c Release -- demo --demo-files 2000 --container $m --csv --quiet
done
```

## Container modes

`--container` selects which plain-.NET storage strategy to profile. They are ordered from the most
idiomatic to the most carefully optimized.

| Mode | What it does | Honest role in the comparison |
|---|---|---|
| `zip-store` | `ZipArchive` with `CompressionLevel.NoCompression` | The **closest apples-to-apples** comparison to an uncompressed Cartograph artifact: same bytes on disk, but the ZIP central directory is read and each entry is copied out of the archive stream rather than mapped in place |
| `zip-deflate` | `ZipArchive` with `CompressionLevel.Optimal` | Trades CPU for a much smaller container. Cartograph does not compress at all, so this shows what compression buys and what it costs |
| `naive` | Flat file, whole thing read into one `byte[]` at open | The `File.ReadAllBytes` anti-pattern: open is O(size) and lands the entire container on the managed heap |
| `naive-stream` | Flat file, index only at open, `RandomAccess.Read` per record into a reused buffer | The best plain .NET can do, and the fairest hand-rolled rival to a mapped read |
| `loose` | No container at all; files read in place with `File.ReadAllBytes` | The two extremes at once: nothing to pack (wins pack), a fresh allocation per read (loses reads) |

The `naive`/`naive-stream` flat format (`.nbc`) is the honest hand-rolled equivalent of a Cartograph
artifact: a magic number, an index offset, the payloads written contiguously, and the index at the
tail. No alignment, no per-record checksum in the container — a developer writing this by hand would
be unlikely to add either. Because `zip` central directories carry only a CRC-32, the XxHash3 computed
at pack time is stashed in each entry's comment so verification stays identical across every mode.
`loose` stores nothing, so it has no checksum to compare against and verification falls back to a
length check (the payload is still hashed, so the CPU cost stays comparable).

## Commands

| Command | Description |
|---|---|
| `demo` | Generate a synthetic tree, then round-trip it |
| `pack <folder>` | Recursively read the folder and write a container |
| `load <container>` | Open, report on and verify a container |
| `roundtrip <folder>` | `pack`, then `load`, plus a byte-for-byte comparison against the files still on disk |

The verb is optional: a folder implies `roundtrip`, a file implies `load`.

### Packing options

| Option | Description |
|---|---|
| `--container <mode>` | `zip-store`, `zip-deflate`, `naive`, `naive-stream` (default) or `loose` |
| `-o`, `--out <path>` | Container to write. Defaults to `<folder-name><ext>` |
| `--include <pattern>` | Only pack matching relative paths. Repeatable, supports `*` and `?` |
| `--exclude <pattern>` | Skip matching relative paths. Repeatable, applied after `--include` |
| `--max-file-size <size>` | Skip files larger than this. Default unlimited |
| `--max-total <size>` | Stop once this many payload bytes are packed. Default unlimited |
| `--max-files <n>` | Stop after this many files. Default `200000` |
| `--follow-links` | Traverse symlinks and junctions. Off by default, to avoid cycles |

### Loading options

| Option | Description |
|---|---|
| `--list` | Print the container listing |
| `--top <n>` | Rows to print with `--list`. Default `20` |
| `--extract <dir>` | Write every stored file into a directory |

### General

| Option | Description |
|---|---|
| `--csv` | Emit a machine readable CSV summary line per phase (identical columns to the harness) |
| `-q`, `--quiet` | Only print warnings, errors and the summary |
| `--demo-files <n>` | Files the `demo` command generates. Default `120` |
| `--keep` | Keep the generated tree and container |

Sizes accept a plain byte count or a `K` / `M` / `G` suffix.

## Exit codes

Identical to the harness, so the two are interchangeable in a script:

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Bad command line |
| `2` | Runtime failure — unreadable folder, corrupt container |
| `3` | Verification failure — a stored file did not match its checksum or length |

## CSV format

Both programs print the same columns, so runs can be concatenated straight into one table:

```
CSV,label,phase,elapsed_ms,allocated_bytes,payload_bytes,gen0,gen1,gen2,peak_ws_bytes
```

The `label` distinguishes them (`baseline-naive-stream`, `cartograph-mapped`, …) and `phase` is one
of `pack`, `open` or `verify`.

## Profiling results

All figures below were collected on this repository (Release, .NET 10, Windows) with:

```bash
# small tree: 2000 generated files, 24.54 MiB payload
<app> demo --demo-files 2000 --container <mode> --csv --quiet

# large tree: three 200 MiB files, 600 MiB payload
<app> roundtrip <dir> --container <mode> --csv --quiet
```

The demo tree is seeded identically in both programs, so both pack a byte-for-byte identical tree.
Numbers are a single representative run; expect a few percent of run-to-run noise. `alloc/payload` is
managed bytes allocated divided by payload bytes — the lower, the less the phase copied onto the heap.

### Small tree — 2000 files, 24.54 MiB payload

| Mode | Container size | Pack ms | Open ms | Open alloc | Verify ms | Verify alloc | alloc/payload (verify) |
|---|---:|---:|---:|---:|---:|---:|---:|
| **cartograph-mapped** | 24.79 MiB | 418 | 10.6 | 68 KB | 11.1 | 1.77 MiB | **0.07×** |
| cartograph-randomaccess | 24.79 MiB | 490 | 4.2 | 89 KB | 10.8 | 3.76 MiB | 0.15× |
| baseline-naive | 24.65 MiB | 441 | 9.3 | **26.1 MiB** | 2.7 | 1.14 MiB | 0.04× |
| baseline-naive-stream | 24.65 MiB | 449 | 3.7 | 284 KB | 16.1 | 3.47 MiB | 0.14× |
| baseline-zip-store | 24.81 MiB | 512 | 2.5 | 1.78 MiB | 25.0 | 3.60 MiB | 0.14× |
| baseline-zip-deflate | **15.46 MiB** | 912 | 3.4 | 1.78 MiB | 33.2 | 4.03 MiB | 0.16× |
| baseline-loose | **0 B** | **0.25** | 58.3 | 1.07 MiB | 439.8 | **27.65 MiB** | **1.07×** |

At this size the open and verify *times* are in the single-digit to tens-of-milliseconds range and
are **JIT-dominated, not size-dominated** — do not read too much into them. The *allocation* columns
are already telling the real story, though: `loose` allocates a full copy of the payload (1.07×) and
is the only mode that triggered GCs, while `cartograph-mapped` reads the whole payload for 0.07×.

### Large tree — three 200 MiB files, 600 MiB payload

This is where the differences become real rather than JIT noise.

| Mode | Pack ms | Open ms | Open alloc | Verify ms | Verify alloc | Peak WS |
|---|---:|---:|---:|---:|---:|---:|
| **cartograph-mapped** | 1040 | 17.6 | **8.5 KB** | 563 | **6.9 KB** | 656 MiB |
| cartograph-randomaccess | 1011 | 11.1 | 20 KB | 346 | 268 MiB | 236 MiB |
| baseline-naive | 1045 | **421.9** | **629 MiB** | 60 | 4.9 KB | 655 MiB |
| baseline-naive-stream | 1014 | 7.7 | 8.3 KB | 360 | 268 MiB | 235 MiB |
| baseline-zip-store | 1159 | 6.3 | 33 KB | 451 | 268 MiB | 235 MiB |
| baseline-loose | **0.5** | 2.0 | 5.2 KB | 782 | **629 MiB** | 652 MiB |

## Conclusions — honestly

- **Cartograph's win is zero-copy reads, not O(1) open and not pack throughput.** On the 600 MiB tree
  the mapped reader allocates **6.9 KB to read 600 MiB** (verify). `loose` allocates the whole 629 MiB
  (one fresh array per `File.ReadAllBytes`) and `naive-stream`, `zip-store` and Cartograph's own
  `randomaccess` strategy each allocate ~256 MiB (the reused scratch/piece buffer). That order of
  magnitude is the entire value proposition, and it only becomes obvious at scale.

  Be precise about the O(1) part: **a hand-rolled index-only reader also opens in O(1), and opens
  slightly faster.** `naive-stream` opens in 7.7 ms against the mapped reader's 17.6 ms, because it
  reads a small index and stops, while `Artifact.Open` additionally parses a header, a manifest, and a
  per-record directory, and validates every offset. O(1) open is not the moat — any careful developer
  reaches it in an afternoon. What they do not get for free is reading a record without copying it.

- **`loose` wins pack, easily, because it does almost nothing.** 0.5 ms versus ~1 s for everyone
  else — it writes no container at all. It then loses the read race just as decisively (782 ms, a
  full-payload allocation, and the only GC pressure in the large run). If your access pattern is
  "read every file once, immediately after producing it," loose is genuinely hard to beat and
  Cartograph buys you nothing. Say so.

- **Cartograph does not pack faster than a hand-rolled flat file.** Pack times are within noise of
  `naive`/`naive-stream` (~1 s for 600 MiB); both are bounded by reading the source and writing the
  bytes. Cartograph is not magic on the write path, and this harness does not pretend it is.

- **`zip-deflate` produces a much smaller container** (15.46 MiB vs 24.5–24.8 MiB), because Cartograph
  does not compress at all. Note the demo tree is ~⅓ incompressible random `.bin`, so on
  text-dominated data Deflate would win by more. Compression costs CPU on both write (912 ms vs 512 ms
  for `zip-store`) and read. If disk footprint matters more than mapped random access, ZIP is the
  right tool and Cartograph is the wrong one.

- **`naive` (whole-file `File.ReadAllBytes`) is the anti-pattern the mapped reader exists to avoid.**
  Its *open* cost scales with the artifact: **421 ms and a 629 MiB managed allocation** to open the
  600 MiB container, versus ~8 KB and flat time for every index-only mode. It "wins" verify (60 ms)
  only because it already paid for the entire payload up front and holds it resident. Total memory is
  the whole file, in private managed heap, for the reader's lifetime.

- **Cartograph's `randomaccess` strategy is essentially the hand-rolled `naive-stream`.** They post
  near-identical numbers (346 vs 360 ms, both ~268 MiB allocated) because they do the same thing: read
  the index, then `RandomAccess.Read` each record into a reused buffer. The mapped strategy is what
  pulls ahead; `randomaccess` is the honest floor that any careful developer could reach without a
  library.

- **Peak working set needs a caveat.** The mapped verify shows a 656 MiB peak working set even though
  it allocated ~7 KB of managed memory. That is the OS mapping the file's pages into the process
  (shared, reclaimable page cache) — **not** private managed heap. `naive` and `loose` reach a similar
  peak, but theirs is private, collectable-only managed memory. Working set alone would flatter the
  copying strategies; the `allocated_bytes` column is what separates a zero-copy read from a copy.

- **Method note: no warm-up was used.** Instead of warming the JIT, the large-file tree was made big
  enough that the size-dependent differences (naive's O(size) open, the ~256 MiB vs ~7 KB verify
  allocation) dwarf JIT startup. The small-tree open/verify *times* remain JIT-dominated and are
  reported only for their allocation columns, not their millisecond columns.
