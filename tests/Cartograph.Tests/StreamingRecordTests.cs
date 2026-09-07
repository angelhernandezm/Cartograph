// ============================================================================
// Cartograph
// File: StreamingRecordTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests covering file-backed streaming records, payload-first segment layout,
// and the record size ceiling enforced when records are appended
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
/// Tests that records streamed from disk round-trip identically to buffered records, that segments
/// holding them are laid out payload-first, and that oversized records are rejected on append.
/// </summary>
public class StreamingRecordTests {
    /// <summary>
    /// Verifies that a whole file appended with <see cref="SegmentBuilder.AddFileRecord(string)"/>
    /// reads back byte-for-byte, under both chunk source kinds.
    /// </summary>
    /// <param name="kind">The chunk source kind to use when opening the artifact.</param>
    [Theory]
    [InlineData(ChunkSourceKind.Mapped)]
    [InlineData(ChunkSourceKind.RandomAccess)]
    public void FileRecord_RoundTripsIdentically(ChunkSourceKind kind) {
        byte[] content = TestArtifacts.Pattern(9001, 3);
        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, content);

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        SegmentedArtifactWriter writer = new();
        writer.AddSegment().AddFileRecord(source);
        writer.Save(path);

        ArtifactOpenOptions options = new() {
            ChunkSource = kind
        };
        using Artifact artifact = Artifact.Open(path, options);

        Assert.Equal(1, artifact.RecordCount);
        using RecordLease lease = artifact.ReadRecord(0);
        Assert.Equal(content, lease.ToArray());
    }

    /// <summary>
    /// Verifies that a segment containing a streamed record is written payload-first, while a
    /// segment of buffered records keeps the directory-first layout.
    /// </summary>
    [Fact]
    public void StreamedSegment_IsLaidOutPayloadFirst() {
        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, TestArtifacts.Pattern(512, 11));

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        SegmentedArtifactWriter writer = new();
        writer.AddSegment(1u).AddRecord(TestArtifacts.Pattern(128, 5));
        writer.AddSegment(2u).AddFileRecord(source);
        writer.Save(path);

        using Artifact artifact = Artifact.Open(path);

        Assert.False(artifact.Segments[0].IsPayloadFirst);
        Assert.True(artifact.Segments[1].IsPayloadFirst);
    }

    /// <summary>
    /// Verifies that streaming a file as one record and buffering the same bytes as one record
    /// produce artifacts whose records are indistinguishable to a reader.
    /// </summary>
    [Fact]
    public void StreamedAndBuffered_ProduceEquivalentRecords() {
        byte[] content = TestArtifacts.Pattern(20_000, 42);

        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, content);

        string streamedPath = TestArtifacts.NewTempPath();
        using TempFile streamedTemp = new(streamedPath);
        SegmentedArtifactWriter streamedWriter = new();
        streamedWriter.AddSegment().AddFileRecord(source);
        streamedWriter.Save(streamedPath);

        string bufferedPath = TestArtifacts.WriteSingleSegment([content]);
        using TempFile bufferedTemp = new(bufferedPath);

        using Artifact streamed = Artifact.Open(streamedPath);
        using Artifact buffered = Artifact.Open(bufferedPath);

        using RecordLease streamedLease = streamed.ReadRecord(0);
        using RecordLease bufferedLease = buffered.ReadRecord(0);

        Assert.Equal(bufferedLease.ToArray(), streamedLease.ToArray());
        Assert.Equal(bufferedLease.Length, streamedLease.Length);
    }

    /// <summary>
    /// Verifies that a file split across several records reassembles into the original bytes,
    /// which is how inputs larger than <see cref="ArtifactFormat.MaxRecordLength"/> are stored.
    /// </summary>
    [Fact]
    public void ChunkedFile_ReassemblesToOriginal() {
        const int ChunkSize = 3000;
        byte[] content = TestArtifacts.Pattern(10_000, 77);

        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, content);

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        SegmentedArtifactWriter writer = new();
        SegmentBuilder segment = writer.AddSegment();
        for (long offset = 0; offset < content.Length; offset += ChunkSize) {
            long length = Math.Min(ChunkSize, content.Length - offset);
            segment.AddFileRecord(source, offset, length);
        }

        writer.Save(path);

        using Artifact artifact = Artifact.Open(path);
        Assert.Equal(4, artifact.RecordCount);

        using MemoryStream reassembled = new();
        for (int i = 0; i < artifact.RecordCount; i++) {
            using RecordLease lease = artifact.ReadRecord(i);
            reassembled.Write(lease.ToArray());
        }

        Assert.Equal(content, reassembled.ToArray());
    }

    /// <summary>
    /// Verifies that buffered and streamed records can be interleaved within a single segment.
    /// </summary>
    [Fact]
    public void MixedRecords_InOneSegment_RoundTrip() {
        byte[] buffered = TestArtifacts.Pattern(300, 9);
        byte[] fileContent = TestArtifacts.Pattern(700, 21);

        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, fileContent);

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        SegmentedArtifactWriter writer = new();
        writer.AddSegment()
            .AddRecord(buffered)
            .AddFileRecord(source)
            .AddRecord(buffered);
        writer.Save(path);

        using Artifact artifact = Artifact.Open(path);
        Assert.Equal(3, artifact.RecordCount);

        using (RecordLease first = artifact.ReadRecord(0)) {
            Assert.Equal(buffered, first.ToArray());
        }

        using (RecordLease second = artifact.ReadRecord(1)) {
            Assert.Equal(fileContent, second.ToArray());
        }

        using (RecordLease third = artifact.ReadRecord(2)) {
            Assert.Equal(buffered, third.ToArray());
        }
    }

    /// <summary>
    /// Verifies that an empty file streams as a zero-length record.
    /// </summary>
    [Fact]
    public void EmptyFileRecord_RoundTrips() {
        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, []);

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        SegmentedArtifactWriter writer = new();
        writer.AddSegment().AddFileRecord(source);
        writer.Save(path);

        using Artifact artifact = Artifact.Open(path);
        using RecordLease lease = artifact.ReadRecord(0);
        Assert.Equal(0, lease.Length);
    }

    /// <summary>
    /// Verifies that appending a record larger than <see cref="ArtifactFormat.MaxRecordLength"/>
    /// fails immediately, rather than producing an artifact that cannot be opened.
    /// </summary>
    [Fact]
    public void OversizedRecord_IsRejectedOnAppend() {
        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, TestArtifacts.Pattern(16, 1));

        SegmentedArtifactWriter writer = new();
        SegmentBuilder segment = writer.AddSegment();

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => segment.AddFileRecord(source, 0, ArtifactFormat.MaxRecordLength + 1));

        Assert.Contains("split the input across multiple records", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a range extending past the end of the backing file is rejected on append.
    /// </summary>
    [Fact]
    public void RangePastEndOfFile_IsRejectedOnAppend() {
        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, TestArtifacts.Pattern(100, 1));

        SegmentedArtifactWriter writer = new();
        SegmentBuilder segment = writer.AddSegment();

        Assert.Throws<ArgumentOutOfRangeException>(() => segment.AddFileRecord(source, 50, 100));
    }

    /// <summary>
    /// Verifies that appending a record from a missing file fails with a clear exception.
    /// </summary>
    [Fact]
    public void MissingFile_IsRejectedOnAppend() {
        SegmentedArtifactWriter writer = new();
        SegmentBuilder segment = writer.AddSegment();

        Assert.Throws<FileNotFoundException>(() => segment.AddFileRecord(TestArtifacts.NewTempPath()));
    }

    /// <summary>
    /// Verifies that segment checksums computed during the single streaming pass validate when the
    /// artifact is reopened with checksum verification enabled.
    /// </summary>
    [Fact]
    public void StreamedSegment_ChecksumsVerifyOnRead() {
        string source = TestArtifacts.NewTempPath();
        using TempFile sourceTemp = new(source);
        File.WriteAllBytes(source, TestArtifacts.Pattern(5000, 13));

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        SegmentedArtifactWriter writer = new();
        writer.AddSegment().AddFileRecord(source).AddFileRecord(source, 100, 400);
        writer.Save(path);

        ArtifactOpenOptions options = new() {
            VerifyChecksums = true
        };
        using Artifact artifact = Artifact.Open(path, options);

        Assert.Equal(2, artifact.RecordCount);

        using (RecordLease whole = artifact.ReadRecord(0)) {
            Assert.Equal(5000, whole.Length);
        }

        using (RecordLease slice = artifact.ReadRecord(1)) {
            Assert.Equal(400, slice.Length);
            Assert.Equal(TestArtifacts.Pattern(5000, 13).AsSpan(100, 400).ToArray(), slice.ToArray());
        }
    }
}
