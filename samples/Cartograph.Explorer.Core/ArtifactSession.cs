// ============================================================================
// Cartograph
// File: ArtifactSession.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// User-interface-agnostic session over one Cartograph artifact, combining the
// catalog reader, the cross-process presence registry and the process
// footprint counters that the Windows and Linux explorers both render
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

using System.Diagnostics;
using Cartograph.Catalog;
using Cartograph.Format;

namespace Cartograph.Explorer.Core;

/// <summary>
/// A point-in-time reading of how much memory the current process is holding
/// </summary>
/// <param name="WorkingSetBytes">Resident set of the process, including file-backed pages the artifact mapped in.</param>
/// <param name="ManagedHeapBytes">Bytes the garbage collector currently considers allocated.</param>
/// <param name="ArtifactBytesTouched">Bytes of the artifact this session has read since it was opened.</param>
public readonly record struct ProcessFootprint(
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long ArtifactBytesTouched);

/// <summary>
/// Owns everything one explorer window needs in order to work with a single artifact
/// </summary>
/// <remarks>
/// <para>
/// The type exists so that the Windows Forms and GTK front ends share one behaviour: they differ only
/// in how they draw. Every operation a user can invoke - previewing a record, extracting it,
/// verifying it, watching peer processes - is implemented once here.
/// </para>
/// <para>
/// A session opens the artifact read-only, which is what allows any number of processes to run
/// against the same file at once. Because the pages are clean and file-backed, the operating system
/// serves all of them from a single physical copy, and the footprint reported by
/// <see cref="Footprint" /> stays flat no matter how large the artifact is.
/// </para>
/// </remarks>
public sealed class ArtifactSession : IDisposable
{
    /// <summary>
    /// The artifact and its catalog
    /// </summary>
    private readonly CatalogedArtifact _artifact;

    /// <summary>
    /// Presence registry that makes sibling processes visible
    /// </summary>
    private readonly InstancePresence _presence;

    /// <summary>
    /// Running total of artifact bytes this session has read
    /// </summary>
    private long _bytesTouched;

    /// <summary>
    /// Non-zero once <see cref="Dispose" /> has run
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtifactSession" /> class
    /// </summary>
    /// <param name="artifact">The opened artifact, whose lifetime the session takes over</param>
    /// <param name="presence">The presence registration, whose lifetime the session takes over</param>
    /// <param name="chunkSource">The read strategy the artifact was opened with</param>
    /// <param name="openElapsed">Wall-clock time the open took</param>
    private ArtifactSession(
        CatalogedArtifact artifact,
        InstancePresence presence,
        ChunkSourceKind chunkSource,
        TimeSpan openElapsed)
    {
        _artifact = artifact;
        _presence = presence;
        ChunkSource = chunkSource;
        OpenElapsed = openElapsed;
    }

    /// <summary>
    /// Gets the fully qualified path of the open artifact
    /// </summary>
    /// <value>The path the session was opened from.</value>
    public string Path => _artifact.Path;

    /// <summary>
    /// Gets the size of the artifact on disk, in bytes
    /// </summary>
    /// <value>The whole file, including the header, directories, padding and manifest.</value>
    public long SizeOnDisk => _artifact.SizeOnDisk;

    /// <summary>
    /// Gets the catalog recovered from the artifact
    /// </summary>
    /// <value>The manifest of every packed file.</value>
    public FileCatalog Catalog => _artifact.Catalog;

    /// <summary>
    /// Gets every catalogued file, in catalog order
    /// </summary>
    /// <value>One entry per packed file.</value>
    public IReadOnlyList<CatalogEntry> Entries => _artifact.Entries;

    /// <summary>
    /// Gets the number of segments in the artifact, including the catalog segment
    /// </summary>
    /// <value>One more than the number of content groups.</value>
    public int SegmentCount => _artifact.SegmentCount;

    /// <summary>
    /// Gets the read strategy the artifact was opened with
    /// </summary>
    /// <value>Either memory mapping or pooled positional reads.</value>
    public ChunkSourceKind ChunkSource { get; }

    /// <summary>
    /// Gets the time the open took
    /// </summary>
    /// <value>
    /// Expected to be effectively constant regardless of artifact size, because opening reads only the
    /// header, the manifest, the record directories and the catalog record.
    /// </value>
    public TimeSpan OpenElapsed { get; }

    /// <summary>
    /// Gets the number of artifact bytes this session has read since it was opened
    /// </summary>
    /// <value>Increases as records are previewed, extracted or verified.</value>
    public long BytesTouched => Interlocked.Read(ref _bytesTouched);

    /// <summary>
    /// Gets the memory the current process is holding right now
    /// </summary>
    /// <value>A freshly sampled footprint.</value>
    public ProcessFootprint Footprint
    {
        get
        {
            using Process self = Process.GetCurrentProcess();
            self.Refresh();

            return new ProcessFootprint(
                self.WorkingSet64,
                GC.GetTotalMemory(forceFullCollection: false),
                BytesTouched);
        }
    }

    /// <summary>
    /// Opens an artifact and registers this process as one of its readers
    /// </summary>
    /// <param name="path">Path of the artifact to open</param>
    /// <param name="frontEnd">Short name of the user interface, used only for peer display</param>
    /// <param name="chunkSource">Read strategy to use</param>
    /// <returns>An open session</returns>
    /// <exception cref="System.ArgumentException"><paramref name="path" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="frontEnd" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.IO.FileNotFoundException">No file exists at <paramref name="path" />.</exception>
    /// <exception cref="System.IO.InvalidDataException">The artifact carries no file catalog.</exception>
    /// <exception cref="Cartograph.Format.CartographFormatException">The file is not a valid Cartograph artifact.</exception>
    public static ArtifactSession Open(
        string path,
        string frontEnd,
        ChunkSourceKind chunkSource = ChunkSourceKind.Mapped)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(frontEnd);

        long start = Stopwatch.GetTimestamp();

        CatalogedArtifact artifact = CatalogedArtifact.Open(
            path,
            new ArtifactOpenOptions { ChunkSource = chunkSource });

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        try
        {
            InstancePresence presence = new(artifact.Path, frontEnd);

            return new ArtifactSession(artifact, presence, chunkSource, elapsed);
        }
        catch
        {
            artifact.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Selects the catalog entries whose relative path contains a search term
    /// </summary>
    /// <param name="query">
    /// Term to match, compared without regard to case, or <see langword="null" /> or whitespace to
    /// select everything
    /// </param>
    /// <returns>The matching entries, in catalog order</returns>
    /// <exception cref="System.ObjectDisposedException">The session has already been disposed.</exception>
    public IReadOnlyList<CatalogEntry> Filter(string? query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(query))
        {
            return Entries;
        }

        string term = query.Trim();
        List<CatalogEntry> matches = [];

        foreach (CatalogEntry entry in Entries)
        {
            if (entry.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(entry);
            }
        }

        return matches;
    }

    /// <summary>
    /// Builds a bounded preview of a catalogued file
    /// </summary>
    /// <param name="entry">Entry to preview</param>
    /// <param name="prefixBytes">Maximum number of bytes to read from the start of the file</param>
    /// <returns>A render-ready preview</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ObjectDisposedException">The session has already been disposed.</exception>
    /// <exception cref="Cartograph.Format.CartographFormatException">A record failed its checksum check.</exception>
    public RecordPreview Preview(CatalogEntry entry, int prefixBytes = RecordPreview.DefaultPrefixBytes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] prefix = _artifact.ReadPrefix(entry, prefixBytes);

        Interlocked.Add(ref _bytesTouched, prefix.Length);

        return RecordPreview.Create(prefix, entry.Length);
    }

    /// <summary>
    /// Reads every record of a catalogued file and compares it with its recorded checksum
    /// </summary>
    /// <param name="entry">Entry to verify</param>
    /// <param name="progress">Optional receiver of the running byte count</param>
    /// <param name="cancellationToken">Token observed between records</param>
    /// <returns>The outcome of the comparison</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ObjectDisposedException">The session has already been disposed.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken" /> was signalled.</exception>
    /// <exception cref="Cartograph.Format.CartographFormatException">A record failed its own checksum check.</exception>
    public CatalogVerification Verify(
        CatalogEntry entry,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        CatalogVerification result = _artifact.Verify(entry, progress, cancellationToken);

        Interlocked.Add(ref _bytesTouched, result.BytesRead);

        return result;
    }

    /// <summary>
    /// Extracts a catalogued file to a path on disk
    /// </summary>
    /// <param name="entry">Entry to extract</param>
    /// <param name="destinationPath">Path of the file to create or overwrite</param>
    /// <returns>The number of bytes written</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="destinationPath" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.ObjectDisposedException">The session has already been disposed.</exception>
    /// <exception cref="System.IO.IOException">The destination could not be written.</exception>
    public long Extract(CatalogEntry entry, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        long written = _artifact.ExtractTo(entry, destinationPath);

        Interlocked.Add(ref _bytesTouched, written);

        return written;
    }

    /// <summary>
    /// Extracts every catalogued file into a destination folder, preserving relative paths
    /// </summary>
    /// <param name="entries">Entries to extract</param>
    /// <param name="destinationRoot">Folder that receives the reconstructed tree</param>
    /// <param name="progress">Optional receiver of the running file count</param>
    /// <param name="cancellationToken">Token observed between files</param>
    /// <returns>The number of bytes written across every file</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="entries" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="destinationRoot" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.ObjectDisposedException">The session has already been disposed.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken" /> was signalled.</exception>
    /// <exception cref="System.IO.IOException">A destination file could not be written.</exception>
    public long ExtractAll(
        IEnumerable<CatalogEntry> entries,
        string destinationRoot,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(destinationRoot);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string root = System.IO.Path.GetFullPath(destinationRoot);
        long written = 0;
        int done = 0;

        foreach (CatalogEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string destination = ResolveWithin(root, entry.RelativePath);

            written += _artifact.ExtractTo(entry, destination);
            progress?.Report(++done);
        }

        Interlocked.Add(ref _bytesTouched, written);

        return written;
    }

    /// <summary>
    /// Refreshes this instance's heartbeat and returns every process sharing the artifact
    /// </summary>
    /// <returns>The live peers, including this process</returns>
    /// <exception cref="System.ObjectDisposedException">The session has already been disposed.</exception>
    public IReadOnlyList<PeerInstance> RefreshPeers()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _presence.Refresh(BytesTouched);
    }

    /// <summary>
    /// Resolves a catalogued relative path against a destination root, refusing to escape it
    /// </summary>
    /// <param name="root">Fully qualified destination folder</param>
    /// <param name="relativePath">Relative path taken from the catalog</param>
    /// <returns>The fully qualified destination path</returns>
    /// <remarks>
    /// A catalog is data, and data from an untrusted artifact could carry <c>..</c> segments or an
    /// absolute path. Resolving and then re-checking the prefix is what stops a crafted artifact from
    /// writing outside the folder the user chose.
    /// </remarks>
    /// <exception cref="System.IO.IOException">The entry resolves outside <paramref name="root" />.</exception>
    private static string ResolveWithin(string root, string relativePath)
    {
        string combined = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        string prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new IOException(
                $"Catalog entry '{relativePath}' resolves outside the destination folder and was refused.");
        }

        return combined;
    }

    /// <summary>
    /// Closes the artifact and withdraws this process from the presence registry
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _presence.Dispose();
        _artifact.Dispose();
    }
}
