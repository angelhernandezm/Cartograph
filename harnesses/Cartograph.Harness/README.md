# Cartograph Harness

A console application that exercises the full lifecycle of a Cartograph artifact: it walks a folder
tree, packs every file into a new artifact, closes it, reopens it from nothing but a path, and
proves that every byte survived.

It is a worked example rather than a product. If you want to know what using Cartograph actually
looks like, read `FolderPacker.cs` and `ArtifactLoader.cs` — between them they touch almost the
entire public surface.

## Quick start

No arguments needed. This generates a synthetic tree, packs it, reads it back and cleans up:

```bash
dotnet run --project harnesses/Cartograph.Harness -c Release -- demo --list
```

Pack a real folder and verify the round trip:

```bash
dotnet run --project harnesses/Cartograph.Harness -c Release -- roundtrip ./src --out src.ctg
```

Read an artifact somebody else produced:

```bash
dotnet run --project harnesses/Cartograph.Harness -c Release -- load src.ctg --list --top 50
```

The verb is optional. A folder implies `roundtrip`, a file implies `load`:

```bash
cartograph-harness ./docs      # roundtrip
cartograph-harness docs.ctg    # load
```

## What the artifact looks like

Cartograph treats records as opaque bytes — it has no notion of a file name, a size or a timestamp.
That metadata has to come from somewhere, so the harness does what a real application would do: it
writes its own catalog into the artifact.

```
segment 0  ->  record 0            the serialized FileCatalog
segment 1  ->  records 1..n        one record per file in the first group
segment 2  ->  records n+1..m      one record per file in the second group
...
```

Because the catalog is always global record `0`, a reader recovers the entire directory structure
with a single `ReadRecord(0)` call, then addresses any file directly by its global index. Nothing
scans the payload.

`--group-by` decides how files are distributed across segments, which matters because a segment is
the unit of checksum and of append-only growth in the format:

| Mode | Segments |
|---|---|
| `ext` (default) | One per distinct file extension |
| `dir` | One per top-level directory under the root |
| `flat` | A single segment for everything |

## Commands

| Command | Description |
|---|---|
| `demo` | Generate a synthetic tree, then round-trip it |
| `pack <folder>` | Recursively read the folder and write an artifact |
| `load <artifact>` | Open, report on and verify an artifact |
| `roundtrip <folder>` | `pack`, then `load`, plus a byte-for-byte comparison against the files still on disk |

### Packing options

| Option | Description |
|---|---|
| `-o`, `--out <path>` | Artifact to write. Defaults to `<folder-name>.ctg` |
| `--group-by <mode>` | `ext` (default), `dir` or `flat` |
| `--include <pattern>` | Only pack matching relative paths. Repeatable, supports `*` and `?` |
| `--exclude <pattern>` | Skip matching relative paths. Repeatable, applied after `--include` |
| `--max-file-size <size>` | Skip files larger than this. Default `64M` |
| `--max-total <size>` | Stop once this many payload bytes are packed. Default `1G` |
| `--max-files <n>` | Stop after this many files. Default `200000` |
| `--follow-links` | Traverse symlinks and junctions. Off by default, to avoid cycles |

### Loading options

| Option | Description |
|---|---|
| `--strategy <kind>` | `mapped` (default) or `randomaccess` |
| `--async` | Use `OpenAsync` and `ReadRecordAsync` throughout |
| `--no-verify` | Skip recomputing record checksums |
| `--no-record-checksums` | Turn off `ArtifactOpenOptions.VerifyChecksums` |
| `--list` | Print the catalog |
| `--top <n>` | Rows to print with `--list`. Default `20` |
| `--extract <dir>` | Write every packed file into a directory |
| `--cat <relative-path>` | Print a single record |

Sizes accept a plain byte count or a `K` / `M` / `G` suffix.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Bad command line |
| `2` | Runtime failure — unreadable folder, corrupt artifact |
| `3` | Verification failure — a record did not match its checksum |

That makes it usable as a smoke test in a script:

```bash
cartograph-harness roundtrip ./data --quiet || echo "artifact is broken"
```

## Notes

- **Packing buffers in managed memory.** `SegmentedArtifactWriter` copies each record onto the heap
  before it writes, so packing is bounded by `--max-total` on purpose. Reading has no such
  limitation — that asymmetry is the point of the library.
- **Files that cannot be read are skipped, not fatal.** Pointing the harness at a live source tree
  will hit locked files; they are reported as warnings and packing continues.
- **Changed files are not mismatches.** During the `roundtrip` comparison, a file that was edited
  after it was packed is reported as skipped. The artifact is immutable by design and is not
  expected to track later edits.
- **`--follow-links` is off by default** so a symlink loop cannot turn the walk into an infinite one.
- **Extraction refuses to escape its destination**, so a hand-edited catalog cannot write outside
  `--extract`.
