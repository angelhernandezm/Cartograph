// ============================================================================
// Cartograph
// File: SegmentManifest.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Represents the append-only segment manifest that lists all segment descriptors
// and marks which are live; provides binary serialization (Write) and deserialization (Read).
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

using System.Buffers.Binary;

namespace Cartograph.Format;

/// <summary>
/// The append-only segment manifest: the list of segment descriptors plus which of them are live.
/// </summary>
public sealed class SegmentManifest
{
    /// <summary>Creates a manifest over the supplied descriptors.</summary>
    /// <param name="segments">The ordered list of segment descriptors to include in the manifest.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="segments"/> is <c>null</c>.</exception>
    public SegmentManifest(IReadOnlyList<SegmentDescriptor> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Segments = segments;
    }

    /// <summary>All descriptors recorded in the manifest, in order.</summary>
    /// <value>All descriptors recorded in the manifest, in order.</value>
    public IReadOnlyList<SegmentDescriptor> Segments { get; }

    /// <summary>The serialized size of this manifest in bytes.</summary>
    /// <value>The serialized size of this manifest in bytes.</value>
    public int ByteLength => ArtifactFormat.ManifestHeaderSize + Segments.Count * ArtifactFormat.SegmentDescriptorSize;

    /// <summary>Serializes the manifest into <paramref name="destination"/>.</summary>
    /// <param name="destination">The byte span to write the manifest into; must be at least <see cref="ByteLength"/> bytes long.</param>
    /// <exception cref="System.ArgumentException">Destination is smaller than the manifest.</exception>
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
    /// <param name="source">The raw bytes to parse; must begin with the manifest magic and be long enough for all descriptors.</param>
    /// <returns>A <see cref="SegmentManifest"/> populated with the parsed descriptors.</returns>
    /// <exception cref="CartographFormatException">Manifest is smaller than its fixed header.</exception>
    /// <exception cref="CartographFormatException">Bad manifest magic; the manifest is corrupt or misaligned.</exception>
    /// <exception cref="CartographFormatException">Manifest is truncated; declared segment count exceeds available bytes.</exception>
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
