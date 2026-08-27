using System.Buffers;
using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

public class SegmentedSequenceTests
{
    [Fact]
    public void MappedSequence_StitchesChunksInOrder()
    {
        byte[] a = TestArtifacts.Pattern(10, 0);
        byte[] b = TestArtifacts.Pattern(10, 10);
        byte[] c = TestArtifacts.Pattern(10, 20);

        ReadOnlySequence<byte> sequence = MappedSequence.Create([a, b, c]);

        Assert.False(sequence.IsSingleSegment);
        Assert.Equal(30, sequence.Length);
        Assert.Equal(TestArtifacts.Pattern(30, 0), sequence.ToArray());
    }

    [Fact]
    public void LargeRecord_SpansMultipleMappedWindows()
    {
        // A record larger than one 64 KiB window forces the stitching path across views.
        byte[] big = TestArtifacts.Pattern(200_000, 3);
        byte[] small = TestArtifacts.Pattern(32, 1);

        string path = TestArtifacts.WriteSingleSegment([small, big]);
        using TempFile temp = new(path);

        // Force a tiny window so a single record must cross window boundaries.
        ArtifactOpenOptions options = new() { ChunkSource = ChunkSourceKind.Mapped, WindowSize = 1 };
        using Artifact artifact = Artifact.Open(path, options);

        using RecordLease lease = artifact.ReadRecord(1);

        Assert.False(lease.IsSingleSegment);
        Assert.True(CountSegments(lease.Sequence) > 1);
        Assert.Equal(big, lease.ToArray());
    }

    [Fact]
    public void CrossBoundaryReconstruction_IsByteExact()
    {
        byte[] big = TestArtifacts.Pattern(150_000, 99);
        string path = TestArtifacts.WriteSingleSegment([big]);
        using TempFile temp = new(path);

        using Artifact artifact = Artifact.Open(path, new ArtifactOpenOptions { WindowSize = 1 });
        using RecordLease lease = artifact.ReadRecord(0);

        // Walk the sequence manually and rebuild, verifying boundary handling.
        byte[] rebuilt = new byte[lease.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in lease.Sequence)
        {
            segment.Span.CopyTo(rebuilt.AsSpan(offset));
            offset += segment.Length;
        }

        Assert.Equal(big, rebuilt);
    }

    private static int CountSegments(ReadOnlySequence<byte> sequence)
    {
        int count = 0;
        foreach (ReadOnlyMemory<byte> _ in sequence)
        {
            count++;
        }

        return count;
    }
}
