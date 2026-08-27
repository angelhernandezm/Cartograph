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
public static class ArtifactFormat
{
    /// <summary>The 4-byte file magic: ASCII "CTGH".</summary>
    public static ReadOnlySpan<byte> Magic => "CTGH"u8;

    /// <summary>The 4-byte segment-manifest magic: ASCII "CTMF".</summary>
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

    /// <summary>Segment descriptor flag: the segment is live in the current manifest.</summary>
    public const uint SegmentFlagLive = 0x1u;
}
