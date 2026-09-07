// ============================================================================
// Cartograph
// File: ArtifactFormat.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Defines all on-disk layout constants (magic bytes, sizes, alignment, flags)
// shared between the reader and writer of the Cartograph artifact format.
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

namespace Cartograph.Format;

/// <summary>
/// On-disk layout constants for the Cartograph artifact format.
/// </summary>
/// <remarks>
/// <para>
/// All multi-byte integers are stored little-endian. The format stores <b>relative offsets only</b>:
/// every offset is measured from the start of the file, never as an absolute pointer. The base
/// address of a mapping differs on every open, so a persisted absolute pointer would be a guaranteed
/// crash on reload. Relative offsets are re-based against the live mapping on every access.
/// </para>
/// <para>
/// Alignment is explicit, not accidental. Segment regions, record directories, and record payloads
/// are all padded to <see cref="Alignment"/> so that <c>MemoryMarshal.Cast&lt;byte, float&gt;</c>
/// (which requires 4-byte alignment) and SIMD loads (which prefer 32/64-byte alignment) are always
/// legal against mapped pages.
/// </para>
/// </remarks>
public static class ArtifactFormat {
    /// <summary>The 4-byte file magic: ASCII "CTGH".</summary>
    /// <value>The 4-byte file magic: ASCII "CTGH".</value>
    public static ReadOnlySpan<byte> Magic => "CTGH"u8;

    /// <summary>The 4-byte segment-manifest magic: ASCII "CTMF".</summary>
    /// <value>The 4-byte segment-manifest magic: ASCII "CTMF".</value>
    public static ReadOnlySpan<byte> ManifestMagic => "CTMF"u8;

    /// <summary>The endianness sentinel written as a little-endian <see cref="uint"/>.</summary>
    public const uint EndiannessMarker = 0x01020304u;

    /// <summary>The current format major version.</summary>
    public const ushort VersionMajor = 0;

    /// <summary>The current format minor version.</summary>
    public const ushort VersionMinor = 1;

    /// <summary>The fixed size of the file header in bytes.</summary>
    public const int HeaderSize = 64;

    /// <summary>The number of header bytes covered by the header checksum (everything before it).</summary>
    public const int HeaderChecksumCoverage = 56;

    /// <summary>The fixed size of a manifest header in bytes.</summary>
    public const int ManifestHeaderSize = 16;

    /// <summary>The fixed size of one segment descriptor in bytes.</summary>
    public const int SegmentDescriptorSize = 64;

    /// <summary>The fixed size of one record directory entry in bytes.</summary>
    public const int RecordEntrySize = 24;

    /// <summary>The explicit alignment (bytes) applied to segments, directories, and record payloads.</summary>
    public const int Alignment = 64;

    /// <summary>
    /// The largest byte length a single record may span.
    /// </summary>
    /// <remarks>
    /// The on-disk directory stores record offsets and lengths as 64-bit values, so the format itself
    /// imposes no such ceiling. The limit comes from the read path: a record is surfaced as a single
    /// <see cref="Cartograph.ChunkLease"/> obtained from <see cref="Cartograph.IChunkSource.Read"/>,
    /// whose length parameter is a 32-bit <see cref="int"/>. Inputs larger than this are expected to
    /// be split across several records, which the artifact can then present as one logical object.
    /// </remarks>
    public const long MaxRecordLength = int.MaxValue;

    /// <summary>Segment descriptor flag: the segment is live in the current manifest.</summary>
    public const uint SegmentFlagLive = 0x1u;
}
