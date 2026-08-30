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
using Microsoft.Win32.SafeHandles;

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

    /// <summary>Non-zero once <see cref="Dispose"/> has been called.</summary>
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Artifact" /> class.
    /// </summary>
    /// <param name="source">The chunk source that provides access to the artifact's raw bytes.</param>
    /// <param name="header">The validated file header read from the artifact.</param>
    /// <param name="sourceKind">The chunk source strategy that was selected when opening.</param>
    /// <param name="segments">The ordered list of live segments parsed from the manifest.</param>
    private Artifact(IChunkSource source, ArtifactHeader header, ChunkSourceKind sourceKind, IReadOnlyList<ArtifactSegment> segments)
    {
        _source = source;
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

        long fileLength = new FileInfo(path).Length;
        ArtifactHeader header;
        SegmentManifest manifest;

        using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Span<byte> headerBytes = stackalloc byte[ArtifactFormat.HeaderSize];
            ReadExact(handle, headerBytes, 0);
            header = ArtifactHeader.Read(headerBytes);

            if ((long)header.ContentLength > fileLength)
            {
                throw new CartographFormatException(
                    $"Artifact is truncated: header declares {header.ContentLength} bytes but the file is {fileLength}.");
            }

            long manifestOffset = (long)header.ManifestOffset;
            long manifestLength = (long)header.ManifestLength;
            if (manifestOffset < ArtifactFormat.HeaderSize
                || manifestLength < ArtifactFormat.ManifestHeaderSize
                || manifestOffset + manifestLength > fileLength)
            {
                throw new CartographFormatException("Manifest offset/length falls outside the file.");
            }

            byte[] manifestBytes = new byte[manifestLength];
            ReadExact(handle, manifestBytes, manifestOffset);
            manifest = SegmentManifest.Read(manifestBytes);
        }

        IChunkSource source = options.ChunkSource == ChunkSourceKind.RandomAccess
            ? RandomAccessChunkSource.Open(path)
            : MappedChunkSource.Open(path, options.WindowSize);

        try
        {
            List<ArtifactSegment> segments = [];
            foreach (SegmentDescriptor descriptor in manifest.Segments)
            {
                if (!descriptor.IsLive)
                {
                    continue;
                }

                ValidateSegment(descriptor, fileLength);
                RecordDirectory directory = ReadDirectory(source, descriptor);
                segments.Add(new ArtifactSegment(source, descriptor, directory, options.VerifyChecksums));
            }

            return new Artifact(source, header, options.ChunkSource, segments);
        }
        catch
        {
            source.Dispose();
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source.Dispose();
        }
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

        if (payloadOffset < directoryOffset + directorySize || payloadOffset > dataEnd)
        {
            throw new CartographFormatException("Segment payload region falls outside the segment.");
        }
    }

    /// <summary>
    /// Reads and parses the record directory for a segment, validating each entry's offset and length.
    /// </summary>
    /// <param name="source">The chunk source used to read bytes from the artifact.</param>
    /// <param name="descriptor">The segment descriptor identifying the directory region.</param>
    /// <returns>A <see cref="RecordDirectory"/> containing the parsed per-record offsets, lengths, and checksums.</returns>
    /// <exception cref="CartographFormatException">Record directory is too large to read.</exception>
    /// <exception cref="CartographFormatException">Record offset/length falls outside its segment.</exception>
    private static RecordDirectory ReadDirectory(IChunkSource source, in SegmentDescriptor descriptor)
    {
        int count = checked((int)descriptor.RecordCount);
        long directorySize = (long)count * ArtifactFormat.RecordEntrySize;
        if (directorySize > int.MaxValue)
        {
            throw new CartographFormatException("Record directory is too large to read.");
        }

        long[] relOffsets = new long[count];
        long[] lengths = new long[count];
        ulong[] checksums = new ulong[count];

        if (count > 0)
        {
            using ChunkLease chunk = source.Read((long)descriptor.DirectoryOffset, (int)directorySize);
            byte[] bytes = ArrayPool<byte>.Shared.Rent((int)directorySize);
            try
            {
                chunk.Sequence.CopyTo(bytes);
                ReadOnlySpan<byte> span = bytes.AsSpan(0, (int)directorySize);
                long segmentEnd = (long)descriptor.DataOffset + (long)descriptor.DataLength;
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> entry = span.Slice(i * ArtifactFormat.RecordEntrySize, ArtifactFormat.RecordEntrySize);
                    long relOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[0..]);
                    long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
                    ulong checksum = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);

                    long absolute = (long)descriptor.PayloadOffset + relOffset;
                    if (relOffset < 0 || length < 0 || length > int.MaxValue || absolute < (long)descriptor.PayloadOffset || absolute + length > segmentEnd)
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
        }

        return new RecordDirectory(relOffsets, lengths, checksums);
    }

    /// <summary>
    /// Reads exactly <c>destination.Length</c> bytes from the file handle at the given offset,
    /// looping until satisfied or throwing if the file ends prematurely.
    /// </summary>
    /// <param name="handle">The open file handle to read from.</param>
    /// <param name="destination">The span to fill with the bytes read.</param>
    /// <param name="fileOffset">The byte offset within the file from which to begin reading.</param>
    /// <exception cref="CartographFormatException">Artifact is truncated: unexpected end of file.</exception>
    private static void ReadExact(SafeFileHandle handle, Span<byte> destination, long fileOffset)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(handle, destination[total..], fileOffset + total);
            if (read == 0)
            {
                throw new CartographFormatException("Artifact is truncated: unexpected end of file.");
            }

            total += read;
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
