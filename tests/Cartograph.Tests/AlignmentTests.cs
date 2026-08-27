using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

public class AlignmentTests
{
    [Fact]
    public void FloatRecord_RoundTripsThroughMemoryMarshalCast()
    {
        float[] vector = new float[512];
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = MathF.Sin(i) * 3.5f + i;
        }

        byte[] payload = MemoryMarshal.AsBytes<float>(vector).ToArray();
        string path = TestArtifacts.WriteSingleSegment([payload]);
        using TempFile temp = new(path);

        using Artifact artifact = Artifact.Open(path);
        using RecordLease lease = artifact.ReadRecord(0);

        Assert.True(lease.IsSingleSegment);
        ReadOnlySpan<float> readBack = MemoryMarshal.Cast<byte, float>(lease.FirstSpan);
        Assert.Equal(vector.Length, readBack.Length);
        for (int i = 0; i < vector.Length; i++)
        {
            Assert.Equal(vector[i], readBack[i]);
        }
    }

    [Fact]
    public unsafe void FirstRecordPayload_IsAtLeast4ByteAligned()
    {
        byte[] payload = TestArtifacts.Pattern(2048, 11);
        string path = TestArtifacts.WriteSingleSegment([payload]);
        using TempFile temp = new(path);

        using Artifact artifact = Artifact.Open(path);
        using RecordLease lease = artifact.ReadRecord(0);

        ref readonly byte first = ref MemoryMarshal.GetReference(lease.FirstSpan);
        nint address = (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in first));

        // Explicit layout aligns record payloads to 64 bytes; the mapped page base is page-aligned,
        // so the observed address must satisfy the 4-byte requirement of MemoryMarshal.Cast<byte,float>.
        Assert.Equal(0, (int)(address % 4));
        Assert.Equal(0, (int)(address % ArtifactFormat.Alignment));
    }
}
