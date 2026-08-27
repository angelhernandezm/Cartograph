using System.Buffers.Binary;
using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

public class HeaderValidationTests
{
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

    [Fact]
    public void ValidHeader_RoundTrips()
    {
        ArtifactHeader header = ArtifactHeader.Read(ValidHeader());
        Assert.Equal(ArtifactFormat.VersionMajor, header.VersionMajor);
        Assert.Equal(64ul, header.ManifestOffset);
    }

    [Fact]
    public void BadMagic_Throws()
    {
        byte[] bytes = ValidHeader();
        bytes[0] ^= 0xFF;
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(bytes));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

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

    [Fact]
    public void WrongVersion_Throws()
    {
        byte[] bytes = ValidHeader();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 99);
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(bytes));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptHeaderChecksum_Throws()
    {
        byte[] bytes = ValidHeader();
        // Flip a reserved byte covered by the checksum but not magic/marker/version.
        bytes[44] ^= 0x5A;
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(bytes));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortBuffer_Throws()
    {
        Assert.Throws<CartographFormatException>(() => ArtifactHeader.Read(new byte[10]));
    }
}
