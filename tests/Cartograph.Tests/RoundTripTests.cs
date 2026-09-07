// ============================================================================
// Cartograph
// File: RoundTripTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests that write artifacts with one or multiple segments and verify that records
// read back are byte-identical to the originals, including async read paths.
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

using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

/// <summary>
/// Tests that verify write-then-read round-trips produce byte-identical records.
/// </summary>
public class RoundTripTests
{
    /// <summary>
    /// Verifies that records written to an artifact are read back identically when using
    /// both the <see cref="ChunkSourceKind.Mapped"/> and <see cref="ChunkSourceKind.RandomAccess"/> sources.
    /// </summary>
    /// <param name="kind">The chunk source kind to use when opening the artifact.</param>
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

    /// <summary>
    /// Verifies that a multiple-segments artifact enumerates segments and records in
    /// declaration order, with correct segment IDs and record count.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="ArtifactSegment.ReadRecordAsync"/> returns bytes
    /// identical to the synchronous <see cref="ArtifactSegment.ReadRecord"/> path.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when the assertion has run.</returns>
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
