// ============================================================================
// Cartograph
// File: StreamingArtifactWriterTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests that StreamingArtifactWriter produces artifacts the existing reader
// accepts, that it is byte-for-byte equivalent to the batch writer for the same
// payload-first input, and that it ingests sources of unknown length without
// buffering them.
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
/// Tests for <see cref="StreamingArtifactWriter"/>, covering round-tripping, layout equivalence with
/// the batch writer, ingestion of sources whose length is not known in advance, and the guard rails
/// around segment and artifact lifetime.
/// </summary>
public class StreamingArtifactWriterTests
{
    /// <summary>
    /// Verifies that records appended as spans read back byte-for-byte under both chunk source
    /// kinds, with checksum verification enabled.
    /// </summary>
    /// <param name="kind">The chunk source kind to use when opening the artifact.</param>
    [Theory]
    [InlineData(ChunkSourceKind.Mapped)]
    [InlineData(ChunkSourceKind.RandomAccess)]
    public void SpanRecords_RoundTrip(ChunkSourceKind kind)
    {
        byte[][] payloads =
        [
            TestArtifacts.Pattern(1, 7),
            TestArtifacts.Pattern(63, 11),
            TestArtifacts.Pattern(64, 13),
            TestArtifacts.Pattern(65, 17),
            TestArtifacts.Pattern(9_001, 19),
        ];

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        using (StreamingArtifactWriter writer = new(path))
        {
            using (StreamingSegment segment = writer.BeginSegment())
            {
                for (int i = 0; i < payloads.Length; i++)
                {
                    Assert.Equal(i, segment.AppendRecord(payloads[i]));
                }
            }

            writer.Complete();
        }

        ArtifactOpenOptions options = new() { ChunkSource = kind, VerifyChecksums = true };
        using Artifact artifact = Artifact.Open(path, options);

        Assert.Equal(payloads.Length, artifact.RecordCount);
        for (int i = 0; i < payloads.Length; i++)
        {
            using RecordLease lease = artifact.ReadRecord(i);
            Assert.Equal(payloads[i], lease.ToArray());
        }
    }

    /// <summary>
    /// Verifies that a forward-only source of unknown length is ingested correctly, which is the
    /// case the batch writer cannot express.
    /// </summary>
    [Fact]
    public void UnknownLengthStream_RoundTrips()
    {
        byte[] first = TestArtifacts.Pattern(5_000, 3);
        byte[] second = TestArtifacts.Pattern(1_500_000, 23);

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        using (StreamingArtifactWriter writer = new(path))
        {
            using (StreamingSegment segment = writer.BeginSegment())
            {
                segment.AppendRecord(new UnknownLengthStream(first));
                segment.AppendRecord(new UnknownLengthStream(second));
            }

            writer.Complete();
        }

        using Artifact artifact = Artifact.Open(path);

        Assert.Equal(2, artifact.RecordCount);
        using RecordLease a = artifact.ReadRecord(0);
        using RecordLease b = artifact.ReadRecord(1);
        Assert.Equal(first, a.ToArray());
        Assert.Equal(second, b.ToArray());
    }

    /// <summary>
    /// Verifies that the asynchronous ingestion path produces the same artifact as the synchronous
    /// one, so network-backed sources need not block a thread.
    /// </summary>
    /// <returns>A task that completes when the assertion has run.</returns>
    [Fact]
    public async Task AsyncStreamAppend_RoundTrips()
    {
        byte[] payload = TestArtifacts.Pattern(300_000, 29);

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        using (StreamingArtifactWriter writer = new(path))
        {
            using (StreamingSegment segment = writer.BeginSegment())
            {
                Assert.Equal(0, await segment.AppendRecordAsync(new UnknownLengthStream(payload)));
            }

            writer.Complete();
        }

        using Artifact artifact = Artifact.Open(path);
        using RecordLease lease = artifact.ReadRecord(0);
        Assert.Equal(payload, lease.ToArray());
    }

    /// <summary>
    /// Verifies that a record serialized through <see cref="IBufferWriter{T}"/> round-trips, including
    /// when the callback forces the staging buffer to drain and to grow beyond its initial size.
    /// </summary>
    [Fact]
    public void BufferWriterRecord_RoundTrips()
    {
        byte[] small = TestArtifacts.Pattern(300, 31);
        byte[] large = TestArtifacts.Pattern(400_000, 37);
        byte[] oversizedHint = TestArtifacts.Pattern(200_000, 41);

        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        using (StreamingArtifactWriter writer = new(path))
        {
            using (StreamingSegment segment = writer.BeginSegment())
            {
                // Many small writes, exercising repeated drains of the staging buffer.
                segment.AppendRecord(w =>
                {
                    for (int i = 0; i < large.Length; i += small.Length)
                    {
                        int take = Math.Min(small.Length, large.Length - i);
                        large.AsSpan(i, take).CopyTo(w.GetSpan(take));
                        w.Advance(take);
                    }
                });

                // A single request larger than the initial buffer, forcing a grow.
                segment.AppendRecord(w =>
                {
                    oversizedHint.CopyTo(w.GetSpan(oversizedHint.Length));
                    w.Advance(oversizedHint.Length);
                });
            }

            writer.Complete();
        }

        using Artifact artifact = Artifact.Open(path);

        using RecordLease a = artifact.ReadRecord(0);
        using RecordLease b = artifact.ReadRecord(1);
        Assert.Equal(large, a.ToArray());
        Assert.Equal(oversizedHint, b.ToArray());
    }

    /// <summary>
    /// Verifies that the streaming writer produces a byte-for-byte identical artifact to the batch
    /// writer given the same records, confirming it is a second write path and not a second format.
    /// </summary>
    [Fact]
    public void StreamedArtifact_IsByteIdenticalToBatchWriter()
    {
        byte[][] first = [TestArtifacts.Pattern(1_000, 2), TestArtifacts.Pattern(4_096, 4)];
        byte[][] second = [TestArtifacts.Pattern(77, 6)];

        string sourceDirectory = Path.Combine(Path.GetTempPath(), $"cartograph-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            string[] firstPaths = WriteAll(sourceDirectory, first, "a");
            string[] secondPaths = WriteAll(sourceDirectory, second, "b");

            string batchPath = TestArtifacts.NewTempPath();
            string streamedPath = TestArtifacts.NewTempPath();
            using TempFile batchTemp = new(batchPath);
            using TempFile streamedTemp = new(streamedPath);

            // The batch writer only chooses payload-first layout for file-backed records, so this is
            // the input shape where the two writers are expected to agree exactly.
            SegmentedArtifactWriter batch = new();
            SegmentBuilder batchFirst = batch.AddSegment();
            foreach (string p in firstPaths)
            {
                batchFirst.AddFileRecord(p);
            }

            SegmentBuilder batchSecond = batch.AddSegment();
            foreach (string p in secondPaths)
            {
                batchSecond.AddFileRecord(p);
            }

            batch.Save(batchPath);

            using (StreamingArtifactWriter streamed = new(streamedPath))
            {
                AppendFiles(streamed, firstPaths);
                AppendFiles(streamed, secondPaths);
                streamed.Complete();
            }

            Assert.Equal(File.ReadAllBytes(batchPath), File.ReadAllBytes(streamedPath));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    /// <summary>Verifies that a segment holding no records produces a readable, empty segment.</summary>
    [Fact]
    public void EmptySegment_IsValid()
    {
        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        using (StreamingArtifactWriter writer = new(path))
        {
            using (writer.BeginSegment())
            {
            }

            writer.Complete();
        }

        using Artifact artifact = Artifact.Open(path);
        Assert.Single(artifact.Segments);
        Assert.Equal(0, artifact.RecordCount);
    }

    /// <summary>Verifies that segment ids and record indices are preserved across multiple segments.</summary>
    [Fact]
    public void MultipleSegments_PreserveIdsAndOrder()
    {
        string path = TestArtifacts.NewTempPath();
        using TempFile temp = new(path);

        using (StreamingArtifactWriter writer = new(path))
        {
            using (StreamingSegment a = writer.BeginSegment(10u))
            {
                a.AppendRecord(TestArtifacts.Pattern(100, 1));
                a.AppendRecord(TestArtifacts.Pattern(200, 2));
            }

            using (StreamingSegment b = writer.BeginSegment(20u))
            {
                b.AppendRecord(TestArtifacts.Pattern(300, 3));
            }

            Assert.Equal(2, writer.SegmentCount);
            writer.Complete();
        }

        using Artifact artifact = Artifact.Open(path);

        Assert.Equal(2, artifact.Segments.Count);
        Assert.Equal(10u, artifact.Segments[0].SegmentId);
        Assert.Equal(20u, artifact.Segments[1].SegmentId);
        Assert.Equal(3, artifact.RecordCount);
    }

    /// <summary>Verifies that opening a second segment while one is still open is rejected.</summary>
    [Fact]
    public void BeginSegment_WhileSegmentOpen_Throws()
    {
        using MemoryStream destination = new();
        using StreamingArtifactWriter writer = new(destination);

        using StreamingSegment open = writer.BeginSegment();
        Assert.Throws<InvalidOperationException>(() => writer.BeginSegment());
    }

    /// <summary>Verifies that completing the artifact while a segment is open is rejected.</summary>
    [Fact]
    public void Complete_WithOpenSegment_Throws()
    {
        using MemoryStream destination = new();
        using StreamingArtifactWriter writer = new(destination);

        using StreamingSegment open = writer.BeginSegment();
        Assert.Throws<InvalidOperationException>(writer.Complete);
    }

    /// <summary>Verifies that appending to a segment after it has been completed is rejected.</summary>
    [Fact]
    public void AppendRecord_AfterSegmentCompleted_Throws()
    {
        using MemoryStream destination = new();
        using StreamingArtifactWriter writer = new(destination);

        StreamingSegment segment = writer.BeginSegment();
        segment.Complete();

        Assert.Throws<InvalidOperationException>(() => segment.AppendRecord(new byte[4]));
    }

    /// <summary>Verifies that a non-seekable destination is rejected, since the header is patched last.</summary>
    [Fact]
    public void NonSeekableDestination_Throws()
    {
        using NonSeekableStream destination = new();
        Assert.Throws<ArgumentException>(() => new StreamingArtifactWriter(destination));
    }

    /// <summary>Writes each payload to its own file and returns the resulting paths in order.</summary>
    /// <param name="directory">The directory to write the files into.</param>
    /// <param name="payloads">The payloads to write, one file per entry.</param>
    /// <param name="prefix">A prefix distinguishing this batch of files.</param>
    /// <returns>The paths of the written files, in the order the payloads were supplied.</returns>
    private static string[] WriteAll(string directory, byte[][] payloads, string prefix)
    {
        string[] paths = new string[payloads.Length];
        for (int i = 0; i < payloads.Length; i++)
        {
            paths[i] = Path.Combine(directory, $"{prefix}-{i}.bin");
            File.WriteAllBytes(paths[i], payloads[i]);
        }

        return paths;
    }

    /// <summary>Appends each file as one record of a new segment, streaming it from disk.</summary>
    /// <param name="writer">The writer to append the segment to.</param>
    /// <param name="paths">The paths of the files to append, in record order.</param>
    private static void AppendFiles(StreamingArtifactWriter writer, string[] paths)
    {
        using StreamingSegment segment = writer.BeginSegment();
        foreach (string path in paths)
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            segment.AppendRecord(file);
        }
    }

    /// <summary>
    /// A forward-only stream that hides its length and returns short reads, standing in for a
    /// network response body whose size is not known before it has been consumed.
    /// </summary>
    /// <param name="payload">The bytes the stream yields.</param>
    private sealed class UnknownLengthStream(byte[] payload) : Stream
    {
        /// <summary>The bytes this stream yields.</summary>
        private readonly byte[] _payload = payload;

        /// <summary>The number of bytes yielded so far.</summary>
        private int _position;

        /// <summary>Always <see langword="true"/>; the stream is readable.</summary>
        /// <value>Always <see langword="true"/>; the stream is readable.</value>
        public override bool CanRead => true;

        /// <summary>Always <see langword="false"/>; the stream is forward-only.</summary>
        /// <value>Always <see langword="false"/>; the stream is forward-only.</value>
        public override bool CanSeek => false;

        /// <summary>Always <see langword="false"/>; the stream is read-only.</summary>
        /// <value>Always <see langword="false"/>; the stream is read-only.</value>
        public override bool CanWrite => false;

        /// <summary>Always throws, because the length is deliberately unknown.</summary>
        /// <value>Always throws, because the length is deliberately unknown.</value>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override long Length => throw new NotSupportedException();

        /// <summary>Always throws, because the stream is forward-only.</summary>
        /// <value>Always throws, because the stream is forward-only.</value>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Does nothing; there is no destination to flush.</summary>
        public override void Flush()
        {
        }

        /// <summary>Reads at most 7,777 bytes at a time, so callers must handle short reads.</summary>
        /// <param name="buffer">The buffer to read into.</param>
        /// <param name="offset">The offset within <paramref name="buffer"/> to begin writing at.</param>
        /// <param name="count">The maximum number of bytes to read.</param>
        /// <returns>The number of bytes read, or zero at end of stream.</returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(count, 7_777), _payload.Length - _position);
            if (take <= 0)
            {
                return 0;
            }

            Array.Copy(_payload, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        /// <summary>Always throws, because the stream is forward-only.</summary>
        /// <param name="offset">Ignored.</param>
        /// <param name="origin">Ignored.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>Always throws, because the stream cannot be resized.</summary>
        /// <param name="value">Ignored.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Always throws, because the stream is read-only.</summary>
        /// <param name="buffer">Ignored.</param>
        /// <param name="offset">Ignored.</param>
        /// <param name="count">Ignored.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A writable stream that reports itself as non-seekable, used to test the guard.</summary>
    private sealed class NonSeekableStream : Stream
    {
        /// <summary>Always <see langword="false"/>; the stream is write-only.</summary>
        /// <value>Always <see langword="false"/>; the stream is write-only.</value>
        public override bool CanRead => false;

        /// <summary>Always <see langword="false"/>; this is the behaviour under test.</summary>
        /// <value>Always <see langword="false"/>; this is the behaviour under test.</value>
        public override bool CanSeek => false;

        /// <summary>Always <see langword="true"/>; the stream accepts writes.</summary>
        /// <value>Always <see langword="true"/>; the stream accepts writes.</value>
        public override bool CanWrite => true;

        /// <summary>Always throws, because the stream is not seekable.</summary>
        /// <value>Always throws, because the stream is not seekable.</value>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override long Length => throw new NotSupportedException();

        /// <summary>Always throws, because the stream is not seekable.</summary>
        /// <value>Always throws, because the stream is not seekable.</value>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Does nothing; writes are discarded.</summary>
        public override void Flush()
        {
        }

        /// <summary>Always throws, because the stream is write-only.</summary>
        /// <param name="buffer">Ignored.</param>
        /// <param name="offset">Ignored.</param>
        /// <param name="count">Ignored.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>Always throws, because the stream is not seekable.</summary>
        /// <param name="offset">Ignored.</param>
        /// <param name="origin">Ignored.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>Always throws, because the stream cannot be resized.</summary>
        /// <param name="value">Ignored.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Discards the supplied bytes.</summary>
        /// <param name="buffer">The buffer whose bytes are discarded.</param>
        /// <param name="offset">Ignored.</param>
        /// <param name="count">Ignored.</param>
        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
