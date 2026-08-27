using System.Buffers.Binary;
using System.IO.Hashing;

namespace Cartograph.Format;

/// <summary>
/// Builds a Cartograph artifact from one or more append-only segments, each holding a sequence of
/// records. The writer computes an explicit, aligned layout, per-record and per-segment XxHash3
/// checksums, and a checksummed header, then publishes them behind a single manifest.
/// </summary>
/// <remarks>
/// Segments are immutable once written. The manifest is written last and records which segments are
/// live, giving an atomic publish point: a reader that observes the new manifest sees a fully
/// written, self-consistent set of segments.
/// </remarks>
public sealed class SegmentedArtifactWriter
{
    private readonly List<SegmentBuilder> _segments = [];

    /// <summary>Adds a new segment and returns a builder to append records to it.</summary>
    /// <param name="segmentId">An optional stable id; defaults to the segment's ordinal.</param>
    public SegmentBuilder AddSegment(uint? segmentId = null)
    {
        SegmentBuilder builder = new(segmentId ?? (uint)_segments.Count);
        _segments.Add(builder);
        return builder;
    }

    /// <summary>Writes the artifact to <paramref name="path"/>, overwriting any existing file.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Save(stream);
    }

    /// <summary>Writes the artifact to <paramref name="stream"/>, which must be writable and seekable.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        SegmentGeometry[] geometry = ComputeLayout(out long manifestOffset, out int manifestLength);
        ulong contentLength = (ulong)(manifestOffset + manifestLength);

        ArtifactHeader header = new()
        {
            VersionMajor = ArtifactFormat.VersionMajor,
            VersionMinor = ArtifactFormat.VersionMinor,
            PointerSize = (byte)IntPtr.Size,
            ManifestOffset = (ulong)manifestOffset,
            ManifestLength = (ulong)manifestLength,
            ContentLength = contentLength,
        };

        Span<byte> headerBytes = stackalloc byte[ArtifactFormat.HeaderSize];
        header.Write(headerBytes);

        long position = 0;
        byte[] padScratch = new byte[ArtifactFormat.Alignment];

        stream.Write(headerBytes);
        position += headerBytes.Length;

        SegmentDescriptor[] descriptors = new SegmentDescriptor[_segments.Count];
        Span<byte> entry = stackalloc byte[ArtifactFormat.RecordEntrySize];
        for (int s = 0; s < _segments.Count; s++)
        {
            SegmentBuilder segment = _segments[s];
            SegmentGeometry geo = geometry[s];

            PadTo(stream, ref position, geo.DataOffset, padScratch, hasher: null);

            XxHash3 segmentHasher = new();

            // Record directory.
            for (int r = 0; r < segment.Records.Count; r++)
            {
                byte[] payload = segment.Records[r];
                BinaryPrimitives.WriteUInt64LittleEndian(entry[0..], (ulong)geo.RecordRelOffsets[r]);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)payload.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], XxHash3.HashToUInt64(payload));
                WriteAndHash(stream, ref position, entry, segmentHasher);
            }

            // Pad to the payload region.
            PadTo(stream, ref position, geo.PayloadOffset, padScratch, segmentHasher);

            // Record payloads, each padded up to alignment.
            for (int r = 0; r < segment.Records.Count; r++)
            {
                long recordStart = geo.PayloadOffset + geo.RecordRelOffsets[r];
                PadTo(stream, ref position, recordStart, padScratch, segmentHasher);
                byte[] payload = segment.Records[r];
                WriteAndHash(stream, ref position, payload, segmentHasher);
            }

            // Trailing pad to the end of the segment region.
            long segmentEnd = geo.DataOffset + geo.DataLength;
            PadTo(stream, ref position, segmentEnd, padScratch, segmentHasher);

            descriptors[s] = new SegmentDescriptor
            {
                SegmentId = segment.Id,
                Flags = ArtifactFormat.SegmentFlagLive,
                DataOffset = (ulong)geo.DataOffset,
                DataLength = (ulong)geo.DataLength,
                RecordCount = (ulong)segment.Records.Count,
                DirectoryOffset = (ulong)geo.DirectoryOffset,
                PayloadOffset = (ulong)geo.PayloadOffset,
                Checksum = segmentHasher.GetCurrentHashAsUInt64(),
            };
        }

        PadTo(stream, ref position, manifestOffset, padScratch, hasher: null);

        SegmentManifest manifest = new(descriptors);
        byte[] manifestBytes = new byte[manifest.ByteLength];
        manifest.Write(manifestBytes);
        stream.Write(manifestBytes);
        position += manifestBytes.Length;

        stream.Flush();
    }

    private SegmentGeometry[] ComputeLayout(out long manifestOffset, out int manifestLength)
    {
        SegmentGeometry[] geometry = new SegmentGeometry[_segments.Count];
        long offset = ArtifactFormat.HeaderSize;

        for (int s = 0; s < _segments.Count; s++)
        {
            SegmentBuilder segment = _segments[s];
            long dataOffset = Platform.AlignUp(offset, ArtifactFormat.Alignment);
            long directoryOffset = dataOffset;
            long directorySize = (long)segment.Records.Count * ArtifactFormat.RecordEntrySize;
            long payloadOffset = Platform.AlignUp(directoryOffset + directorySize, ArtifactFormat.Alignment);

            long[] relOffsets = new long[segment.Records.Count];
            long relRunning = 0;
            for (int r = 0; r < segment.Records.Count; r++)
            {
                relOffsets[r] = relRunning;
                relRunning = Platform.AlignUp(relRunning + segment.Records[r].Length, ArtifactFormat.Alignment);
            }

            long dataLength = payloadOffset - dataOffset + relRunning;
            geometry[s] = new SegmentGeometry(dataOffset, directoryOffset, payloadOffset, dataLength, relOffsets);
            offset = dataOffset + dataLength;
        }

        manifestOffset = Platform.AlignUp(offset, ArtifactFormat.Alignment);
        manifestLength = ArtifactFormat.ManifestHeaderSize + _segments.Count * ArtifactFormat.SegmentDescriptorSize;
        return geometry;
    }

    private static void WriteAndHash(Stream stream, ref long position, ReadOnlySpan<byte> data, XxHash3 hasher)
    {
        stream.Write(data);
        hasher.Append(data);
        position += data.Length;
    }

    private static void PadTo(Stream stream, ref long position, long target, byte[] scratch, XxHash3? hasher)
    {
        if (target < position)
        {
            throw new InvalidOperationException("Layout error: attempted to pad backwards.");
        }

        long remaining = target - position;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, scratch.Length);
            Array.Clear(scratch, 0, chunk);
            stream.Write(scratch, 0, chunk);
            hasher?.Append(scratch.AsSpan(0, chunk));
            position += chunk;
            remaining -= chunk;
        }
    }

    private readonly struct SegmentGeometry(
        long dataOffset,
        long directoryOffset,
        long payloadOffset,
        long dataLength,
        long[] recordRelOffsets)
    {
        public long DataOffset { get; } = dataOffset;
        public long DirectoryOffset { get; } = directoryOffset;
        public long PayloadOffset { get; } = payloadOffset;
        public long DataLength { get; } = dataLength;
        public long[] RecordRelOffsets { get; } = recordRelOffsets;
    }
}

/// <summary>Accumulates the records for a single segment being built by a <see cref="SegmentedArtifactWriter"/>.</summary>
public sealed class SegmentBuilder
{
    private readonly List<byte[]> _records = [];

    internal SegmentBuilder(uint id) => Id = id;

    internal uint Id { get; }

    internal IReadOnlyList<byte[]> Records => _records;

    /// <summary>The number of records added so far.</summary>
    public int RecordCount => _records.Count;

    /// <summary>Appends a record, copying <paramref name="data"/> into the writer.</summary>
    public SegmentBuilder AddRecord(ReadOnlySpan<byte> data)
    {
        _records.Add(data.ToArray());
        return this;
    }
}
