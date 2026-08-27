using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

public class RoundTripTests
{
    [Theory]
    [InlineData(ChunkSourceKind.Mapped)]
    [InlineData(ChunkSourceKind.RandomAccess)]
    public void WriteThenReopen_ReturnsIdenticalRecords(ChunkSourceKind kind)
    {
        byte[][] records =
        [
            TestArtifacts.Pattern(16, 1),
            TestArtifacts.Pattern(1000, 40),
            TestArtifacts.Pattern(4096, 7),
            [],
        ];

        string path = TestArtifacts.WriteSingleSegment(records);
        using TempFile temp = new(path);

        ArtifactOpenOptions options = new() { ChunkSource = kind };
        using Artifact artifact = Artifact.Open(path, options);

        Assert.Equal(kind, artifact.SourceKind);
        Assert.Single(artifact.Segments);
        Assert.Equal(records.Length, artifact.RecordCount);

        for (int i = 0; i < records.Length; i++)
        {
            using RecordLease lease = artifact.ReadRecord(i);
            Assert.Equal(records[i], lease.ToArray());
            Assert.Equal(records[i].Length, lease.Length);
        }
    }

    [Fact]
    public void MultipleSegments_EnumerateInOrder()
    {
        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        SegmentedArtifactWriter writer = new();
        writer.AddSegment(10).AddRecord(TestArtifacts.Pattern(50, 1)).AddRecord(TestArtifacts.Pattern(60, 2));
        writer.AddSegment(20).AddRecord(TestArtifacts.Pattern(70, 3));
        writer.Save(path);

        using Artifact artifact = Artifact.Open(path);

        Assert.Equal(2, artifact.Segments.Count);
        Assert.Equal(10u, artifact.Segments[0].SegmentId);
        Assert.Equal(20u, artifact.Segments[1].SegmentId);
        Assert.Equal(3, artifact.RecordCount);

        using RecordLease first = artifact.ReadRecord(0);
        using RecordLease last = artifact.ReadRecord(2);
        Assert.Equal(TestArtifacts.Pattern(50, 1), first.ToArray());
        Assert.Equal(TestArtifacts.Pattern(70, 3), last.ToArray());
    }

    [Fact]
    public async Task ReadRecordAsync_MatchesSync()
    {
        byte[] record = TestArtifacts.Pattern(2048, 9);
        string path = TestArtifacts.WriteSingleSegment([record]);
        using TempFile temp = new(path);

        using Artifact artifact = Artifact.Open(path, new ArtifactOpenOptions { ChunkSource = ChunkSourceKind.RandomAccess });
        using RecordLease lease = await artifact.Segments[0].ReadRecordAsync(0);
        Assert.Equal(record, lease.ToArray());
    }
}
