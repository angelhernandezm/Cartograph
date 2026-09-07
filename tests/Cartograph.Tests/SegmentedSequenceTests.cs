// ============================================================================
// Cartograph
// File: SegmentedSequenceTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests that verify MappedSequence stitches multi-chunk records correctly,
// including large records that span multiple memory-mapped windows.
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

using System.Buffers;
using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

/// <summary>
/// Tests that verify <see cref="MappedSequence"/> stitches multi-chunk records correctly.
/// </summary>
public class SegmentedSequenceTests {
    /// <summary>
    /// Verifies that <see cref="MappedSequence.Create"/> produces a multi-segment
    /// <see cref="ReadOnlySequence{T}"/> that concatenates the source chunks in order.
    /// </summary>
    [Fact]
    public void MappedSequence_StitchesChunksInOrder() {
        byte[] a = TestArtifacts.Pattern(10, 0);
        byte[] b = TestArtifacts.Pattern(10, 10);
        byte[] c = TestArtifacts.Pattern(10, 20);

        ReadOnlySequence<byte> sequence = MappedSequence.Create([a, b, c]);

        Assert.False(sequence.IsSingleSegment);
        Assert.Equal(30, sequence.Length);
        Assert.Equal(TestArtifacts.Pattern(30, 0), sequence.ToArray());
    }

    /// <summary>
    /// Verifies that a record larger than one mapped window forces multi-segment stitching
    /// and that the resulting bytes are byte-exact.
    /// </summary>
    [Fact]
    public void LargeRecord_SpansMultipleMappedWindows() {
        // A record larger than one 64 KiB window forces the stitching path across views.
        byte[] big = TestArtifacts.Pattern(200_000, 3);
        byte[] small = TestArtifacts.Pattern(32, 1);

        string path = TestArtifacts.WriteSingleSegment([small, big]);
        using TempFile temp = new(path);

        // Force a tiny window so a single record must cross window boundaries.
        ArtifactOpenOptions options = new() {
            ChunkSource = ChunkSourceKind.Mapped, WindowSize = 1
        };
        using Artifact artifact = Artifact.Open(path, options);

        using RecordLease lease = artifact.ReadRecord(1);

        Assert.False(lease.IsSingleSegment);
        Assert.True(CountSegments(lease.Sequence) > 1);
        Assert.Equal(big, lease.ToArray());
    }

    /// <summary>
    /// Verifies that manually walking the <see cref="RecordLease.Sequence"/> across window
    /// boundaries produces bytes that are bit-for-bit identical to the original payload.
    /// </summary>
    [Fact]
    public void CrossBoundaryReconstruction_IsByteExact() {
        byte[] big = TestArtifacts.Pattern(150_000, 99);
        string path = TestArtifacts.WriteSingleSegment([big]);
        using TempFile temp = new(path);

        using Artifact artifact = Artifact.Open(path, new ArtifactOpenOptions { WindowSize = 1 });
        using RecordLease lease = artifact.ReadRecord(0);

        // Walk the sequence manually and rebuild, verifying boundary handling.
        byte[] rebuilt = new byte[lease.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in lease.Sequence) {
            segment.Span.CopyTo(rebuilt.AsSpan(offset));
            offset += segment.Length;
        }

        Assert.Equal(big, rebuilt);
    }

    /// <summary>
    /// Counts the number of segments in a <see cref="ReadOnlySequence{T}"/>.
    /// </summary>
    /// <param name="sequence">The sequence whose segments are counted.</param>
    /// <returns>The total number of memory segments in the sequence.</returns>
    private static int CountSegments(ReadOnlySequence<byte> sequence) {
        int count = 0;
        foreach (ReadOnlyMemory<byte> _ in sequence) {
            count++;
        }

        return count;
    }
}
