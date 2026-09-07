# Cartograph.Catalog

**Pack a folder of files into a single artifact, then read any one of them back without unpacking.**

Cartograph.Catalog adds a self-describing file catalog to a Cartograph artifact. The catalog lives as
record zero and maps each packed file's path, size, timestamp and checksum back to the segment and
records holding its bytes — so you can list the contents, extract one file, or verify integrity
without scanning or decompressing anything.

Unlike a zip, reading one file out of a 50 GB artifact touches only that file's pages, and several
processes reading the same artifact share one physical copy.

> Independent open-source project; not affiliated with or endorsed by Microsoft.

## Install

```
dotnet add package Cartograph.Catalog
```

## Quick start

```csharp
using Cartograph.Catalog;

using CatalogedArtifact artifact = CatalogedArtifact.Open("corpus.ctg");

Console.WriteLine($"{artifact.Entries.Count} files, {artifact.SizeOnDisk:N0} bytes on disk");

// List what's inside — metadata only, no payload is read.
foreach (CatalogEntry entry in artifact.Entries) {
    Console.WriteLine($"{entry.RelativePath,-50} {entry.Length,12:N0}  {entry.LastWriteUtc:u}");
}

// Pull one file out by name.
CatalogEntry? found = artifact.Find("docs/readme.txt");
if (found is not null) {
    artifact.ExtractTo(found, @"C:\out\readme.txt");

    // …or peek at the first few KB without materialising the whole thing.
    byte[] head = artifact.ReadPrefix(found, maxBytes: 4096);
    Console.WriteLine(Encoding.UTF8.GetString(head));
}
```

## Streaming a file without buffering it

`ForEachChunk` hands you the mapped pages as they are, so a multi-gigabyte entry never becomes a
multi-gigabyte array:

```csharp
artifact.ForEachChunk(entry, (span, index) => hasher.Append(span));

// Or copy straight into any stream.
using FileStream destination = File.Create("payload.bin");
long written = artifact.CopyTo(entry, destination);
```

## Verifying integrity

Each entry records the XxHash3 checksum captured when it was packed:

```csharp
CatalogVerification result = artifact.Verify(entry);

if (!result.HasExpectedChecksum) {
    Console.WriteLine("No checksum was recorded for this entry.");
}
else if (!result.Matches) {
    Console.WriteLine("Content does not match the recorded checksum.");
}
```

A checksum of `0` means *never computed*, not *corrupt* — `HasExpectedChecksum` keeps the two cases
apart so an unverifiable entry is never mistaken for a damaged one.

## What an entry tells you

| Member | Meaning |
| ------ | ------- |
| `RelativePath`, `Name`, `Directory`, `Extension` | Where the file sat under the packed root |
| `Length` | Original size in bytes |
| `LastWriteUtc` | Timestamp captured at pack time |
| `Checksum` | XxHash3 of the content, or `0` if not computed |
| `SegmentIndex`, `RecordIndex`, `RecordCount` | Where the bytes live inside the artifact |

Extraction resolves every destination path and re-checks it against the output root, so a crafted
entry cannot escape the target directory via `..` or an absolute path.

## Requirements

* .NET 10 or later
* Windows, Linux or macOS

## The Cartograph stack

This package is the **top** layer.

```
Cartograph                  the memory-mapping substrate
  └── Cartograph.Format     the .ctg container: header, segments, checksums
        └── Cartograph.Catalog   ← you are here
```

**Dependencies pulled in automatically:**

| Package | Version | Why |
| ------- | ------- | --- |
| [`Cartograph.Format`](https://www.nuget.org/packages/Cartograph.Format) | `0.1.0-alpha` | Artifact container and record access |
| [`Cartograph`](https://www.nuget.org/packages/Cartograph) | `0.1.0-alpha` | Memory-mapped windows, slices and leases |
| `System.IO.Hashing` | `9.0.0` | XxHash3 for entry checksums |

Installing this package alone is enough — the whole stack comes with it.

| Package | Adds | Depends on |
| ------- | ---- | ---------- |
| [`Cartograph`](https://www.nuget.org/packages/Cartograph) | Memory-mapped windows, slices, leases | *nothing* |
| [`Cartograph.Format`](https://www.nuget.org/packages/Cartograph.Format) | Artifact container, records, integrity | `Cartograph`, `System.IO.Hashing` |
| [`Cartograph.Catalog`](https://www.nuget.org/packages/Cartograph.Catalog) | File catalog, extract, verify | `Cartograph.Format`, `Cartograph`, `System.IO.Hashing` |

All three are versioned and released together.

## Documentation

Full documentation, including the packing harness that produces catalogued artifacts, lives in the
repository:
[https://github.com/angelhernandezm/Cartograph](https://github.com/angelhernandezm/Cartograph)

## License

MIT © Angel Hernandez
