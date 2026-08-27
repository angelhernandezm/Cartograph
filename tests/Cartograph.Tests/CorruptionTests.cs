using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

public class CorruptionTests
{
    [Fact]
    public void BadMagic_OnOpen_ThrowsCleanly()
    {
        string path = TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(100, 1)]);
        using TempFile temp = new(path);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<CartographFormatException>(() => Artifact.Open(path));
    }

    [Fact]
    public void TruncatedFile_ThrowsCleanly()
    {
        string path = TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(4096, 2)]);
        using TempFile temp = new(path);

        byte[] bytes = File.ReadAllBytes(path);
        // Drop the tail (including the manifest) to simulate a truncated write.
        File.WriteAllBytes(path, bytes[..(bytes.Length - 128)]);

        Assert.Throws<CartographFormatException>(() => Artifact.Open(path));
    }

    [Fact]
    public void CorruptRecordPayload_FailsChecksum()
    {
        // A single-record, single-segment artifact places the first payload at offset 128.
        byte[] record = TestArtifacts.Pattern(4096, 0xAB);
        string path = TestArtifacts.WriteSingleSegment([record]);
        using TempFile temp = new(path);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[150] ^= 0x01; // inside the payload region
        File.WriteAllBytes(path, bytes);

        using Artifact artifact = Artifact.Open(path);
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => artifact.ReadRecord(0));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChecksumVerificationDisabled_DoesNotThrow()
    {
        byte[] record = TestArtifacts.Pattern(4096, 0xAB);
        string path = TestArtifacts.WriteSingleSegment([record]);
        using TempFile temp = new(path);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[150] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        using Artifact artifact = Artifact.Open(path, new ArtifactOpenOptions { VerifyChecksums = false });
        using RecordLease lease = artifact.ReadRecord(0);
        Assert.Equal(4096, lease.Length); // reads the (corrupt) bytes without validation
    }
}
