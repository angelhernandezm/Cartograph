using System.Buffers.Binary;

namespace Cartograph.Format;

/// <summary>
/// One entry in the segment manifest, describing an append-only, immutable segment.
/// </summary>
/// <remarks>
/// All offsets are file-relative. The manifest is the atomic publish point: a writer appends new
/// segments then swaps the manifest, and readers holding leases on old segments finish before those
/// segments are unmapped and deleted. This immutable-segment-plus-atomic-manifest scheme gives crash
/// consistency without a write-ahead log, and is modeled on Lucene's MMapDirectory and Tantivy's
/// segment design.
/// </remarks>
public readonly struct SegmentDescriptor
{
    /// <summary>A stable identifier for the segment.</summary>
    public required uint SegmentId { get; init; }

    /// <summary>Descriptor flags (see <see cref="ArtifactFormat.SegmentFlagLive"/>).</summary>
    public required uint Flags { get; init; }

    /// <summary>The file-relative offset of the segment region.</summary>
    public required ulong DataOffset { get; init; }

    /// <summary>The length of the segment region in bytes.</summary>
    public required ulong DataLength { get; init; }

    /// <summary>The number of records in the segment.</summary>
    public required ulong RecordCount { get; init; }

    /// <summary>The file-relative offset of the record directory.</summary>
    public required ulong DirectoryOffset { get; init; }

    /// <summary>The file-relative offset of the record payload region.</summary>
    public required ulong PayloadOffset { get; init; }

    /// <summary>The XxHash3 checksum of the whole segment region.</summary>
    public required ulong Checksum { get; init; }

    /// <summary>Whether the segment is live in the current manifest.</summary>
    public bool IsLive => (Flags & ArtifactFormat.SegmentFlagLive) != 0;

    /// <summary>Serializes the descriptor into <paramref name="destination"/> (<see cref="ArtifactFormat.SegmentDescriptorSize"/> bytes).</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < ArtifactFormat.SegmentDescriptorSize)
        {
            throw new ArgumentException("Destination is smaller than a segment descriptor.", nameof(destination));
        }

        destination[..ArtifactFormat.SegmentDescriptorSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..], SegmentId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], DataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], DataLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], RecordCount);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], DirectoryOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..], PayloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[48..], Checksum);
        // 56..64 reserved
    }

    /// <summary>Parses a descriptor from <paramref name="source"/>.</summary>
    public static SegmentDescriptor Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < ArtifactFormat.SegmentDescriptorSize)
        {
            throw new CartographFormatException("Manifest is truncated inside a segment descriptor.");
        }

        return new SegmentDescriptor
        {
            SegmentId = BinaryPrimitives.ReadUInt32LittleEndian(source[0..]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
            DataOffset = BinaryPrimitives.ReadUInt64LittleEndian(source[8..]),
            DataLength = BinaryPrimitives.ReadUInt64LittleEndian(source[16..]),
            RecordCount = BinaryPrimitives.ReadUInt64LittleEndian(source[24..]),
            DirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(source[32..]),
            PayloadOffset = BinaryPrimitives.ReadUInt64LittleEndian(source[40..]),
            Checksum = BinaryPrimitives.ReadUInt64LittleEndian(source[48..]),
        };
    }
}
