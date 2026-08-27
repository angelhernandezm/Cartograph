using System.Buffers.Binary;

namespace Cartograph.Format;

/// <summary>
/// The append-only segment manifest: the list of segment descriptors plus which of them are live.
/// </summary>
public sealed class SegmentManifest
{
    /// <summary>Creates a manifest over the supplied descriptors.</summary>
    public SegmentManifest(IReadOnlyList<SegmentDescriptor> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Segments = segments;
    }

    /// <summary>All descriptors recorded in the manifest, in order.</summary>
    public IReadOnlyList<SegmentDescriptor> Segments { get; }

    /// <summary>The serialized size of this manifest in bytes.</summary>
    public int ByteLength => ArtifactFormat.ManifestHeaderSize + Segments.Count * ArtifactFormat.SegmentDescriptorSize;

    /// <summary>Serializes the manifest into <paramref name="destination"/>.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < ByteLength)
        {
            throw new ArgumentException("Destination is smaller than the manifest.", nameof(destination));
        }

        int liveCount = 0;
        foreach (SegmentDescriptor segment in Segments)
        {
            if (segment.IsLive)
            {
                liveCount++;
            }
        }

        destination[..ByteLength].Clear();
        ArtifactFormat.ManifestMagic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)Segments.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)liveCount);
        // 12..16 reserved

        int offset = ArtifactFormat.ManifestHeaderSize;
        foreach (SegmentDescriptor segment in Segments)
        {
            segment.Write(destination.Slice(offset, ArtifactFormat.SegmentDescriptorSize));
            offset += ArtifactFormat.SegmentDescriptorSize;
        }
    }

    /// <summary>
    /// Parses a manifest from <paramref name="source"/>, validating the magic and length. Descriptor
    /// offsets are bounds-checked separately when the owning <see cref="Artifact"/> is opened.
    /// </summary>
    public static SegmentManifest Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < ArtifactFormat.ManifestHeaderSize)
        {
            throw new CartographFormatException("Manifest is smaller than its fixed header.");
        }

        if (!source[..4].SequenceEqual(ArtifactFormat.ManifestMagic))
        {
            throw new CartographFormatException("Bad manifest magic; the manifest is corrupt or misaligned.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        long required = ArtifactFormat.ManifestHeaderSize + (long)count * ArtifactFormat.SegmentDescriptorSize;
        if (source.Length < required)
        {
            throw new CartographFormatException("Manifest is truncated; declared segment count exceeds available bytes.");
        }

        SegmentDescriptor[] segments = new SegmentDescriptor[count];
        int offset = ArtifactFormat.ManifestHeaderSize;
        for (int i = 0; i < count; i++)
        {
            segments[i] = SegmentDescriptor.Read(source.Slice(offset, ArtifactFormat.SegmentDescriptorSize));
            offset += ArtifactFormat.SegmentDescriptorSize;
        }

        return new SegmentManifest(segments);
    }
}
