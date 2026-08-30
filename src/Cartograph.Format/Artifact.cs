// ============================================================================
// Cartograph
// File: Artifact.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Represents an opened Cartograph artifact, providing O(1) open, bounds-validated
// segment enumeration, and zero-copy record reads via a pluggable IChunkSource.
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
using System.Buffers.Binary;
using System.IO.Hashing;

namespace Cartograph.Format;

/// <summary>
/// Opens a Cartograph artifact and enumerates its records as zero-copy
/// <see cref="ReadOnlySequence{Byte}"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Opening is O(1) with respect to file size: the header and (small) manifest are read and
/// validated, then the payload region is exposed through the chosen <see cref="IChunkSource"/>. No
/// record payload is touched until it is read, and mapped payloads never enter the managed heap.
/// </para>
/// <para>
/// Every offset read from the file is bounds-validated against the file and its segments, so a
/// truncated or malformed artifact raises <see cref="CartographFormatException"/> rather than
/// performing an out-of-bounds read.
/// </para>
/// </remarks>
public sealed class Artifact : IDisposable
{
    /// <summary>The backing chunk source used to read record payloads.</summary>
    private readonly IChunkSource _source;

    /// <summary>Whether <see cref="Dispose"/> should dispose <see cref="_source"/>.</summary>
    private readonly bool _ownsSource;

    /// <summary>Non-zero once <see cref="Dispose"/> has been called.</summary>
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Artifact" /> class.
    /// </summary>
    /// <param name="source">The chunk source that provides access to the artifact's raw bytes.</param>
    /// <param name="header">The validated file header read from the artifact.</param>
    /// <param name="sourceKind">The chunk source strategy that was selected when opening.</param>
    /// <param name="segments">The ordered list of live segments parsed from the manifest.</param>
    /// <param name="ownsSource">Whether disposing this artifact should also dispose <paramref name="source"/>.</param>
    private Artifact(IChunkSource source, ArtifactHeader header, ChunkSourceKind sourceKind, IReadOnlyList<ArtifactSegment> segments, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        Header = header;
        SourceKind = sourceKind;
        Segments = segments;
    }

    /// <summary>The validated file header.</summary>
    public ArtifactHeader Header { get; }

    /// <summary>The chunk source strategy in use.</summary>
    public ChunkSourceKind SourceKind { get; }

    /// <summary>The live segments in the artifact, in manifest order.</summary>
    public IReadOnlyList<ArtifactSegment> Segments { get; }

    /// <summary>The total number of records across all live segments.</summary>
    public long RecordCount
    {
        get
        {
            long total = 0;
            foreach (ArtifactSegment segment in Segments)
            {
                total += segment.RecordCount;
            }

            return total;
        }
    }

    /// <summary>Opens the artifact at <paramref name="path"/> using <paramref name="options"/>.</summary>
    /// <param name="path">The path to the artifact file to open.</param>
    /// <param name="options">Options controlling the chunk source and checksum behavior; defaults to <see cref="ArtifactOpenOptions.Default"/>.</param>
    /// <returns>A fully validated, open <see cref="Artifact"/> ready for record reads.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="path"/> is <c>null</c> or empty.</exception>
    /// <exception cref="CartographFormatException">The file is not a valid, self-consistent artifact (bad magic, wrong version,
    /// endianness mismatch, header checksum failure, truncation, manifest corruption, or segment/record bounds violation).</exception>
    public static Artifact Open(string path, ArtifactOpenOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= ArtifactOpenOptions.Default;
        return Open(CreateSource(path, options), options, ownsSource: true);
    }

    /// <summary>
    /// Opens an artifact backed by a caller-supplied <see cref="IChunkSource"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the extension point for storing artifacts somewhere other than the local file system:
    /// implement <see cref="IChunkSource"/> over HTTP range requests, object storage, an encrypted
    /// container, or an in-memory buffer, and pass it here. Opening remains O(1) with respect to
    /// artifact size — only the header, the manifest, and each segment's record directory are read.
    /// </para>
    /// <para>
    /// The source must satisfy the contract documented on <see cref="IChunkSource"/>. Structural
    /// validation and per-record checksum verification still apply, so a misbehaving source produces
    /// a <see cref="CartographFormatException"/> rather than corrupt reads.
    /// </para>
    /// </remarks>
    /// <param name="source">The chunk source providing the artifact's raw bytes.</param>
    /// <param name="options">Options controlling checksum behavior; defaults to <see cref="ArtifactOpenOptions.Default"/>.
    /// <see cref="ArtifactOpenOptions.ChunkSource"/> and <see cref="ArtifactOpenOptions.WindowSize"/> are ignored here.</param>
    /// <param name="ownsSource"><see langword="true"/> (the default) to dispose <paramref name="source"/> when this artifact is
    /// disposed; <see langword="false"/> to leave its lifetime to the caller.</param>
    /// <returns>A fully validated, open <see cref="Artifact"/> ready for record reads.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
    /// <exception cref="CartographFormatException">The bytes are not a valid, self-consistent artifact (bad magic, wrong version,
    /// endianness mismatch, header checksum failure, truncation, manifest corruption, or segment/record bounds violation).</exception>
    public static Artifact Open(IChunkSource source, ArtifactOpenOptions? options = null, bool ownsSource = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= ArtifactOpenOptions.Default;

        try
        {
            long sourceLength = source.Length;
            ArtifactHeader header = ReadHeader(source, sourceLength);
            SegmentManifest manifest = SegmentManifest.Read(ReadManifestBytes(source, header, sourceLength));

            List<ArtifactSegment> segments = [];
            foreach (SegmentDescriptor descriptor in manifest.Segments)
            {
                if (!descriptor.IsLive)
                {
                    continue;
                }

                ValidateSegment(descriptor, sourceLength);
                (int count, int directorySize) = DirectoryGeometry(descriptor);
                RecordDirectory directory;
                if (count == 0)
                {
                    directory = EmptyDirectory();
                }
                else
                {
                    using ChunkLease chunk = source.Read((long)descriptor.DirectoryOffset, directorySize);
                    directory = ParseDirectory(chunk.Sequence, descriptor, count, directorySize);
                }

                segments.Add(new ArtifactSegment(source, descriptor, directory, options.VerifyChecksums));
            }

            return new Artifact(source, header, DetermineSourceKind(source), segments, ownsSource);
        }
        catch
        {
            if (ownsSource)
            {
                source.Dispose();
            }

            throw;
        }
    }

    /// <summary>Asynchronously opens the artifact at <paramref name="path"/> using <paramref name="options"/>.</summary>
    /// <param name="path">The path to the artifact file to open.</param>
    /// <param name="options">Options controlling the chunk source and checksum behavior; defaults to <see cref="ArtifactOpenOptions.Default"/>.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>A task that resolves to a fully validated, open <see cref="Artifact"/> ready for record reads.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="path"/> is <c>null</c> or empty.</exception>
    /// <exception cref="CartographFormatException">The file is not a valid, self-consistent artifact.</exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    public static ValueTask<Artifact> OpenAsync(string path, ArtifactOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= ArtifactOpenOptions.Default;
        return OpenAsync(CreateSource(path, options), options, ownsSource: true, cancellationToken);
    }

    /// <summary>
    /// Asynchronously opens an artifact backed by a caller-supplied <see cref="IChunkSource"/>.
    /// </summary>
    /// <remarks>
    /// Prefer this overload for sources whose reads cross a network. Every read performed while
    /// opening — header, manifest, and each segment's record directory — goes through
    /// <see cref="IChunkSource.ReadAsync"/>, so no blocking wait is imposed on a remote source.
    /// </remarks>
    /// <param name="source">The chunk source providing the artifact's raw bytes.</param>
    /// <param name="options">Options controlling checksum behavior; defaults to <see cref="ArtifactOpenOptions.Default"/>.</param>
    /// <param name="ownsSource"><see langword="true"/> (the default) to dispose <paramref name="source"/> when this artifact is
    /// disposed; <see langword="false"/> to leave its lifetime to the caller.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>A task that resolves to a fully validated, open <see cref="Artifact"/> ready for record reads.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
    /// <exception cref="CartographFormatException">The bytes are not a valid, self-consistent artifact.</exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    public static async ValueTask<Artifact> OpenAsync(
        IChunkSource source,
        ArtifactOpenOptions? options = null,
        bool ownsSource = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= ArtifactOpenOptions.Default;

        try
        {
            long sourceLength = source.Length;
            ArtifactHeader header = await ReadHeaderAsync(source, sourceLength, cancellationToken).ConfigureAwait(false);
            SegmentManifest manifest = SegmentManifest.Read(
                await ReadManifestBytesAsync(source, header, sourceLength, cancellationToken).ConfigureAwait(false));

            List<ArtifactSegment> segments = [];
            foreach (SegmentDescriptor descriptor in manifest.Segments)
            {
                if (!descriptor.IsLive)
                {
                    continue;
                }

                ValidateSegment(descriptor, sourceLength);
                (int count, int directorySize) = DirectoryGeometry(descriptor);
                RecordDirectory directory;
                if (count == 0)
                {
                    directory = EmptyDirectory();
                }
                else
                {
                    using ChunkLease chunk = await source
                        .ReadAsync((long)descriptor.DirectoryOffset, directorySize, cancellationToken)
                        .ConfigureAwait(false);
                    directory = ParseDirectory(chunk.Sequence, descriptor, count, directorySize);
                }

                segments.Add(new ArtifactSegment(source, descriptor, directory, options.VerifyChecksums));
            }

            return new Artifact(source, header, DetermineSourceKind(source), segments, ownsSource);
        }
        catch
        {
            if (ownsSource)
            {
                source.Dispose();
            }

            throw;
        }
    }

    /// <summary>Reads the record at the given global index across all live segments.</summary>
    /// <param name="globalIndex">The zero-based global record index spanning all live segments.</param>
    /// <returns>A <see cref="RecordLease"/> holding the record bytes; the caller must dispose it.</returns>
    /// <exception cref="System.ObjectDisposedException">The <see cref="Artifact"/> instance has been disposed.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="globalIndex"/> is negative or exceeds the total record count.</exception>
    public RecordLease ReadRecord(long globalIndex)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (globalIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(globalIndex));
        }

        long remaining = globalIndex;
        foreach (ArtifactSegment segment in Segments)
        {
            if (remaining < segment.RecordCount)
            {
                return segment.ReadRecord((int)remaining);
            }

            remaining -= segment.RecordCount;
        }

        throw new ArgumentOutOfRangeException(nameof(globalIndex));
    }

    /// <summary>Asynchronously reads the record at the given global index across all live segments.</summary>
    /// <remarks>Prefer this over <see cref="ReadRecord"/> when the backing <see cref="IChunkSource"/> performs network or other high-latency I/O.</remarks>
    /// <param name="globalIndex">The zero-based global record index spanning all live segments.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>A task that resolves to a <see cref="RecordLease"/> holding the record bytes; the caller must dispose it.</returns>
    /// <exception cref="System.ObjectDisposedException">The <see cref="Artifact"/> instance has been disposed.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="globalIndex"/> is negative or exceeds the total record count.</exception>
    /// <exception cref="CartographFormatException">The record's checksum does not match, when verification is enabled.</exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    public ValueTask<RecordLease> ReadRecordAsync(long globalIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (globalIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(globalIndex));
        }

        long remaining = globalIndex;
        foreach (ArtifactSegment segment in Segments)
        {
            if (remaining < segment.RecordCount)
            {
                return segment.ReadRecordAsync((int)remaining, cancellationToken);
            }

            remaining -= segment.RecordCount;
        }

        throw new ArgumentOutOfRangeException(nameof(globalIndex));
    }

    /// <summary>
    /// Releases the artifact. The backing <see cref="IChunkSource"/> is disposed only when this
    /// artifact owns it — that is, unless it was opened with <c>ownsSource: false</c>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsSource)
        {
            _source.Dispose();
        }
    }

    /// <summary>Creates one of the built-in chunk sources for a local file path.</summary>
    /// <param name="path">The path to the artifact file.</param>
    /// <param name="options">Options selecting the strategy and, for mapped sources, the window size.</param>
    /// <returns>An open <see cref="IChunkSource"/> over <paramref name="path"/>.</returns>
    private static IChunkSource CreateSource(string path, ArtifactOpenOptions options)
        => options.ChunkSource == ChunkSourceKind.RandomAccess
            ? RandomAccessChunkSource.Open(path)
            : MappedChunkSource.Open(path, options.WindowSize);

    /// <summary>Reports which strategy a chunk source represents, classifying anything unrecognized as custom.</summary>
    /// <param name="source">The chunk source to classify.</param>
    /// <returns>The matching <see cref="ChunkSourceKind"/>, or <see cref="ChunkSourceKind.Custom"/> for caller-supplied sources.</returns>
    private static ChunkSourceKind DetermineSourceKind(IChunkSource source) => source switch
    {
        MappedChunkSource => ChunkSourceKind.Mapped,
        RandomAccessChunkSource => ChunkSourceKind.RandomAccess,
        _ => ChunkSourceKind.Custom,
    };

    /// <summary>Reads and validates the fixed header from the start of the source.</summary>
    /// <param name="source">The chunk source to read from.</param>
    /// <param name="sourceLength">The total length reported by the source.</param>
    /// <returns>The validated <see cref="ArtifactHeader"/>.</returns>
    /// <exception cref="CartographFormatException">The source is smaller than the fixed header, or the header is invalid or declares more content than the source holds.</exception>
    private static ArtifactHeader ReadHeader(IChunkSource source, long sourceLength)
    {
        EnsureHeaderFits(sourceLength);
        Span<byte> headerBytes = stackalloc byte[ArtifactFormat.HeaderSize];
        using (ChunkLease chunk = source.Read(0, ArtifactFormat.HeaderSize))
        {
            CopyExact(chunk.Sequence, headerBytes);
        }

        ArtifactHeader header = ArtifactHeader.Read(headerBytes);
        ValidateContentLength(header, sourceLength);
        return header;
    }

    /// <summary>Asynchronously reads and validates the fixed header from the start of the source.</summary>
    /// <param name="source">The chunk source to read from.</param>
    /// <param name="sourceLength">The total length reported by the source.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>A task that resolves to the validated <see cref="ArtifactHeader"/>.</returns>
    /// <exception cref="CartographFormatException">The source is smaller than the fixed header, or the header is invalid or declares more content than the source holds.</exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    private static async ValueTask<ArtifactHeader> ReadHeaderAsync(IChunkSource source, long sourceLength, CancellationToken cancellationToken)
    {
        EnsureHeaderFits(sourceLength);
        byte[] headerBytes = new byte[ArtifactFormat.HeaderSize];
        using (ChunkLease chunk = await source.ReadAsync(0, ArtifactFormat.HeaderSize, cancellationToken).ConfigureAwait(false))
        {
            CopyExact(chunk.Sequence, headerBytes);
        }

        ArtifactHeader header = ArtifactHeader.Read(headerBytes);
        ValidateContentLength(header, sourceLength);
        return header;
    }

    /// <summary>Reads the manifest region described by the header.</summary>
    /// <param name="source">The chunk source to read from.</param>
    /// <param name="header">The validated header naming the manifest region.</param>
    /// <param name="sourceLength">The total length reported by the source.</param>
    /// <returns>The raw manifest bytes.</returns>
    /// <exception cref="CartographFormatException">Manifest offset/length falls outside the file.</exception>
    private static byte[] ReadManifestBytes(IChunkSource source, in ArtifactHeader header, long sourceLength)
    {
        int manifestLength = ValidateManifestRegion(header, sourceLength);
        byte[] manifestBytes = new byte[manifestLength];
        using ChunkLease chunk = source.Read((long)header.ManifestOffset, manifestLength);
        CopyExact(chunk.Sequence, manifestBytes);
        return manifestBytes;
    }

    /// <summary>Asynchronously reads the manifest region described by the header.</summary>
    /// <param name="source">The chunk source to read from.</param>
    /// <param name="header">The validated header naming the manifest region.</param>
    /// <param name="sourceLength">The total length reported by the source.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>A task that resolves to the raw manifest bytes.</returns>
    /// <exception cref="CartographFormatException">Manifest offset/length falls outside the file.</exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    private static async ValueTask<byte[]> ReadManifestBytesAsync(IChunkSource source, ArtifactHeader header, long sourceLength, CancellationToken cancellationToken)
    {
        int manifestLength = ValidateManifestRegion(header, sourceLength);
        byte[] manifestBytes = new byte[manifestLength];
        using ChunkLease chunk = await source
            .ReadAsync((long)header.ManifestOffset, manifestLength, cancellationToken)
            .ConfigureAwait(false);
        CopyExact(chunk.Sequence, manifestBytes);
        return manifestBytes;
    }

    /// <summary>Throws when the source cannot even contain the fixed header.</summary>
    /// <param name="sourceLength">The total length reported by the source.</param>
    /// <exception cref="CartographFormatException">File is smaller than the fixed header.</exception>
    private static void EnsureHeaderFits(long sourceLength)
    {
        if (sourceLength < ArtifactFormat.HeaderSize)
        {
            throw new CartographFormatException("File is smaller than the fixed header.");
        }
    }

    /// <summary>Verifies the header does not declare more content than the source actually holds.</summary>
    /// <param name="header">The header to check.</param>
    /// <param name="sourceLength">The total length reported by the source.</param>
    /// <exception cref="CartographFormatException">Artifact is truncated: the header declares more bytes than the source holds.</exception>
    private static void ValidateContentLength(in ArtifactHeader header, long sourceLength)
    {
        if ((long)header.ContentLength > sourceLength)
        {
            throw new CartographFormatException(
                $"Artifact is truncated: header declares {header.ContentLength} bytes but the file is {sourceLength}.");
        }
    }

    /// <summary>Bounds-checks the manifest region named by the header.</summary>
    /// <param name="header">The header naming the manifest region.</param>
    /// <param name="sourceLength">The total length reported by the source.</param>
    /// <returns>The manifest length in bytes, narrowed to <see cref="int"/>.</returns>
    /// <exception cref="CartographFormatException">Manifest offset/length falls outside the file.</exception>
    private static int ValidateManifestRegion(in ArtifactHeader header, long sourceLength)
    {
        long manifestOffset = (long)header.ManifestOffset;
        long manifestLength = (long)header.ManifestLength;
        if (manifestOffset < ArtifactFormat.HeaderSize
            || manifestLength < ArtifactFormat.ManifestHeaderSize
            || manifestLength > int.MaxValue
            || manifestOffset + manifestLength > sourceLength)
        {
            throw new CartographFormatException("Manifest offset/length falls outside the file.");
        }

        return (int)manifestLength;
    }

    /// <summary>Computes the record count and directory byte size for a segment.</summary>
    /// <param name="descriptor">The segment descriptor.</param>
    /// <returns>The record count and the directory region size in bytes.</returns>
    /// <exception cref="CartographFormatException">Record directory is too large to read.</exception>
    private static (int Count, int DirectorySize) DirectoryGeometry(in SegmentDescriptor descriptor)
    {
        int count = checked((int)descriptor.RecordCount);
        long directorySize = (long)count * ArtifactFormat.RecordEntrySize;
        if (directorySize > int.MaxValue)
        {
            throw new CartographFormatException("Record directory is too large to read.");
        }

        return (count, (int)directorySize);
    }

    /// <summary>Creates the directory used for a segment that holds no records.</summary>
    /// <returns>An empty <see cref="RecordDirectory"/>.</returns>
    private static RecordDirectory EmptyDirectory() => new([], [], []);

    /// <summary>
    /// Parses a segment's record directory, validating each entry's offset and length against the segment bounds.
    /// </summary>
    /// <param name="sequence">The raw directory bytes.</param>
    /// <param name="descriptor">The segment descriptor identifying the directory region.</param>
    /// <param name="count">The number of record entries to parse.</param>
    /// <param name="directorySize">The directory region size in bytes.</param>
    /// <returns>A <see cref="RecordDirectory"/> containing the parsed per-record offsets, lengths, and checksums.</returns>
    /// <exception cref="CartographFormatException">The directory region was short, or a record offset/length falls outside its segment.</exception>
    private static RecordDirectory ParseDirectory(in ReadOnlySequence<byte> sequence, in SegmentDescriptor descriptor, int count, int directorySize)
    {
        long[] relOffsets = new long[count];
        long[] lengths = new long[count];
        ulong[] checksums = new ulong[count];

        byte[] bytes = ArrayPool<byte>.Shared.Rent(directorySize);
        try
        {
            CopyExact(sequence, bytes.AsSpan(0, directorySize));
            ReadOnlySpan<byte> span = bytes.AsSpan(0, directorySize);

            // The payload region may sit before or after the directory within the segment. When it
            // comes first, records must stop at the directory rather than at the segment end.
            long payloadOffset = (long)descriptor.PayloadOffset;
            long directoryOffset = (long)descriptor.DirectoryOffset;
            long segmentEnd = (long)descriptor.DataOffset + (long)descriptor.DataLength;
            long payloadLimit = payloadOffset < directoryOffset ? directoryOffset : segmentEnd;

            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> entry = span.Slice(i * ArtifactFormat.RecordEntrySize, ArtifactFormat.RecordEntrySize);
                long relOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[0..]);
                long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
                ulong checksum = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);

                if (length > ArtifactFormat.MaxRecordLength)
                {
                    throw new CartographFormatException(
                        $"Record {i} is {length} bytes, which exceeds the {ArtifactFormat.MaxRecordLength}-byte " +
                        "maximum a single record can be read as.");
                }

                long absolute = payloadOffset + relOffset;
                if (relOffset < 0 || length < 0 || absolute < payloadOffset || absolute + length > payloadLimit)
                {
                    throw new CartographFormatException($"Record {i} offset/length falls outside its segment.");
                }

                relOffsets[i] = relOffset;
                lengths[i] = length;
                checksums[i] = checksum;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }

        return new RecordDirectory(relOffsets, lengths, checksums);
    }

    /// <summary>
    /// Copies a chunk into <paramref name="destination"/>, enforcing the <see cref="IChunkSource"/>
    /// contract that a read returns exactly the requested number of bytes.
    /// </summary>
    /// <param name="sequence">The sequence returned by the chunk source.</param>
    /// <param name="destination">The span to fill.</param>
    /// <exception cref="CartographFormatException">Artifact is truncated: unexpected end of file.</exception>
    private static void CopyExact(in ReadOnlySequence<byte> sequence, Span<byte> destination)
    {
        if (sequence.Length != destination.Length)
        {
            throw new CartographFormatException("Artifact is truncated: unexpected end of file.");
        }

        sequence.CopyTo(destination);
    }

    /// <summary>
    /// Computes the XxHash3 checksum of a <see cref="ReadOnlySequence{Byte}"/>, handling multi-segment sequences.
    /// </summary>
    /// <param name="sequence">The byte sequence to hash.</param>
    /// <returns>The XxHash3 hash value as a <see cref="ulong"/>.</returns>
    internal static ulong HashSequence(ReadOnlySequence<byte> sequence)
    {
        XxHash3 hasher = new();
        foreach (ReadOnlyMemory<byte> segment in sequence)
        {
            hasher.Append(segment.Span);
        }

        return hasher.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// Validates that all region offsets within a <see cref="SegmentDescriptor"/> are self-consistent
    /// and fall entirely within the file.
    /// </summary>
    /// <remarks>
    /// A segment stores its record directory and its payload region at independent offsets, so either
    /// may come first. Buffered segments are written directory-first; segments containing streamed
    /// records are written payload-first, which lets the writer checksum each record in the same pass
    /// that emits it. Both orders are accepted here, provided the two regions stay inside the segment
    /// and do not overlap.
    /// </remarks>
    /// <param name="descriptor">The segment descriptor to validate.</param>
    /// <param name="fileLength">The total length of the artifact file in bytes.</param>
    /// <exception cref="CartographFormatException">Segment data region falls outside the file.</exception>
    /// <exception cref="CartographFormatException">Segment record directory falls outside the segment.</exception>
    /// <exception cref="CartographFormatException">Segment payload region falls outside the segment.</exception>
    private static void ValidateSegment(in SegmentDescriptor descriptor, long fileLength)
    {
        long dataOffset = (long)descriptor.DataOffset;
        long dataLength = (long)descriptor.DataLength;
        long directoryOffset = (long)descriptor.DirectoryOffset;
        long payloadOffset = (long)descriptor.PayloadOffset;

        if (dataOffset < ArtifactFormat.HeaderSize || dataLength < 0 || dataOffset + dataLength > fileLength)
        {
            throw new CartographFormatException("Segment data region falls outside the file.");
        }

        long dataEnd = dataOffset + dataLength;
        long directorySize = (long)descriptor.RecordCount * ArtifactFormat.RecordEntrySize;
        if (directoryOffset < dataOffset || directoryOffset + directorySize > dataEnd)
        {
            throw new CartographFormatException("Segment record directory falls outside the segment.");
        }

        if (payloadOffset < dataOffset || payloadOffset > dataEnd)
        {
            throw new CartographFormatException("Segment payload region falls outside the segment.");
        }

        // Payload-first segments are bounded against the directory when each record is parsed;
        // directory-first segments must clear the directory outright.
        if (payloadOffset >= directoryOffset && payloadOffset < directoryOffset + directorySize)
        {
            throw new CartographFormatException("Segment payload region overlaps its record directory.");
        }
    }
}

/// <summary>
/// Holds the per-record relative offsets, byte lengths, and XxHash3 checksums for all records in a segment.
/// </summary>
internal sealed class RecordDirectory(long[] relOffsets, long[] lengths, ulong[] checksums)
{
    /// <summary>The payload-relative byte offset of each record, indexed by record position.</summary>
    public long[] RelOffsets { get; } = relOffsets;

    /// <summary>The byte length of each record, indexed by record position.</summary>
    public long[] Lengths { get; } = lengths;

    /// <summary>The XxHash3 checksum of each record's bytes, indexed by record position.</summary>
    public ulong[] Checksums { get; } = checksums;
}
