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
    private readonly IChunkSource _source;
    private int _disposed;

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
    /// <exception cref="CartographFormatException">The file is not a valid, self-consistent artifact.</exception>
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

    internal static ulong HashSequence(ReadOnlySequence<byte> sequence)
    {
        XxHash3 hasher = new();
        foreach (ReadOnlyMemory<byte> segment in sequence)
        {
            hasher.Append(segment.Span);
        }

        return hasher.GetCurrentHashAsUInt64();
    }

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

internal sealed class RecordDirectory(long[] relOffsets, long[] lengths, ulong[] checksums)
{
    public long[] RelOffsets { get; } = relOffsets;
    public long[] Lengths { get; } = lengths;
    public ulong[] Checksums { get; } = checksums;
}
