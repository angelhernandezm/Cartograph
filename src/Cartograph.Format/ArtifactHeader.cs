using System.Buffers.Binary;
using System.IO.Hashing;

namespace Cartograph.Format;

/// <summary>
/// The fixed-size file header. Opening an artifact validates this header and refuses anything
/// unrecognized rather than reading garbage.
/// </summary>
public readonly struct ArtifactHeader
{
    /// <summary>The format major version recorded in the file.</summary>
    public required ushort VersionMajor { get; init; }

    /// <summary>The format minor version recorded in the file.</summary>
    public required ushort VersionMinor { get; init; }

    /// <summary>The native pointer size (in bytes) recorded by the writer.</summary>
    public required byte PointerSize { get; init; }

    /// <summary>The file-relative offset of the segment manifest.</summary>
    public required ulong ManifestOffset { get; init; }

    /// <summary>The length in bytes of the segment manifest.</summary>
    public required ulong ManifestLength { get; init; }

    /// <summary>The total length in bytes the writer recorded for the artifact.</summary>
    public required ulong ContentLength { get; init; }

    /// <summary>Serializes the header into <paramref name="destination"/> (must be at least <see cref="ArtifactFormat.HeaderSize"/> bytes).</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < ArtifactFormat.HeaderSize)
        {
            throw new ArgumentException("Destination is smaller than the header.", nameof(destination));
        }

        destination[..ArtifactFormat.HeaderSize].Clear();
        ArtifactFormat.Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], VersionMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], VersionMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], ArtifactFormat.EndiannessMarker);
        destination[12] = PointerSize;
        // 13..16 reserved
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], ManifestOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], ManifestLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], ContentLength);
        // 40..56 reserved
        ulong checksum = XxHash3.HashToUInt64(destination[..ArtifactFormat.HeaderChecksumCoverage]);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[ArtifactFormat.HeaderChecksumCoverage..], checksum);
    }

    /// <summary>
    /// Parses and validates a header from <paramref name="source"/>, throwing
    /// <see cref="CartographFormatException"/> for any inconsistency.
    /// </summary>
    public static ArtifactHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < ArtifactFormat.HeaderSize)
        {
            throw new CartographFormatException("File is smaller than the fixed header.");
        }

        if (!source[..4].SequenceEqual(ArtifactFormat.Magic))
        {
            throw new CartographFormatException("Bad magic: not a Cartograph artifact.");
        }

        uint marker = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        if (marker != ArtifactFormat.EndiannessMarker)
        {
            throw new CartographFormatException(
                $"Endianness marker mismatch (0x{marker:X8}); the artifact was written on an incompatible byte order.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        if (major != ArtifactFormat.VersionMajor)
        {
            throw new CartographFormatException(
                $"Unsupported format version {major}.{minor}; this build reads {ArtifactFormat.VersionMajor}.x.");
        }

        ulong expected = XxHash3.HashToUInt64(source[..ArtifactFormat.HeaderChecksumCoverage]);
        ulong actual = BinaryPrimitives.ReadUInt64LittleEndian(source[ArtifactFormat.HeaderChecksumCoverage..]);
        if (expected != actual)
        {
            throw new CartographFormatException("Header checksum mismatch; the header is corrupt.");
        }

        return new ArtifactHeader
        {
            VersionMajor = major,
            VersionMinor = minor,
            PointerSize = source[12],
            ManifestOffset = BinaryPrimitives.ReadUInt64LittleEndian(source[16..]),
            ManifestLength = BinaryPrimitives.ReadUInt64LittleEndian(source[24..]),
            ContentLength = BinaryPrimitives.ReadUInt64LittleEndian(source[32..]),
        };
    }
}
