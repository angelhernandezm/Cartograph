// ============================================================================
// Cartograph
// File: BaselineContainer.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Abstraction over the .NET container strategies the baseline can pack into and
// read back, together with the source tree scanner shared by all of them
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

using System.IO.Enumeration;

namespace Cartograph.Baseline;

/// <summary>
/// Identifies the .NET storage strategy the baseline uses
/// </summary>
/// <remarks>
/// The modes are deliberately ordered from the most idiomatic to the most carefully optimized, so
/// that a profile run shows both what a developer would write by default and how close plain .NET
/// can get when someone tries hard.
/// </remarks>
internal enum ContainerKind
{
    /// <summary>
    /// A ZIP archive written with compression disabled
    /// </summary>
    ZipStore = 0,

    /// <summary>
    /// A ZIP archive written with the Deflate algorithm
    /// </summary>
    ZipDeflate = 1,

    /// <summary>
    /// A flat container read entirely into a single managed array
    /// </summary>
    Naive = 2,

    /// <summary>
    /// A flat container whose index alone is read, with records fetched on demand into a reused buffer
    /// </summary>
    NaiveStream = 3,

    /// <summary>
    /// No container at all; the files are read individually from the source tree
    /// </summary>
    Loose = 4,
}

/// <summary>
/// Describes a file discovered in the source tree
/// </summary>
/// <param name="FullPath">Fully qualified path of the file on disk</param>
/// <param name="RelativePath">Path relative to the scanned root, using forward slashes</param>
/// <param name="Length">Length of the file in bytes, as observed during the scan</param>
/// <param name="LastWriteUtc">Last write time of the file, in UTC</param>
internal sealed record SourceFile(string FullPath, string RelativePath, long Length, DateTime LastWriteUtc);

/// <summary>
/// Describes a single file stored in a baseline container
/// </summary>
internal sealed class BaselineEntry
{
    /// <summary>
    /// Gets the path of the file relative to the packed root, using forward slashes
    /// </summary>
    /// <value>A relative path such as <c>docs/reference/item-0001.md</c>.</value>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Gets the length of the file, in bytes
    /// </summary>
    /// <value>The exact byte length of the stored payload.</value>
    public required long Length { get; init; }

    /// <summary>
    /// Gets the last write time of the source file, expressed in UTC ticks
    /// </summary>
    /// <value>The tick count of the file's UTC last write time at the moment it was packed.</value>
    public required long LastWriteUtcTicks { get; init; }

    /// <summary>
    /// Gets the XxHash3 checksum of the file contents computed at pack time
    /// </summary>
    /// <value>A 64-bit hash used to verify the payload after the container is reopened.</value>
    public required ulong Checksum { get; init; }

    /// <summary>
    /// Gets the byte offset of the payload within the container
    /// </summary>
    /// <value>Meaningful only for the flat container modes; zero otherwise.</value>
    public long Offset { get; init; }
}

/// <summary>
/// Summarizes the outcome of a baseline packing run
/// </summary>
internal sealed class BaselinePackResult
{
    /// <summary>
    /// Gets the fully qualified path of the container that was written
    /// </summary>
    /// <value>The file the baseline created, or the source root for <see cref="ContainerKind.Loose" />.</value>
    public required string ContainerPath { get; init; }

    /// <summary>
    /// Gets the number of files that were stored
    /// </summary>
    /// <value>One per packed file.</value>
    public required int FileCount { get; init; }

    /// <summary>
    /// Gets the total number of payload bytes that were stored
    /// </summary>
    /// <value>The sum of the lengths of every packed file, before any compression.</value>
    public required long PayloadBytes { get; init; }

    /// <summary>
    /// Gets the size of the container on disk, in bytes
    /// </summary>
    /// <value>Zero for <see cref="ContainerKind.Loose" />, which writes no container.</value>
    public required long ContainerBytes { get; init; }

    /// <summary>
    /// Gets the metrics captured while packing
    /// </summary>
    /// <value>Covers reading the source files and writing the container.</value>
    public required RunMetrics Metrics { get; init; }
}

/// <summary>
/// Provides read access to a packed baseline container
/// </summary>
internal abstract class ContainerReader : IDisposable
{
    /// <summary>
    /// Gets the entries describing every stored file
    /// </summary>
    /// <value>Ordered by relative path, matching the order they were packed in.</value>
    public abstract IReadOnlyList<BaselineEntry> Entries { get; }

    /// <summary>
    /// Reads the payload of one entry
    /// </summary>
    /// <param name="index">Zero-based index into <see cref="Entries" /></param>
    /// <param name="scratch">
    /// A caller-owned buffer the reader may grow and reuse across calls; implementations that can
    /// return the payload without copying it are free to ignore this
    /// </param>
    /// <returns>The payload, valid until the next call or until this reader is disposed</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="index" /> is outside the entry range.</exception>
    public abstract ReadOnlyMemory<byte> Read(int index, ref byte[] scratch);

    /// <summary>
    /// Releases the resources held by the reader
    /// </summary>
    public abstract void Dispose();

    /// <summary>
    /// Grows a scratch buffer so that it can hold at least a given number of bytes
    /// </summary>
    /// <param name="scratch">Buffer to grow in place</param>
    /// <param name="required">Minimum capacity needed, in bytes</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="required" /> exceeds the maximum array length.</exception>
    protected static void EnsureCapacity(ref byte[] scratch, long required)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(required, Array.MaxLength);

        if (scratch.Length >= required)
        {
            return;
        }

        int size = scratch.Length == 0 ? 4096 : scratch.Length;

        while (size < required)
        {
            size = size > Array.MaxLength / 2 ? Array.MaxLength : size * 2;
        }

        scratch = new byte[size];
    }
}

/// <summary>
/// Packs a folder into a container and reads it back using nothing but .NET primitives
/// </summary>
internal abstract class BaselineContainer
{
    /// <summary>
    /// Gets the display name of the strategy
    /// </summary>
    /// <value>A short label such as <c>zip-store</c>, used in reports and in the CSV label column.</value>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the file extension applied to containers this strategy writes
    /// </summary>
    /// <value>An extension including the leading dot, or an empty string when no file is written.</value>
    public abstract string Extension { get; }

    /// <summary>
    /// Writes the discovered files into a new container
    /// </summary>
    /// <param name="path">Fully qualified path of the container to write</param>
    /// <param name="files">Files to store, ordered by relative path</param>
    /// <returns>A description of what was written and what it cost</returns>
    public abstract BaselinePackResult Pack(string path, IReadOnlyList<SourceFile> files);

    /// <summary>
    /// Opens a container for reading
    /// </summary>
    /// <param name="path">Fully qualified path of the container to open</param>
    /// <param name="metrics">Receives the metrics captured while opening</param>
    /// <returns>A reader positioned over the container</returns>
    public abstract ContainerReader Open(string path, out RunMetrics metrics);

    /// <summary>
    /// Creates the strategy matching a container kind
    /// </summary>
    /// <param name="kind">Strategy to create</param>
    /// <param name="root">
    /// Source root, required by <see cref="ContainerKind.Loose" /> because it reads the tree in place
    /// </param>
    /// <returns>The requested strategy</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="kind" /> is not a known container kind.</exception>
    public static BaselineContainer Create(ContainerKind kind, string root) => kind switch
    {
        ContainerKind.ZipStore => new ZipContainer(store: true),
        ContainerKind.ZipDeflate => new ZipContainer(store: false),
        ContainerKind.Naive => new NaiveContainer(streaming: false),
        ContainerKind.NaiveStream => new NaiveContainer(streaming: true),
        ContainerKind.Loose => new LooseContainer(root),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown container kind."),
    };
}

/// <summary>
/// Walks a folder tree and applies the size and pattern filters
/// </summary>
/// <remarks>
/// Kept behaviourally identical to the scan performed by the Cartograph harness, so that both
/// programs pack exactly the same set of files and the comparison stays meaningful.
/// </remarks>
internal static class SourceScanner
{
    /// <summary>
    /// Enumerates the source tree and applies the configured filters
    /// </summary>
    /// <param name="options">Parsed command line supplying the filters</param>
    /// <param name="root">Fully qualified path of the folder to walk</param>
    /// <param name="skippedTooLarge">Receives the number of files rejected by the size limit</param>
    /// <param name="skippedFiltered">Receives the number of files rejected by the include or exclude patterns</param>
    /// <returns>The surviving files, ordered by relative path so that runs are reproducible</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="System.IO.DirectoryNotFoundException"><paramref name="root" /> does not exist.</exception>
    public static List<SourceFile> Scan(
        BaselineOptions options,
        string root,
        out int skippedTooLarge,
        out int skippedFiltered)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root folder '{root}' does not exist.");
        }

        skippedTooLarge = 0;
        skippedFiltered = 0;

        EnumerationOptions enumeration = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = options.FollowLinks ? FileAttributes.None : FileAttributes.ReparsePoint,
        };

        List<SourceFile> files = [];
        long budget = 0;

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

            if (files.Count >= options.MaxFiles || budget + info.Length > options.MaxTotalBytes)
            {
                break;
            }

            budget += info.Length;
            files.Add(new SourceFile(path, relative, info.Length, info.LastWriteTimeUtc));
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    /// <summary>
    /// Evaluates the include and exclude patterns against a relative path
    /// </summary>
    /// <param name="options">Parsed command line supplying the patterns</param>
    /// <param name="relativePath">Path relative to the scanned root, using forward slashes</param>
    /// <returns><see langword="true" /> when the file should be packed; otherwise <see langword="false" /></returns>
    private static bool Matches(BaselineOptions options, string relativePath)
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
}
