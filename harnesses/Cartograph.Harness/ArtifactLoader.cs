// ============================================================================
// Cartograph
// File: ArtifactLoader.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Reopens an artifact produced by the harness, reports on its header, segments
// and catalog, verifies every record and optionally lists, extracts or prints
// the packed files
//
// License: MIT
// ============================================================================
//
// MIT License
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
// ============================================================================

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Cartograph.Format;

namespace Cartograph.Harness;

/// <summary>
/// Summarizes the outcome of a load and verification run
/// </summary>
internal sealed class LoadResult
{
    /// <summary>
    /// Gets the catalog recovered from record zero of the artifact
    /// </summary>
    /// <value>The directory of every file stored in the artifact.</value>
    public required FileCatalog Catalog { get; init; }

    /// <summary>
    /// Gets the time taken to open the artifact
    /// </summary>
    /// <value>
    /// Expected to be effectively constant regardless of artifact size, because opening reads only the
    /// header, the manifest and the per-segment record directories.
    /// </value>
    public required TimeSpan OpenElapsed { get; init; }

    /// <summary>
    /// Gets the time taken to read and verify every record
    /// </summary>
    /// <value><see cref="System.TimeSpan.Zero" /> when verification was disabled.</value>
    public required TimeSpan VerifyElapsed { get; init; }

    /// <summary>
    /// Gets the number of records whose contents matched their catalog checksum
    /// </summary>
    /// <value>Equal to the catalog entry count on a healthy artifact.</value>
    public required int VerifiedRecords { get; init; }

    /// <summary>
    /// Gets the number of records whose contents did not match the catalog
    /// </summary>
    /// <value>Zero on a healthy artifact.</value>
    public required int FailedRecords { get; init; }

    /// <summary>
    /// Gets the total number of payload bytes read during verification
    /// </summary>
    /// <value>Zero when verification was disabled.</value>
    public required long BytesVerified { get; init; }

    /// <summary>
    /// Gets the number of files that were compared against the original tree and matched
    /// </summary>
    /// <value>Zero when no source comparison was requested or possible.</value>
    public int SourceMatches { get; init; }

    /// <summary>
    /// Gets the number of files that were compared against the original tree and differed
    /// </summary>
    /// <value>Zero when the artifact faithfully reproduces the source tree.</value>
    public int SourceMismatches { get; init; }

    /// <summary>
    /// Gets a value indicating whether the run completed without any detected problem
    /// </summary>
    /// <value><see langword="true" /> when no record and no source comparison failed.</value>
    public bool Success => FailedRecords == 0 && SourceMismatches == 0;

    /// <summary>
    /// Gets the resource metrics captured while opening the artifact
    /// </summary>
    /// <value>The open-phase metrics, directly comparable to the .NET baseline's open phase.</value>
    public required RunMetrics OpenMetrics { get; init; }

    /// <summary>
    /// Gets the resource metrics captured while reading and verifying every record
    /// </summary>
    /// <value>
    /// The verify-phase metrics, directly comparable to the .NET baseline's verify phase, or
    /// <see langword="null" /> when verification was disabled with <c>--no-verify</c>.
    /// </value>
    public RunMetrics? VerifyMetrics { get; init; }
}

/// <summary>
/// Reads back an artifact written by <see cref="FolderPacker" />
/// </summary>
/// <remarks>
/// This is the half of the harness that exercises the reading API: constant-time open, catalog
/// recovery from a single record, random access by global index, and optional checksum verification
/// over the whole payload.
/// </remarks>
internal static class ArtifactLoader
{
    /// <summary>
    /// Number of bytes of a record printed by the <c>--cat</c> option before output is truncated
    /// </summary>
    private const int CatPreviewLimit = 8 * 1024;

    /// <summary>
    /// Opens an artifact, reports on it and performs whatever verification was requested
    /// </summary>
    /// <param name="options">Parsed command line controlling verification, listing and extraction</param>
    /// <param name="artifactPath">Fully qualified path of the artifact to open</param>
    /// <param name="compareRoot">
    /// Folder to compare the extracted contents against, or <see langword="null" /> to skip the
    /// comparison
    /// </param>
    /// <returns>A description of what was read and whether it verified</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="artifactPath" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.IO.FileNotFoundException"><paramref name="artifactPath" /> does not exist.</exception>
    public static async Task<LoadResult> LoadAsync(HarnessOptions options, string artifactPath, string? compareRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(artifactPath);

        if (!File.Exists(artifactPath))
        {
            throw new FileNotFoundException($"Artifact '{artifactPath}' does not exist.", artifactPath);
        }

        ArtifactOpenOptions openOptions = new()
        {
            ChunkSource = options.Strategy,
            VerifyChecksums = options.VerifyRecordChecksums,
        };

        long fileBytes = new FileInfo(artifactPath).Length;

        RunMetrics.Scope openScope = RunMetrics.Measure("open");
        Stopwatch openWatch = Stopwatch.StartNew();

        Artifact artifact = options.Async
            ? await Artifact.OpenAsync(artifactPath, openOptions).ConfigureAwait(false)
            : Artifact.Open(artifactPath, openOptions);

        openWatch.Stop();
        RunMetrics openMetrics = openScope.Stop();

        using (artifact)
        {
            ReportHeader(artifact, artifactPath, fileBytes, openWatch.Elapsed);

            FileCatalog catalog = await ReadCatalogAsync(artifact, options.Async).ConfigureAwait(false);

            ReportCatalog(catalog, artifact);
            ReportSegments(artifact, catalog);

            if (options.List)
            {
                ReportListing(catalog, options.Top);
            }

            int verified = 0;
            int failed = 0;
            int withoutCatalogChecksum = 0;
            long bytesVerified = 0;
            TimeSpan verifyElapsed = TimeSpan.Zero;
            RunMetrics? verifyMetrics = null;

            if (options.Verify)
            {
                RunMetrics.Scope verifyScope = RunMetrics.Measure("verify");
                Stopwatch verifyWatch = Stopwatch.StartNew();

                foreach (CatalogEntry entry in catalog.Entries)
                {
                    long actualLength = 0;
                    XxHash3? hasher = entry.Checksum != 0 ? new XxHash3() : null;

                    for (int piece = 0; piece < entry.RecordCount; piece++)
                    {
                        using RecordLease lease = options.Async
                            ? await artifact.ReadRecordAsync(entry.GlobalIndex + piece).ConfigureAwait(false)
                            : artifact.ReadRecord(entry.GlobalIndex + piece);

                        actualLength += lease.Length;

                        if (hasher is not null)
                        {
                            AppendSequence(hasher, lease.Sequence);
                        }
                    }

                    if (actualLength != entry.Length)
                    {
                        failed++;
                        ConsoleReport.Error(
                            $"length mismatch for '{entry.RelativePath}': catalog says {entry.Length}, records have {actualLength}.");
                        continue;
                    }

                    if (hasher is not null)
                    {
                        ulong actual = hasher.GetCurrentHashAsUInt64();

                        if (actual != entry.Checksum)
                        {
                            failed++;
                            ConsoleReport.Error(
                                $"checksum mismatch for '{entry.RelativePath}': expected {ConsoleReport.Checksum(entry.Checksum)}, got {ConsoleReport.Checksum(actual)}.");
                            continue;
                        }
                    }
                    else
                    {
                        // No whole-file checksum was stored, so reading each record (which the format
                        // checksums on its own) is the integrity guarantee for this entry.
                        withoutCatalogChecksum++;
                    }

                    verified++;
                    bytesVerified += actualLength;
                }

                verifyWatch.Stop();
                verifyElapsed = verifyWatch.Elapsed;
                verifyMetrics = verifyScope.Stop(bytesVerified);

                ReportVerification(verified, failed, withoutCatalogChecksum, bytesVerified, verifyElapsed);
            }

            int sourceMatches = 0;
            int sourceMismatches = 0;

            if (compareRoot is not null && Directory.Exists(compareRoot))
            {
                (sourceMatches, sourceMismatches) =
                    await CompareWithSourceAsync(artifact, catalog, compareRoot, options.Async).ConfigureAwait(false);
            }

            if (options.ExtractDirectory is not null)
            {
                await ExtractAsync(artifact, catalog, options.ExtractDirectory, options.Async).ConfigureAwait(false);
            }

            if (options.Cat is not null)
            {
                await CatAsync(artifact, catalog, options.Cat, options.Async).ConfigureAwait(false);
            }

            return new LoadResult
            {
                Catalog = catalog,
                OpenElapsed = openWatch.Elapsed,
                VerifyElapsed = verifyElapsed,
                VerifiedRecords = verified,
                FailedRecords = failed,
                BytesVerified = bytesVerified,
                SourceMatches = sourceMatches,
                SourceMismatches = sourceMismatches,
                OpenMetrics = openMetrics,
                VerifyMetrics = verifyMetrics,
            };
        }
    }

    /// <summary>
    /// Recovers the harness catalog from record zero of the artifact
    /// </summary>
    /// <param name="artifact">Open artifact to read from</param>
    /// <param name="useAsync">Whether to use the asynchronous record API</param>
    /// <returns>The deserialized catalog</returns>
    /// <exception cref="System.IO.InvalidDataException">The artifact contains no records at all.</exception>
    private static async Task<FileCatalog> ReadCatalogAsync(Artifact artifact, bool useAsync)
    {
        if (artifact.RecordCount == 0)
        {
            throw new InvalidDataException("The artifact contains no records, so it has no catalog.");
        }

        using RecordLease lease = useAsync
            ? await artifact.ReadRecordAsync(0).ConfigureAwait(false)
            : artifact.ReadRecord(0);

        return FileCatalog.Deserialize(lease.Sequence);
    }

    /// <summary>
    /// Prints the artifact header and open timing
    /// </summary>
    /// <param name="artifact">Open artifact to describe</param>
    /// <param name="artifactPath">Path the artifact was opened from</param>
    /// <param name="fileBytes">Size of the artifact on disk, in bytes</param>
    /// <param name="openElapsed">Time taken to open the artifact</param>
    private static void ReportHeader(Artifact artifact, string artifactPath, long fileBytes, TimeSpan openElapsed)
    {
        ConsoleReport.Heading("ARTIFACT");
        ConsoleReport.Field("Path", artifactPath);
        ConsoleReport.Field("Size on disk", $"{ConsoleReport.Bytes(fileBytes)} ({ConsoleReport.Count(fileBytes)} bytes)");
        ConsoleReport.Field("Format version", $"{artifact.Header.VersionMajor}.{artifact.Header.VersionMinor}");
        ConsoleReport.Field("Pointer size", $"{artifact.Header.PointerSize} bytes");
        ConsoleReport.Field("Content length", ConsoleReport.Count((long)artifact.Header.ContentLength));
        ConsoleReport.Field("Manifest offset", ConsoleReport.Count((long)artifact.Header.ManifestOffset));
        ConsoleReport.Field("Manifest length", ConsoleReport.Count((long)artifact.Header.ManifestLength));
        ConsoleReport.Field("Chunk source", artifact.SourceKind.ToString());
        ConsoleReport.Field("Segments", ConsoleReport.Count(artifact.Segments.Count));
        ConsoleReport.Field("Records", ConsoleReport.Count(artifact.RecordCount));
        ConsoleReport.Field("Open time", ConsoleReport.Duration(openElapsed));

        double ratio = fileBytes > 0 ? openElapsed.TotalMilliseconds / (fileBytes / (1024d * 1024d)) : 0d;
        ConsoleReport.Field(
            "Open cost",
            string.Format(CultureInfo.InvariantCulture, "{0:0.####} ms per MiB of artifact", ratio));
    }

    /// <summary>
    /// Prints the metadata recovered from the catalog
    /// </summary>
    /// <param name="catalog">Catalog recovered from the artifact</param>
    /// <param name="artifact">Open artifact the catalog came from</param>
    private static void ReportCatalog(FileCatalog catalog, Artifact artifact)
    {
        ConsoleReport.Heading("CATALOG");
        ConsoleReport.Field("Source root", catalog.SourceRoot);
        ConsoleReport.Field("Created (UTC)", catalog.CreatedUtc.ToString("u", CultureInfo.InvariantCulture));
        ConsoleReport.Field("Grouping", catalog.GroupingMode);
        ConsoleReport.Field("Files", ConsoleReport.Count(catalog.Entries.Count));
        ConsoleReport.Field("Payload bytes", ConsoleReport.Bytes(catalog.TotalBytes));

        long overhead = (long)artifact.Header.ContentLength - catalog.TotalBytes;
        double overheadPercent = catalog.TotalBytes > 0 ? overhead * 100d / catalog.TotalBytes : 0d;

        ConsoleReport.Field(
            "Format overhead",
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1:0.##}% of payload; directories, 64-byte alignment, manifest)",
                ConsoleReport.Bytes(overhead),
                overheadPercent));
    }

    /// <summary>
    /// Prints one row per artifact segment
    /// </summary>
    /// <param name="artifact">Open artifact to describe</param>
    /// <param name="catalog">Catalog supplying the human readable segment names</param>
    private static void ReportSegments(Artifact artifact, FileCatalog catalog)
    {
        ConsoleReport.Subheading("Segments");

        List<string[]> rows = [];

        for (int i = 0; i < artifact.Segments.Count; i++)
        {
            ArtifactSegment segment = artifact.Segments[i];

            string name = i == 0
                ? "(catalog)"
                : i - 1 < catalog.GroupNames.Count ? catalog.GroupNames[i - 1] : "(unnamed)";

            rows.Add(
            [
                i.ToString(CultureInfo.InvariantCulture),
                segment.SegmentId.ToString(CultureInfo.InvariantCulture),
                name,
                ConsoleReport.Count(segment.RecordCount),
                ConsoleReport.Bytes(segment.DataLength),
                ConsoleReport.Checksum(segment.Checksum),
            ]);
        }

        ConsoleReport.Table(
            ["#", "id", "name", "records", "data", "xxh3"],
            rows,
            [true, true, false, true, true, false]);
    }

    /// <summary>
    /// Prints the catalog listing, truncated to a row limit
    /// </summary>
    /// <param name="catalog">Catalog to list</param>
    /// <param name="top">Maximum number of rows to print</param>
    private static void ReportListing(FileCatalog catalog, int top)
    {
        ConsoleReport.Subheading($"Files (showing up to {top} of {ConsoleReport.Count(catalog.Entries.Count)})");

        List<string[]> rows = [];

        foreach (CatalogEntry entry in catalog.Entries.Take(top))
        {
            rows.Add(
            [
                entry.GlobalIndex.ToString(CultureInfo.InvariantCulture),
                $"{entry.SegmentIndex}:{entry.RecordIndex}",
                ConsoleReport.Count(entry.RecordCount),
                ConsoleReport.Bytes(entry.Length),
                ConsoleReport.Checksum(entry.Checksum),
                entry.LastWriteUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                entry.RelativePath,
            ]);
        }

        ConsoleReport.Table(
            ["idx", "seg:rec", "recs", "size", "xxh3", "modified (UTC)", "path"],
            rows,
            [true, false, true, true, false, false, false]);

        if (catalog.Entries.Count > top)
        {
            ConsoleReport.Line($"... {ConsoleReport.Count(catalog.Entries.Count - top)} more (raise with --top).");
        }
    }

    /// <summary>
    /// Prints the outcome of checksum verification
    /// </summary>
    /// <param name="verified">Number of records that verified successfully</param>
    /// <param name="failed">Number of records that failed verification</param>
    /// <param name="withoutCatalogChecksum">Number of entries that had no whole-file catalog checksum and were verified via record checksums</param>
    /// <param name="bytesVerified">Total number of payload bytes read</param>
    /// <param name="elapsed">Time taken to verify</param>
    private static void ReportVerification(
        int verified,
        int failed,
        int withoutCatalogChecksum,
        long bytesVerified,
        TimeSpan elapsed)
    {
        ConsoleReport.Subheading("Verification");
        ConsoleReport.Field("Entries verified", ConsoleReport.Count(verified));
        ConsoleReport.Field("Entries failed", ConsoleReport.Count(failed));

        if (withoutCatalogChecksum > 0)
        {
            ConsoleReport.Field(
                "No catalog checksum",
                $"{ConsoleReport.Count(withoutCatalogChecksum)} verified via record checksums");
        }

        ConsoleReport.Field("Bytes read", ConsoleReport.Bytes(bytesVerified));
        ConsoleReport.Field("Elapsed", ConsoleReport.Duration(elapsed));

        if (elapsed.TotalSeconds > 0 && bytesVerified > 0)
        {
            double throughput = bytesVerified / (1024d * 1024d) / elapsed.TotalSeconds;
            ConsoleReport.Field(
                "Throughput",
                string.Format(CultureInfo.InvariantCulture, "{0:0.##} MiB/s", throughput));
        }
    }

    /// <summary>
    /// Compares every record against the file it was packed from
    /// </summary>
    /// <param name="artifact">Open artifact to read from</param>
    /// <param name="catalog">Catalog describing the packed files</param>
    /// <param name="root">Folder the artifact was packed from</param>
    /// <param name="useAsync">Whether to use the asynchronous record API</param>
    /// <returns>A tuple holding the number of matching and mismatching files</returns>
    /// <remarks>
    /// Files that changed on disk since packing are reported as skipped rather than as mismatches,
    /// because the artifact is immutable by design and is not expected to track later edits.
    /// </remarks>
    private static async Task<(int Matches, int Mismatches)> CompareWithSourceAsync(
        Artifact artifact,
        FileCatalog catalog,
        string root,
        bool useAsync)
    {
        ConsoleReport.Subheading("Comparison against the source tree");

        int matches = 0;
        int mismatches = 0;
        int skipped = 0;

        foreach (CatalogEntry entry in catalog.Entries)
        {
            string path = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                skipped++;
                continue;
            }

            byte[] onDisk;

            try
            {
                onDisk = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            if (entry.Checksum != 0 && XxHash3.HashToUInt64(onDisk) != entry.Checksum)
            {
                // A whole-file checksum was stored and it no longer matches: the file changed after it
                // was packed, which is not an artifact defect.
                skipped++;
                continue;
            }

            byte[] artifactBytes = await ReadEntryBytesAsync(artifact, entry, useAsync).ConfigureAwait(false);

            // With no catalog checksum we cannot tell "changed on disk" from "corrupt", so compare the
            // reassembled artifact bytes against the file directly, which is the stronger check anyway.
            if (artifactBytes.AsSpan().SequenceEqual(onDisk))
            {
                matches++;
            }
            else
            {
                mismatches++;
                ConsoleReport.Error($"content mismatch for '{entry.RelativePath}'.");
            }
        }

        ConsoleReport.Field("Byte-identical", ConsoleReport.Count(matches));
        ConsoleReport.Field("Different", ConsoleReport.Count(mismatches));
        ConsoleReport.Field("Skipped", $"{ConsoleReport.Count(skipped)} (missing or changed on disk)");

        return (matches, mismatches);
    }

    /// <summary>
    /// Writes every packed file into a destination directory
    /// </summary>
    /// <param name="artifact">Open artifact to read from</param>
    /// <param name="catalog">Catalog describing the packed files</param>
    /// <param name="destination">Directory to write into; created when it does not exist</param>
    /// <param name="useAsync">Whether to use the asynchronous record API</param>
    /// <returns>A <see cref="System.Threading.Tasks.Task"/> that completes once every file has been written.</returns>
    /// <exception cref="System.InvalidOperationException">A catalog entry escapes the destination directory.</exception>
    private static async Task ExtractAsync(
        Artifact artifact,
        FileCatalog catalog,
        string destination,
        bool useAsync)
    {
        ConsoleReport.Subheading("Extraction");

        string fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(fullDestination);

        Stopwatch watch = Stopwatch.StartNew();
        long written = 0;

        foreach (CatalogEntry entry in catalog.Entries)
        {
            string relative = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(fullDestination, relative));

            if (!target.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{entry.RelativePath}' escapes the extraction directory; refusing to write it.");
            }

            string? directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using FileStream stream = new(target, FileMode.Create, FileAccess.Write, FileShare.None);

            for (int piece = 0; piece < entry.RecordCount; piece++)
            {
                using RecordLease lease = useAsync
                    ? await artifact.ReadRecordAsync(entry.GlobalIndex + piece).ConfigureAwait(false)
                    : artifact.ReadRecord(entry.GlobalIndex + piece);

                foreach (ReadOnlyMemory<byte> memory in lease.Sequence)
                {
                    await stream.WriteAsync(memory).ConfigureAwait(false);
                }

                written += lease.Length;
            }
        }

        watch.Stop();

        ConsoleReport.Field("Destination", fullDestination);
        ConsoleReport.Field("Files written", ConsoleReport.Count(catalog.Entries.Count));
        ConsoleReport.Field("Bytes written", ConsoleReport.Bytes(written));
        ConsoleReport.Field("Elapsed", ConsoleReport.Duration(watch.Elapsed));
    }

    /// <summary>
    /// Prints a single record, located by its relative path
    /// </summary>
    /// <param name="artifact">Open artifact to read from</param>
    /// <param name="catalog">Catalog used to resolve the path to a record index</param>
    /// <param name="relativePath">Relative path of the file to print</param>
    /// <param name="useAsync">Whether to use the asynchronous record API</param>
    /// <returns>A <see cref="System.Threading.Tasks.Task"/> that completes once the record has been printed.</returns>
    private static async Task CatAsync(Artifact artifact, FileCatalog catalog, string relativePath, bool useAsync)
    {
        string normalized = relativePath.Replace('\\', '/');

        CatalogEntry? entry = catalog.Entries.FirstOrDefault(
            e => string.Equals(e.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            ConsoleReport.Error($"'{relativePath}' is not present in the artifact.");
            return;
        }

        ConsoleReport.Subheading($"cat {entry.RelativePath}");
        ConsoleReport.Field("Global index", ConsoleReport.Count(entry.GlobalIndex));
        ConsoleReport.Field("Segment:record", $"{entry.SegmentIndex}:{entry.RecordIndex}");
        ConsoleReport.Field("Length", ConsoleReport.Bytes(entry.Length));
        ConsoleReport.Blank();

        // The preview only shows a prefix, so read the first piece regardless of how many the file
        // spans. entry.Length is the true total and drives the truncation message.
        using RecordLease lease = useAsync
            ? await artifact.ReadRecordAsync(entry.GlobalIndex).ConfigureAwait(false)
            : artifact.ReadRecord(entry.GlobalIndex);

        byte[] bytes = lease.ToArray();
        int limit = Math.Min(bytes.Length, CatPreviewLimit);

        ConsoleReport.Always(Encoding.UTF8.GetString(bytes, 0, limit));

        if (entry.Length > limit)
        {
            ConsoleReport.Line($"... truncated at {ConsoleReport.Bytes(limit)} of {ConsoleReport.Bytes(entry.Length)}.");
        }
    }

    /// <summary>
    /// Appends the contents of a possibly multi-segment sequence to a running hash
    /// </summary>
    /// <param name="hasher">Incremental hasher to append to</param>
    /// <param name="sequence">Record contents to hash</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="hasher" /> is <see langword="null" />.</exception>
    private static void AppendSequence(XxHash3 hasher, ReadOnlySequence<byte> sequence)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        foreach (ReadOnlyMemory<byte> memory in sequence)
        {
            hasher.Append(memory.Span);
        }
    }

    /// <summary>
    /// Reads every record of an entry and concatenates them into a single contiguous buffer
    /// </summary>
    /// <param name="artifact">Open artifact to read from</param>
    /// <param name="entry">Catalog entry describing the file and its record range</param>
    /// <param name="useAsync">Whether to use the asynchronous record API</param>
    /// <returns>A buffer of exactly <see cref="CatalogEntry.Length" /> bytes holding the whole file</returns>
    /// <remarks>
    /// A file may span several consecutive records when it was larger than the pack's piece size, so
    /// this stitches those pieces back together. Callers that only need a prefix, or that can stream
    /// straight to an output, should avoid this and read the pieces directly to keep large files from
    /// being materialized whole.
    /// </remarks>
    /// <exception cref="System.ArgumentNullException"><paramref name="artifact" /> or <paramref name="entry" /> is <see langword="null" />.</exception>
    private static async ValueTask<byte[]> ReadEntryBytesAsync(Artifact artifact, CatalogEntry entry, bool useAsync)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(entry);

        byte[] result = new byte[entry.Length];
        int offset = 0;

        for (int piece = 0; piece < entry.RecordCount; piece++)
        {
            using RecordLease lease = useAsync
                ? await artifact.ReadRecordAsync(entry.GlobalIndex + piece).ConfigureAwait(false)
                : artifact.ReadRecord(entry.GlobalIndex + piece);

            lease.Sequence.CopyTo(result.AsSpan(offset));
            offset += (int)lease.Length;
        }

        return result;
    }
}
