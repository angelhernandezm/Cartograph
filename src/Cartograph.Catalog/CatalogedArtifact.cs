// ============================================================================
// Cartograph
// File: CatalogedArtifact.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Read-only facade that opens a Cartograph artifact, recovers its file catalog
// from record zero, and streams, extracts or verifies any catalogued file
// without copying the payload into the managed heap
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
using System.IO.Hashing;
using Cartograph.Format;

namespace Cartograph.Catalog;

/// <summary>
/// Reports the outcome of verifying one catalogued file against its recorded checksum
/// </summary>
/// <param name="Entry">The entry that was verified.</param>
/// <param name="BytesRead">The number of payload bytes that were read and hashed.</param>
/// <param name="ComputedChecksum">The XxHash3 hash computed over the file's records.</param>
/// <param name="ExpectedChecksum">The whole-file checksum recorded at pack time, or <c>0</c> when none was recorded.</param>
/// <param name="Elapsed">Wall-clock time spent reading and hashing.</param>
public readonly record struct CatalogVerification(
    CatalogEntry Entry,
    long BytesRead,
    ulong ComputedChecksum,
    ulong ExpectedChecksum,
    TimeSpan Elapsed) {
    /// <summary>
    /// Gets a value indicating whether a whole-file checksum was available to compare against
    /// </summary>
    /// <value>
    /// <see langword="true" /> when the packer spent the extra read pass to hash the file.
    /// </value>
    public bool HasExpectedChecksum => ExpectedChecksum != 0;

    /// <summary>
    /// Gets a value indicating whether the file's contents match the recorded checksum
    /// </summary>
    /// <value>
    /// <see langword="true" /> when the checksums agree, or when no whole-file checksum was recorded.
    /// A record whose own per-record checksum fails never reaches this point, because reading it
    /// throws instead.
    /// </value>
    public bool Matches => !HasExpectedChecksum || ComputedChecksum == ExpectedChecksum;
}

/// <summary>
/// Provides read-only access to a Cartograph artifact together with the file catalog stored in it
/// </summary>
/// <remarks>
/// <para>
/// Opening is constant-time: the header, manifest and record directories are read, the catalog is
/// recovered with a single record read, and no payload byte is touched until a file is actually
/// requested. Because the underlying artifact is mapped read-only, any number of processes may hold
/// a <see cref="CatalogedArtifact" /> over the same file at once and the operating system will back
/// them all with a single physical copy of each page.
/// </para>
/// <para>
/// Instances are safe for concurrent reads from multiple threads.
/// </para>
/// </remarks>
public sealed class CatalogedArtifact : IDisposable {
    /// <summary>
    /// Size of the buffer used when streaming a record into a destination stream
    /// </summary>
    private const int CopyBufferSize = 128 * 1024;

    /// <summary>
    /// The opened artifact whose lifetime this instance owns
    /// </summary>
    private readonly Artifact _artifact;

    /// <summary>
    /// Entries indexed by their relative path, for constant-time lookup by name
    /// </summary>
    private readonly Dictionary<string, CatalogEntry> _byPath;

    /// <summary>
    /// Non-zero once <see cref="Dispose" /> has run
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogedArtifact" /> class
    /// </summary>
    /// <param name="artifact">The opened artifact, whose lifetime this instance takes over</param>
    /// <param name="path">Fully qualified path the artifact was opened from</param>
    /// <param name="catalog">The catalog recovered from record zero</param>
    private CatalogedArtifact(Artifact artifact, string path, FileCatalog catalog) {
        _artifact = artifact;
        Path = path;
        Catalog = catalog;
        SizeOnDisk = new FileInfo(path).Length;

        _byPath = new Dictionary<string, CatalogEntry>(catalog.Entries.Count, StringComparer.Ordinal);

        foreach (CatalogEntry entry in catalog.Entries) {
            // A catalog is written from a directory walk, so duplicate paths should be impossible.
            // Tolerate one anyway rather than throwing while opening a viewer: the first wins.
            _byPath.TryAdd(entry.RelativePath, entry);
        }
    }

    /// <summary>
    /// Gets the fully qualified path the artifact was opened from
    /// </summary>
    /// <value>The path passed to <see cref="Open(string, ArtifactOpenOptions)" />, fully qualified.</value>
    public string Path {
        get;
    }

    /// <summary>
    /// Gets the catalog recovered from record zero of the artifact
    /// </summary>
    /// <value>The manifest describing every packed file.</value>
    public FileCatalog Catalog {
        get;
    }

    /// <summary>
    /// Gets the size of the artifact file on disk, in bytes
    /// </summary>
    /// <value>Includes the header, record directories, alignment padding and the manifest.</value>
    public long SizeOnDisk {
        get;
    }

    /// <summary>
    /// Gets the entries of the catalog, ordered as they were written
    /// </summary>
    /// <value>One entry per packed file.</value>
    public IReadOnlyList<CatalogEntry> Entries => Catalog.Entries;

    /// <summary>
    /// Gets the number of segments in the artifact, including the catalog segment
    /// </summary>
    /// <value>One more than the number of content groups.</value>
    public int SegmentCount => _artifact.Segments.Count;

    /// <summary>
    /// Opens an artifact and recovers the file catalog stored as its first record
    /// </summary>
    /// <param name="path">Path of the artifact to open</param>
    /// <param name="options">Read strategy to use, or <see langword="null" /> for memory mapping</param>
    /// <returns>An open, read-only view over the artifact and its catalog</returns>
    /// <exception cref="System.ArgumentException"><paramref name="path" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.IO.FileNotFoundException">No file exists at <paramref name="path" />.</exception>
    /// <exception cref="System.IO.InvalidDataException">The artifact holds no catalog in record zero.</exception>
    /// <exception cref="Cartograph.Format.CartographFormatException">The file is not a valid Cartograph artifact.</exception>
    public static CatalogedArtifact Open(string path, ArtifactOpenOptions? options = null) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string full = System.IO.Path.GetFullPath(path);

        if (!File.Exists(full)) {
            throw new FileNotFoundException($"Artifact '{full}' does not exist.", full);
        }

        Artifact artifact = Artifact.Open(full, options);

        try {
            if (artifact.Segments.Count == 0 || artifact.Segments[0].RecordCount == 0) {
                throw new InvalidDataException(
                    $"Artifact '{full}' has no record 0, so it cannot carry a file catalog.");
            }

            using RecordLease record = artifact.ReadRecord(0);
            FileCatalog catalog = FileCatalog.Deserialize(record.Sequence);

            return new CatalogedArtifact(artifact, full, catalog);
        } catch {
            artifact.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Finds a catalog entry by its relative path
    /// </summary>
    /// <param name="relativePath">Path relative to the packed root, using forward slashes</param>
    /// <returns>The matching entry, or <see langword="null" /> when the artifact holds no such file</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="relativePath" /> is <see langword="null" />.</exception>
    public CatalogEntry? Find(string relativePath) {
        ArgumentNullException.ThrowIfNull(relativePath);

        return _byPath.GetValueOrDefault(relativePath);
    }

    /// <summary>
    /// Invokes a callback once per record that makes up a catalogued file
    /// </summary>
    /// <param name="entry">Entry whose contents should be walked</param>
    /// <param name="onChunk">
    /// Callback receiving each record's bytes in order. The sequence points directly at mapped pages
    /// and is only valid for the duration of the call, so it must not be captured.
    /// </param>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="onChunk" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ObjectDisposedException">The artifact has already been disposed.</exception>
    /// <exception cref="Cartograph.Format.CartographFormatException">A record failed its checksum check.</exception>
    public void ForEachChunk(CatalogEntry entry, ReadOnlySpanAction<byte, int> onChunk) {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(onChunk);
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int i = 0; i < entry.RecordCount; i++) {
            using RecordLease record = _artifact.ReadRecord(entry.GlobalIndex + i);

            foreach (ReadOnlyMemory<byte> memory in record.Sequence) {
                onChunk(memory.Span, i);
            }
        }
    }

    /// <summary>
    /// Streams the whole contents of a catalogued file into a destination stream
    /// </summary>
    /// <param name="entry">Entry to extract</param>
    /// <param name="destination">Stream that receives the file's bytes</param>
    /// <returns>The number of bytes written</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="destination" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ObjectDisposedException">The artifact has already been disposed.</exception>
    /// <exception cref="Cartograph.Format.CartographFormatException">A record failed its checksum check.</exception>
    public long CopyTo(CatalogEntry entry, Stream destination) {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        long written = 0;

        for (int i = 0; i < entry.RecordCount; i++) {
            using RecordLease record = _artifact.ReadRecord(entry.GlobalIndex + i);

            foreach (ReadOnlyMemory<byte> memory in record.Sequence) {
                // Writing the mapped memory straight through keeps the copy at one hop: mapped page
                // to file-system cache, with nothing proportional to the payload on the managed heap.
                destination.Write(memory.Span);
                written += memory.Length;
            }
        }

        return written;
    }

    /// <summary>
    /// Extracts a catalogued file to a path on disk
    /// </summary>
    /// <param name="entry">Entry to extract</param>
    /// <param name="destinationPath">Path of the file to create or overwrite</param>
    /// <returns>The number of bytes written</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="destinationPath" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.ObjectDisposedException">The artifact has already been disposed.</exception>
    /// <exception cref="System.IO.IOException">The destination could not be written.</exception>
    public long ExtractTo(CatalogEntry entry, string destinationPath) {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destinationPath));

        if (!string.IsNullOrEmpty(directory)) {
            System.IO.Directory.CreateDirectory(directory);
        }

        using FileStream output = new(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.SequentialScan);

        long written = CopyTo(entry, output);

        output.Flush();

        return written;
    }

    /// <summary>
    /// Reads the leading bytes of a catalogued file into a newly allocated array
    /// </summary>
    /// <param name="entry">Entry to read</param>
    /// <param name="maxBytes">Maximum number of bytes to return</param>
    /// <returns>
    /// Up to <paramref name="maxBytes" /> bytes from the start of the file, or fewer when the file is
    /// shorter
    /// </returns>
    /// <remarks>
    /// This is the one deliberately copying operation in the type. It exists so a user interface can
    /// show a bounded preview of a record without materializing a file that may be larger than RAM.
    /// </remarks>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="maxBytes" /> is negative.</exception>
    /// <exception cref="System.ObjectDisposedException">The artifact has already been disposed.</exception>
    public byte[] ReadPrefix(CatalogEntry entry, int maxBytes) {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int wanted = (int)Math.Min(maxBytes, entry.Length);

        if (wanted == 0) {
            return [];
        }

        byte[] buffer = new byte[wanted];
        int filled = 0;

        for (int i = 0; i < entry.RecordCount && filled < wanted; i++) {
            using RecordLease record = _artifact.ReadRecord(entry.GlobalIndex + i);

            foreach (ReadOnlyMemory<byte> memory in record.Sequence) {
                int take = Math.Min(memory.Length, wanted - filled);

                memory.Span[..take].CopyTo(buffer.AsSpan(filled));
                filled += take;

                if (filled == wanted) {
                    break;
                }
            }
        }

        return filled == wanted ? buffer : buffer[..filled];
    }

    /// <summary>
    /// Reads every record of a catalogued file and compares the result with its recorded checksum
    /// </summary>
    /// <param name="entry">Entry to verify</param>
    /// <param name="progress">
    /// Optional receiver of the running byte count, reported once per record so a user interface can
    /// show progress on a large file
    /// </param>
    /// <param name="cancellationToken">Token observed between records</param>
    /// <returns>The outcome of the comparison</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ObjectDisposedException">The artifact has already been disposed.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken" /> was signalled.</exception>
    /// <exception cref="Cartograph.Format.CartographFormatException">A record failed its own checksum check.</exception>
    public CatalogVerification Verify(
        CatalogEntry entry,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        XxHash3 hasher = new();
        long read = 0;

        for (int i = 0; i < entry.RecordCount; i++) {
            cancellationToken.ThrowIfCancellationRequested();

            using RecordLease record = _artifact.ReadRecord(entry.GlobalIndex + i);

            foreach (ReadOnlyMemory<byte> memory in record.Sequence) {
                hasher.Append(memory.Span);
                read += memory.Length;
            }

            progress?.Report(read);
        }

        return new CatalogVerification(
            entry,
            read,
            hasher.GetCurrentHashAsUInt64(),
            entry.Checksum,
            System.Diagnostics.Stopwatch.GetElapsedTime(start));
    }

    /// <summary>
    /// Releases the mapping and the underlying file handle
    /// </summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        _artifact.Dispose();
    }
}
