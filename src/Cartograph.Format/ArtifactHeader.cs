// ============================================================================
// Cartograph
// File: ArtifactHeader.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Defines the fixed-size 64-byte file header struct with serialization (Write)
// and deserialization (Read) including magic, version, endianness, and checksum validation.
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
using System.IO.Hashing;

namespace Cartograph.Format;

/// <summary>
/// The fixed-size file header. Opening an artifact validates this header and refuses anything
/// unrecognized rather than reading garbage.
/// </summary>
public readonly struct ArtifactHeader {
    /// <summary>The format major version recorded in the file.</summary>
    /// <value>The format major version recorded in the file.</value>
    public required ushort VersionMajor {
        get; init;
    }

    /// <summary>The format minor version recorded in the file.</summary>
    /// <value>The format minor version recorded in the file.</value>
    public required ushort VersionMinor {
        get; init;
    }

    /// <summary>The native pointer size (in bytes) recorded by the writer.</summary>
    /// <value>The native pointer size (in bytes) recorded by the writer.</value>
    public required byte PointerSize {
        get; init;
    }

    /// <summary>The file-relative offset of the segment manifest.</summary>
    /// <value>The file-relative offset of the segment manifest.</value>
    public required ulong ManifestOffset {
        get; init;
    }

    /// <summary>The length in bytes of the segment manifest.</summary>
    /// <value>The length in bytes of the segment manifest.</value>
    public required ulong ManifestLength {
        get; init;
    }

    /// <summary>The total length in bytes the writer recorded for the artifact.</summary>
    /// <value>The total length in bytes the writer recorded for the artifact.</value>
    public required ulong ContentLength {
        get; init;
    }

    /// <summary>Serializes the header into <paramref name="destination"/> (must be at least <see cref="ArtifactFormat.HeaderSize"/> bytes).</summary>
    /// <param name="destination">The byte span to write the serialized header into; must be at least <see cref="ArtifactFormat.HeaderSize"/> bytes long.</param>
    /// <exception cref="System.ArgumentException">Destination is smaller than the header.</exception>
    public void Write(Span<byte> destination) {
        if (destination.Length < ArtifactFormat.HeaderSize) {
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
    /// <param name="source">The raw bytes to parse; must be at least <see cref="ArtifactFormat.HeaderSize"/> bytes long.</param>
    /// <returns>The populated and validated <see cref="ArtifactHeader"/>.</returns>
    /// <exception cref="CartographFormatException">File is smaller than the fixed header.</exception>
    /// <exception cref="CartographFormatException">Bad magic: not a Cartograph artifact.</exception>
    /// <exception cref="CartographFormatException">Endianness marker mismatch; the artifact was written on an incompatible byte order.</exception>
    /// <exception cref="CartographFormatException">Unsupported format version; this build reads a different major version.</exception>
    /// <exception cref="CartographFormatException">Header checksum mismatch; the header is corrupt.</exception>
    public static ArtifactHeader Read(ReadOnlySpan<byte> source) {
        if (source.Length < ArtifactFormat.HeaderSize) {
            throw new CartographFormatException("File is smaller than the fixed header.");
        }

        if (!source[..4].SequenceEqual(ArtifactFormat.Magic)) {
            throw new CartographFormatException("Bad magic: not a Cartograph artifact.");
        }

        uint marker = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        if (marker != ArtifactFormat.EndiannessMarker) {
            throw new CartographFormatException(
                $"Endianness marker mismatch (0x{marker:X8}); the artifact was written on an incompatible byte order.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        if (major != ArtifactFormat.VersionMajor) {
            throw new CartographFormatException(
                $"Unsupported format version {major}.{minor}; this build reads {ArtifactFormat.VersionMajor}.x.");
        }

        ulong expected = XxHash3.HashToUInt64(source[..ArtifactFormat.HeaderChecksumCoverage]);
        ulong actual = BinaryPrimitives.ReadUInt64LittleEndian(source[ArtifactFormat.HeaderChecksumCoverage..]);
        if (expected != actual) {
            throw new CartographFormatException("Header checksum mismatch; the header is corrupt.");
        }

        return new ArtifactHeader {
            VersionMajor = major,
            VersionMinor = minor,
            PointerSize = source[12],
            ManifestOffset = BinaryPrimitives.ReadUInt64LittleEndian(source[16..]),
            ManifestLength = BinaryPrimitives.ReadUInt64LittleEndian(source[24..]),
            ContentLength = BinaryPrimitives.ReadUInt64LittleEndian(source[32..]),
        };
    }
}
