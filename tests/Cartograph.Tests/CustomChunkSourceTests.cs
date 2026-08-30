// ============================================================================
// Cartograph
// File: CustomChunkSourceTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Reference in-memory IChunkSource implementation plus tests covering the
// custom chunk source extension point: opening from a caller-supplied source,
// source ownership, async open and reads, and enforcement of the source
// contract against misbehaving implementations.
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
/// Verifies that an artifact can be opened from a caller-supplied <see cref="IChunkSource"/>,
/// covering the extension point that lets artifacts live somewhere other than the local file system.
/// </summary>
public class CustomChunkSourceTests
{
    /// <summary>
    /// Verifies that an artifact backed by a custom in-memory source returns exactly the same
    /// records as the same artifact opened from disk.
    /// </summary>
    [Fact]
    public void CustomSource_ReadsSameRecordsAsFileSource()
    {
        byte[][] records = [TestArtifacts.Pattern(64, 1), TestArtifacts.Pattern(4096, 7), TestArtifacts.Pattern(11, 200)];
        using TempFile file = new(TestArtifacts.WriteSingleSegment(records));

        using InMemoryChunkSource source = new(File.ReadAllBytes(file.Path));
        using Artifact artifact = Artifact.Open(source);

        Assert.Equal(records.Length, artifact.RecordCount);
        for (int i = 0; i < records.Length; i++)
        {
            using RecordLease lease = artifact.ReadRecord(i);
            Assert.Equal(records[i], lease.ToArray());
        }
    }

    /// <summary>Verifies that a caller-supplied source is reported as <see cref="ChunkSourceKind.Custom"/>.</summary>
    [Fact]
    public void CustomSource_ReportsCustomSourceKind()
    {
        using TempFile file = new(TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(32, 3)]));

        using InMemoryChunkSource source = new(File.ReadAllBytes(file.Path));
        using Artifact artifact = Artifact.Open(source);

        Assert.Equal(ChunkSourceKind.Custom, artifact.SourceKind);
    }

    /// <summary>Verifies that opening from a path still reports the built-in strategy that was selected.</summary>
    /// <param name="kind">The chunk source strategy requested when opening.</param>
    [Theory]
    [InlineData(ChunkSourceKind.Mapped)]
    [InlineData(ChunkSourceKind.RandomAccess)]
    public void FileSource_ReportsBuiltInSourceKind(ChunkSourceKind kind)
    {
        using TempFile file = new(TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(32, 3)]));

        using Artifact artifact = Artifact.Open(file.Path, new ArtifactOpenOptions { ChunkSource = kind });

        Assert.Equal(kind, artifact.SourceKind);
    }

    /// <summary>Verifies that disposing the artifact disposes an owned source exactly once.</summary>
    [Fact]
    public void OwnedSource_IsDisposedWithArtifact()
    {
        using TempFile file = new(TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(32, 3)]));
        InMemoryChunkSource source = new(File.ReadAllBytes(file.Path));

        Artifact artifact = Artifact.Open(source, ownsSource: true);
        Assert.Equal(0, source.DisposeCount);

        artifact.Dispose();
        Assert.Equal(1, source.DisposeCount);

        artifact.Dispose();
        Assert.Equal(1, source.DisposeCount);
    }

    /// <summary>
    /// Verifies that a source opened with <c>ownsSource: false</c> outlives the artifact and stays
    /// usable, so one source can back several artifacts.
    /// </summary>
    [Fact]
    public void UnownedSource_SurvivesArtifactDisposal()
    {
        byte[] record = TestArtifacts.Pattern(128, 9);
        using TempFile file = new(TestArtifacts.WriteSingleSegment([record]));
        using InMemoryChunkSource source = new(File.ReadAllBytes(file.Path));

        Artifact first = Artifact.Open(source, ownsSource: false);
        first.Dispose();

        Assert.Equal(0, source.DisposeCount);

        using Artifact second = Artifact.Open(source, ownsSource: false);
        using RecordLease lease = second.ReadRecord(0);
        Assert.Equal(record, lease.ToArray());
    }

    /// <summary>Verifies that a failed open disposes the source only when the artifact owns it.</summary>
    /// <param name="ownsSource">Whether the artifact was asked to take ownership of the source.</param>
    /// <param name="expectedDisposeCount">The number of dispose calls expected after the failure.</param>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void FailedOpen_DisposesSourceOnlyWhenOwned(bool ownsSource, int expectedDisposeCount)
    {
        byte[] garbage = new byte[4096];
        InMemoryChunkSource source = new(garbage);

        Assert.Throws<CartographFormatException>(() => Artifact.Open(source, ownsSource: ownsSource));
        Assert.Equal(expectedDisposeCount, source.DisposeCount);
    }

    /// <summary>Verifies that a null source is rejected before any read is attempted.</summary>
    [Fact]
    public void NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Artifact.Open((IChunkSource)null!));
    }

    /// <summary>Verifies that a source too small to contain the fixed header fails cleanly.</summary>
    [Fact]
    public void SourceSmallerThanHeader_ThrowsCleanly()
    {
        using InMemoryChunkSource source = new(new byte[8]);

        Assert.Throws<CartographFormatException>(() => Artifact.Open(source));
    }

    /// <summary>
    /// Verifies that a source violating the exact-length read contract is caught by the format
    /// layer and surfaces as a clean <see cref="CartographFormatException"/> rather than corrupt data.
    /// </summary>
    [Fact]
    public void SourceReturningShortChunk_ThrowsCleanly()
    {
        using TempFile file = new(TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(32, 3)]));
        using ShortReadChunkSource source = new(File.ReadAllBytes(file.Path));

        Assert.Throws<CartographFormatException>(() => Artifact.Open(source));
    }

    /// <summary>
    /// Verifies that opening is O(1) with respect to payload size: only the header, manifest, and
    /// record directory are read, never the payload.
    /// </summary>
    [Fact]
    public void Open_DoesNotReadPayload()
    {
        byte[][] records = new byte[200][];
        for (int i = 0; i < records.Length; i++)
        {
            records[i] = TestArtifacts.Pattern(4096, (byte)i);
        }

        using TempFile file = new(TestArtifacts.WriteSingleSegment(records));
        byte[] bytes = File.ReadAllBytes(file.Path);

        using InMemoryChunkSource source = new(bytes);
        using Artifact artifact = Artifact.Open(source);

        Assert.True(
            source.BytesRead < bytes.Length / 10,
            $"Open read {source.BytesRead} of {bytes.Length} bytes; expected only header, manifest and directory.");
    }

    /// <summary>Verifies that the async open and async read paths work over a custom source.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task OpenAsync_ReadsRecordsAsynchronously()
    {
        byte[][] records = [TestArtifacts.Pattern(64, 11), TestArtifacts.Pattern(2048, 22)];
        using TempFile file = new(TestArtifacts.WriteSingleSegment(records));

        using InMemoryChunkSource source = new(File.ReadAllBytes(file.Path));
        using Artifact artifact = await Artifact.OpenAsync(source);

        Assert.Equal(ChunkSourceKind.Custom, artifact.SourceKind);
        for (int i = 0; i < records.Length; i++)
        {
            using RecordLease lease = await artifact.ReadRecordAsync(i);
            Assert.Equal(records[i], lease.ToArray());
        }
    }

    /// <summary>Verifies that the async open path over a file path produces a readable artifact.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task OpenAsync_FromPath_ReadsRecords()
    {
        byte[] record = TestArtifacts.Pattern(256, 5);
        using TempFile file = new(TestArtifacts.WriteSingleSegment([record]));

        using Artifact artifact = await Artifact.OpenAsync(file.Path);
        using RecordLease lease = await artifact.ReadRecordAsync(0);

        Assert.Equal(record, lease.ToArray());
    }

    /// <summary>Verifies that async open reports corruption cleanly rather than faulting unexpectedly.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task OpenAsync_OnGarbage_ThrowsCleanly()
    {
        InMemoryChunkSource source = new(new byte[4096]);

        await Assert.ThrowsAsync<CartographFormatException>(async () => await Artifact.OpenAsync(source));
        Assert.Equal(1, source.DisposeCount);
    }
}

/// <summary>
/// A minimal reference <see cref="IChunkSource"/> over a byte array, serving as both a test double
/// and a worked example of the implementer contract documented on <see cref="IChunkSource"/>.
/// </summary>
/// <remarks>
/// A real remote source (HTTP range requests, object storage) has the same shape: report a stable
/// <see cref="Length"/>, validate the requested range, and return exactly the requested bytes wrapped
/// in a <see cref="ChunkLease"/>. Reads here are naturally thread-safe because the backing array is
/// never mutated.
/// </remarks>
internal sealed class InMemoryChunkSource : IChunkSource
{
    /// <summary>The immutable bytes backing this source.</summary>
    private readonly byte[] _bytes;

    /// <summary>Running total of bytes handed out, used to assert that opening does not read payload.</summary>
    private long _bytesRead;

    /// <summary>Number of times <see cref="Dispose"/> has been called.</summary>
    private int _disposeCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryChunkSource" /> class.
    /// </summary>
    /// <param name="bytes">The complete artifact bytes to serve.</param>
    public InMemoryChunkSource(byte[] bytes) => _bytes = bytes;

    /// <inheritdoc />
    public long Length => _bytes.Length;

    /// <summary>The total number of bytes returned by this source so far.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>The number of times this source has been disposed.</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The requested range falls outside <c>[0, Length)</c>.</exception>
    public ChunkLease Read(long offset, int length)
    {
        ValidateRange(offset, length);
        Interlocked.Add(ref _bytesRead, length);
        return new ChunkLease(new ReadOnlySequence<byte>(_bytes, (int)offset, length), owner: null);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The requested range falls outside <c>[0, Length)</c>.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    public ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(offset, length));
    }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Increment(ref _disposeCount);

    /// <summary>Validates that the requested range lies entirely within the source.</summary>
    /// <param name="offset">The absolute byte offset requested.</param>
    /// <param name="length">The number of bytes requested.</param>
    /// <exception cref="ArgumentOutOfRangeException">The requested range falls outside <c>[0, Length)</c>.</exception>
    private void ValidateRange(long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > _bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Requested range extends past the end of the source.");
        }
    }
}

/// <summary>
/// A deliberately broken <see cref="IChunkSource"/> that violates the exact-length read contract by
/// returning one byte fewer than requested, used to prove the format layer detects it.
/// </summary>
internal sealed class ShortReadChunkSource : IChunkSource
{
    /// <summary>The bytes backing this source.</summary>
    private readonly byte[] _bytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShortReadChunkSource" /> class.
    /// </summary>
    /// <param name="bytes">The complete artifact bytes to serve.</param>
    public ShortReadChunkSource(byte[] bytes) => _bytes = bytes;

    /// <inheritdoc />
    public long Length => _bytes.Length;

    /// <inheritdoc />
    public ChunkLease Read(long offset, int length)
    {
        int shortened = Math.Max(0, length - 1);
        return new ChunkLease(new ReadOnlySequence<byte>(_bytes, (int)offset, shortened), owner: null);
    }

    /// <inheritdoc />
    public ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(offset, length));

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
