// ============================================================================
// Cartograph
// File: HeaderValidationTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests that validate ArtifactHeader parsing: correct round-trips, and clean
// exceptions for bad magic, wrong endianness, version mismatch, and checksum errors.
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
using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

/// <summary>
/// Tests that validate <see cref="ArtifactHeader"/> parsing and error handling.
/// </summary>
public class HeaderValidationTests
{
    /// <summary>
    /// Builds and returns a valid serialized <see cref="ArtifactHeader"/> byte buffer.
    /// </summary>
    /// <returns>A byte array containing a well-formed artifact header.</returns>
    private static byte[] ValidHeader()
    {
        ArtifactHeader header = new()
        {
            VersionMajor = ArtifactFormat.VersionMajor,
            VersionMinor = ArtifactFormat.VersionMinor,
            PointerSize = 8,
            ManifestOffset = 64,
            ManifestLength = 16,
            ContentLength = 80,
        };
        byte[] bytes = new byte[ArtifactFormat.HeaderSize];
        header.Write(bytes);
        return bytes;
    }

    /// <summary>
    /// Verifies that a valid header serializes and deserializes its fields correctly.
    /// </summary>
    [Fact]
    public void ValidHeader_RoundTrips()
    {
        ArtifactHeader header = ArtifactHeader.Read(ValidHeader());
        Assert.Equal(ArtifactFormat.VersionMajor, header.VersionMajor);
        Assert.Equal(64ul, header.ManifestOffset);
    }

    /// <summary>
    /// Verifies that a header with a corrupted magic field throws a
    /// <see cref="CartographFormatException"/> mentioning "magic".
    /// </summary>
    [Fact]
    public void BadMagic_Throws()
    {
        byte[] bytes = ValidHeader();
        bytes[0] ^= 0xFF;
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(bytes));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that a header with a byte-swapped endianness marker throws a
    /// <see cref="CartographFormatException"/> mentioning "endian".
    /// </summary>
    [Fact]
    public void WrongEndianness_Throws()
    {
        byte[] bytes = ValidHeader();
        // Byte-swap the endianness marker to simulate an opposite-endian producer.
        uint swapped = BinaryPrimitives.ReverseEndianness(ArtifactFormat.EndiannessMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), swapped);
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(bytes));
        Assert.Contains("endian", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that a header with an unsupported version number throws a
    /// <see cref="CartographFormatException"/> mentioning "version".
    /// </summary>
    [Fact]
    public void WrongVersion_Throws()
    {
        byte[] bytes = ValidHeader();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 99);
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(bytes));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that a header with a flipped reserved byte (covered by checksum) throws a
    /// <see cref="CartographFormatException"/> mentioning "checksum".
    /// </summary>
    [Fact]
    public void CorruptHeaderChecksum_Throws()
    {
        byte[] bytes = ValidHeader();
        // Flip a reserved byte covered by the checksum but not magic/marker/version.
        bytes[44] ^= 0x5A;
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(bytes));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that passing a buffer shorter than the minimum header size throws a
    /// <see cref="CartographFormatException"/>.
    /// </summary>
    [Fact]
    public void ShortBuffer_Throws()
    {
        Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(new byte[10]));
    }
}
