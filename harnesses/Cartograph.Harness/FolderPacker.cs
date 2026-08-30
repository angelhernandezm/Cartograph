// ============================================================================
// Cartograph
// File: FolderPacker.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Recursively walks a folder, groups the discovered files into artifact
// segments and writes them, together with a catalog record, into a new
// Cartograph artifact
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
using System.IO.Enumeration;
using System.IO.Hashing;
using Cartograph.Format;

namespace Cartograph.Harness;

/// <summary>
/// Summarizes the outcome of a packing run
/// </summary>
internal sealed class PackResult
{
    /// <summary>
    /// Gets the fully qualified path of the artifact that was written
    /// </summary>
    /// <value>The file the harness created.</value>
    public required string ArtifactPath { get; init; }

    /// <summary>
    /// Gets the catalog that was embedded in the artifact
    /// </summary>
    /// <value>The in-memory catalog, identical to the one stored as record zero.</value>
    public required FileCatalog Catalog { get; init; }

    /// <summary>
    /// Gets the size of the artifact on disk, in bytes
    /// </summary>
    /// <value>Includes the header, the record directories, alignment padding and the manifest.</value>
    public required long ArtifactBytes { get; init; }

    /// <summary>
    /// Gets the number of segments in the artifact, including the catalog segment
    /// </summary>
    /// <value>One more than the number of content groups.</value>
    public required int SegmentCount { get; init; }

    /// <summary>
    /// Gets the total number of records written to the artifact, including the catalog record
    /// </summary>
    /// <value>
    /// The catalog record plus one record per piece of every packed file. A file larger than the
    /// piece size contributes several records, so this can exceed the packed file count.
    /// </value>
    public required long RecordsWritten { get; init; }

    /// <summary>
    /// Gets the time spent enumerating and filtering the source tree
    /// </summary>
    /// <value>Wall-clock duration of the directory walk.</value>
    public required TimeSpan ScanElapsed { get; init; }

    /// <summary>
    /// Gets the time spent reading file contents and writing the artifact
    /// </summary>
    /// <value>Wall-clock duration of the read and write phase.</value>
    public required TimeSpan WriteElapsed { get; init; }

    /// <summary>
    /// Gets the number of files skipped because they exceeded the size limit
    /// </summary>
    /// <value>Zero when every discovered file fitted within the limit.</value>
    public required int SkippedTooLarge { get; init; }

    /// <summary>
    /// Gets the number of files skipped because they could not be read
    /// </summary>
    /// <value>Typically caused by permissions or by another process holding the file open.</value>
    public required int SkippedUnreadable { get; init; }

    /// <summary>
    /// Gets the number of files skipped because they did not match the include or exclude patterns
    /// </summary>
    /// <value>Zero when no patterns were supplied.</value>
    public required int SkippedFiltered { get; init; }

    /// <summary>
    /// Gets the reason packing stopped before the whole tree was consumed
    /// </summary>
    /// <value>A short explanation, or <see langword="null" /> when the entire tree was packed.</value>
    public string? StopReason { get; init; }
}

/// <summary>
/// Builds a Cartograph artifact from the contents of a folder
/// </summary>
/// <remarks>
/// The layout the packer produces is deliberately simple and is the point of the harness: segment
/// zero holds exactly one record, the serialized <see cref="FileCatalog" />, and every subsequent
/// segment holds the records of one content group. File contents are streamed from disk at save time
/// rather than buffered in managed memory, and a file larger than the configured piece size is split
/// across several consecutive records. A reader therefore needs a single record read to learn the
/// entire directory structure, and can then address any file directly by its first global index.
/// </remarks>
internal static class FolderPacker
{
    /// <summary>
    /// Segment name used when a file has no extension
    /// </summary>
    private const string NoExtensionGroup = "(no extension)";

    /// <summary>
    /// Segment name used for files that sit directly in the packed root
    /// </summary>
    private const string RootDirectoryGroup = "(root)";

    /// <summary>
    /// Segment name used when every file is placed in a single segment
    /// </summary>
    private const string FlatGroup = "(all files)";

    /// <summary>
    /// Packs a folder tree into a new artifact
    /// </summary>
    /// <param name="options">Parsed command line controlling the walk, the grouping and the limits</param>
    /// <param name="root">Fully qualified path of the folder to pack</param>
    /// <param name="artifactPath">Fully qualified path of the artifact to write</param>
    /// <returns>A description of what was packed and how long it took</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="root" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="artifactPath" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.IO.DirectoryNotFoundException"><paramref name="root" /> does not exist.</exception>
    /// <exception cref="System.InvalidOperationException">No files matched, so there is nothing to pack.</exception>
    public static PackResult Pack(HarnessOptions options, string root, string artifactPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(artifactPath);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root folder '{root}' does not exist.");
        }

        Stopwatch scanWatch = Stopwatch.StartNew();
        List<Candidate> candidates = Scan(options, root, out int skippedTooLarge, out int skippedFiltered);
        scanWatch.Stop();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No files under '{root}' matched the current filters, so there is nothing to pack.");
        }

        ConsoleReport.Field("Files discovered", ConsoleReport.Count(candidates.Count));

        Stopwatch writeWatch = Stopwatch.StartNew();

        List<SegmentGroup> groups = Group(options, candidates);
        int skippedUnreadable = 0;
        long packedBytes = 0;
        int packedFiles = 0;
        string? stopReason = null;

        List<CatalogEntry> entries = [];
        List<string> groupNames = [];
        List<List<FilePiece>> payloads = [];

        foreach (SegmentGroup group in groups)
        {
            if (stopReason is not null)
            {
                break;
            }

            List<FilePiece> pieces = [];
            int segmentIndex = payloads.Count + 1; // Segment 0 is always the catalog.

            foreach (Candidate candidate in group.Files)
            {
                if (packedFiles >= options.MaxFiles)
                {
                    stopReason = $"Reached the --max-files limit of {ConsoleReport.Count(options.MaxFiles)}.";
                    break;
                }

                if (packedBytes + candidate.Length > options.MaxTotalBytes)
                {
                    stopReason = $"Reached the --max-total budget of {ConsoleReport.Bytes(options.MaxTotalBytes)}.";
                    break;
                }

                // Validate readability cheaply and, when asked, hash the file. Both open the file so a
                // locked or unreadable file is skipped gracefully here rather than aborting the pack
                // inside writer.Save. Contents are not otherwise read during packing.
                ulong checksum;

                if (options.ComputeChecksums)
                {
                    if (!TryHashFile(candidate.FullPath, out checksum, out string? hashError))
                    {
                        skippedUnreadable++;
                        ConsoleReport.Warn($"skipping '{candidate.RelativePath}': {hashError}");
                        continue;
                    }
                }
                else
                {
                    if (!TryValidateReadable(candidate.FullPath, out string? readError))
                    {
                        skippedUnreadable++;
                        ConsoleReport.Warn($"skipping '{candidate.RelativePath}': {readError}");
                        continue;
                    }

                    checksum = 0;
                }

                int firstRecordIndex = pieces.Count;
                int pieceCount = PieceCount(candidate.Length, options.PieceSize);
                long offset = 0;
                long remaining = candidate.Length;

                for (int p = 0; p < pieceCount; p++)
                {
                    long pieceLength = Math.Min(remaining, options.PieceSize);
                    pieces.Add(new FilePiece(candidate.FullPath, offset, pieceLength));
                    offset += pieceLength;
                    remaining -= pieceLength;
                }

                entries.Add(new CatalogEntry
                {
                    RelativePath = candidate.RelativePath,
                    Length = candidate.Length,
                    RecordCount = pieceCount,
                    LastWriteUtcTicks = candidate.LastWriteUtc.Ticks,
                    Checksum = checksum,
                    SegmentIndex = segmentIndex,
                    RecordIndex = firstRecordIndex,
                    GlobalIndex = 0, // Patched once every segment is sized.
                });

                packedBytes += candidate.Length;
                packedFiles++;
            }

            if (pieces.Count > 0)
            {
                groupNames.Add(group.Name);
                payloads.Add(pieces);
            }
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Every candidate file failed to read, so there is nothing to pack.");
        }

        AssignGlobalIndices(entries, payloads);

        FileCatalog catalog = new()
        {
            SourceRoot = root,
            CreatedUtc = DateTime.UtcNow,
            GroupingMode = options.Grouping.ToString().ToLowerInvariant(),
            GroupNames = groupNames,
            Entries = entries,
        };

        SegmentedArtifactWriter writer = new();

        // Segment 0: the catalog. Written first so that it is always global record 0.
        writer.AddSegment(0u).AddRecord(catalog.Serialize());

        long recordsWritten = 1; // The catalog record.

        for (int i = 0; i < payloads.Count; i++)
        {
            SegmentBuilder segment = writer.AddSegment((uint)(i + 1));

            foreach (FilePiece piece in payloads[i])
            {
                segment.AddFileRecord(piece.Path, piece.Offset, piece.Length);
                recordsWritten++;
            }
        }

        string? directory = Path.GetDirectoryName(artifactPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // File contents are read here, at save time. A source file that was deleted, truncated or
        // locked between the scan and now will surface as an exception rather than a silent corrupt
        // artifact, so translate it into a clear runtime error.
        try
        {
            writer.Save(artifactPath);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or FileNotFoundException
                or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException(
                "A source file changed or became unreadable while the artifact was being written, " +
                $"so the pack was aborted rather than producing a corrupt artifact: {ex.Message}",
                ex);
        }

        writeWatch.Stop();

        return new PackResult
        {
            ArtifactPath = artifactPath,
            Catalog = catalog,
            ArtifactBytes = new FileInfo(artifactPath).Length,
            SegmentCount = payloads.Count + 1,
            RecordsWritten = recordsWritten,
            ScanElapsed = scanWatch.Elapsed,
            WriteElapsed = writeWatch.Elapsed,
            SkippedTooLarge = skippedTooLarge,
            SkippedUnreadable = skippedUnreadable,
            SkippedFiltered = skippedFiltered,
            StopReason = stopReason,
        };
    }

    /// <summary>
    /// Enumerates the source tree and applies the size and pattern filters
    /// </summary>
    /// <param name="options">Parsed command line supplying the filters</param>
    /// <param name="root">Fully qualified path of the folder to walk</param>
    /// <param name="skippedTooLarge">Receives the number of files rejected by the size limit</param>
    /// <param name="skippedFiltered">Receives the number of files rejected by the include or exclude patterns</param>
    /// <returns>The surviving files, ordered by relative path so that runs are reproducible</returns>
    private static List<Candidate> Scan(
        HarnessOptions options,
        string root,
        out int skippedTooLarge,
        out int skippedFiltered)
    {
        skippedTooLarge = 0;
        skippedFiltered = 0;

        EnumerationOptions enumeration = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = options.FollowLinks ? FileAttributes.None : FileAttributes.ReparsePoint,
        };

        List<Candidate> candidates = [];

        foreach (string path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

            if (!Matches(options, relative))
            {
                skippedFiltered++;
                continue;
            }

            FileInfo info;

            try
            {
                info = new FileInfo(path);

                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (info.Length > options.MaxFileSize)
            {
                skippedTooLarge++;
                continue;
            }

            candidates.Add(new Candidate(path, relative, info.Length, info.LastWriteTimeUtc));
        }

        candidates.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return candidates;
    }

    /// <summary>
    /// Evaluates the include and exclude patterns against a relative path
    /// </summary>
    /// <param name="options">Parsed command line supplying the patterns</param>
    /// <param name="relativePath">Path relative to the packed root, using forward slashes</param>
    /// <returns><see langword="true" /> when the file should be packed; otherwise <see langword="false" /></returns>
    private static bool Matches(HarnessOptions options, string relativePath)
    {
        if (options.Includes.Count > 0)
        {
            bool included = false;

            foreach (string pattern in options.Includes)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, relativePath, ignoreCase: true))
                {
                    included = true;
                    break;
                }
            }

            if (!included)
            {
                return false;
            }
        }

        foreach (string pattern in options.Excludes)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, relativePath, ignoreCase: true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Distributes the discovered files across named segment groups
    /// </summary>
    /// <param name="options">Parsed command line supplying the grouping strategy</param>
    /// <param name="candidates">Files to distribute, already ordered by relative path</param>
    /// <returns>The groups, ordered by name so that runs are reproducible</returns>
    private static List<SegmentGroup> Group(HarnessOptions options, List<Candidate> candidates)
    {
        Dictionary<string, SegmentGroup> groups = new(StringComparer.Ordinal);

        foreach (Candidate candidate in candidates)
        {
            string key = options.Grouping switch
            {
                SegmentGrouping.Extension => ExtensionKey(candidate.RelativePath),
                SegmentGrouping.Directory => DirectoryKey(candidate.RelativePath),
                _ => FlatGroup,
            };

            if (!groups.TryGetValue(key, out SegmentGroup? group))
            {
                group = new SegmentGroup(key);
                groups.Add(key, group);
            }

            group.Files.Add(candidate);
        }

        List<SegmentGroup> ordered = [.. groups.Values];
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return ordered;
    }

    /// <summary>
    /// Derives the segment name for extension-based grouping
    /// </summary>
    /// <param name="relativePath">Path relative to the packed root</param>
    /// <returns>The lower-case extension, or a placeholder when the file has none</returns>
    private static string ExtensionKey(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        return extension.Length == 0 ? NoExtensionGroup : extension.ToLowerInvariant();
    }

    /// <summary>
    /// Derives the segment name for directory-based grouping
    /// </summary>
    /// <param name="relativePath">Path relative to the packed root, using forward slashes</param>
    /// <returns>The first path component, or a placeholder for files in the root itself</returns>
    private static string DirectoryKey(string relativePath)
    {
        int slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? RootDirectoryGroup : relativePath[..slash];
    }

    /// <summary>
    /// Fills in the artifact-wide index of every catalog entry's first record
    /// </summary>
    /// <param name="entries">Catalog entries whose segment and record indices are already set</param>
    /// <param name="payloads">Record pieces, one list per content segment</param>
    /// <remarks>
    /// The global index of a record is the number of records in all preceding segments plus its own
    /// index within its segment. A segment's record count is the total number of file pieces it holds,
    /// which is the sum of the per-file piece counts rather than the file count. Segment zero always
    /// contributes exactly one record, the catalog.
    /// </remarks>
    private static void AssignGlobalIndices(List<CatalogEntry> entries, List<List<FilePiece>> payloads)
    {
        long[] segmentStart = new long[payloads.Count + 1];
        segmentStart[0] = 0;

        long running = 1; // The catalog record occupies global index 0.

        for (int i = 0; i < payloads.Count; i++)
        {
            segmentStart[i + 1] = running;
            running += payloads[i].Count;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            CatalogEntry entry = entries[i];

            entries[i] = new CatalogEntry
            {
                RelativePath = entry.RelativePath,
                Length = entry.Length,
                RecordCount = entry.RecordCount,
                LastWriteUtcTicks = entry.LastWriteUtcTicks,
                Checksum = entry.Checksum,
                SegmentIndex = entry.SegmentIndex,
                RecordIndex = entry.RecordIndex,
                GlobalIndex = segmentStart[entry.SegmentIndex] + entry.RecordIndex,
            };
        }
    }

    /// <summary>
    /// Computes the number of records a file of a given length occupies
    /// </summary>
    /// <param name="length">Length of the file in bytes</param>
    /// <param name="pieceSize">Maximum number of bytes stored in a single record</param>
    /// <returns>At least one; the number of consecutive records the file is split across</returns>
    private static int PieceCount(long length, long pieceSize)
    {
        if (length <= 0)
        {
            return 1;
        }

        return (int)((length + pieceSize - 1) / pieceSize);
    }

    /// <summary>
    /// Confirms a file can be opened for reading without pulling its contents into memory
    /// </summary>
    /// <param name="path">Fully qualified path of the file to probe</param>
    /// <param name="error">On failure, a human readable description of the problem; otherwise <see langword="null" /></param>
    /// <returns><see langword="true" /> when the file opened; otherwise <see langword="false" /></returns>
    private static bool TryValidateReadable(string path, out string? error)
    {
        error = null;

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Streams a file through <see cref="XxHash3" /> to compute its whole-file checksum
    /// </summary>
    /// <param name="path">Fully qualified path of the file to hash</param>
    /// <param name="hash">On success, the 64-bit hash of the whole file; otherwise zero</param>
    /// <param name="error">On failure, a human readable description of the problem; otherwise <see langword="null" /></param>
    /// <returns><see langword="true" /> when the file was hashed; otherwise <see langword="false" /></returns>
    /// <remarks>
    /// Uses a pooled one-mebibyte buffer so hashing a multi-gigabyte file stays memory-bounded and
    /// never reads the whole file into a managed array.
    /// </remarks>
    private static bool TryHashFile(string path, out ulong hash, out string? error)
    {
        hash = 0;
        error = null;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            XxHash3 hasher = new();
            int read;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
            }

            hash = hasher.GetCurrentHashAsUInt64();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Represents one streamed record: a byte range of a file on disk
    /// </summary>
    /// <param name="Path">Fully qualified path of the source file</param>
    /// <param name="Offset">Zero-based byte offset of the range within the file</param>
    /// <param name="Length">Length of the range in bytes</param>
    private sealed record FilePiece(string Path, long Offset, long Length);

    /// <summary>
    /// Represents a file that survived filtering and is a candidate for packing
    /// </summary>
    /// <param name="FullPath">Fully qualified path of the file on disk</param>
    /// <param name="RelativePath">Path relative to the packed root, using forward slashes</param>
    /// <param name="Length">Length of the file in bytes, as observed during the scan</param>
    /// <param name="LastWriteUtc">Last write time of the file, in UTC</param>
    private sealed record Candidate(string FullPath, string RelativePath, long Length, DateTime LastWriteUtc);

    /// <summary>
    /// Represents the set of files destined for a single artifact segment
    /// </summary>
    /// <param name="name">Display name of the group, also used to order segments</param>
    private sealed class SegmentGroup(string name)
    {
        /// <summary>
        /// Gets the display name of the group
        /// </summary>
        /// <value>The extension, directory name or placeholder that defined the group.</value>
        public string Name { get; } = name;

        /// <summary>
        /// Gets the files assigned to the group, in relative path order
        /// </summary>
        /// <value>One entry per file that will become a record in the segment.</value>
        public List<Candidate> Files { get; } = [];
    }
}
