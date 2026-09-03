// ============================================================================
// Cartograph
// File: RecordBufferWriter.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// An IBufferWriter<byte> that stages record bytes in a pooled buffer and drains
// them to the artifact destination as it fills, so serializers can write objects
// straight into a record without allocating a payload-sized array or string.
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
using System.IO.Hashing;

namespace Cartograph.Format;

/// <summary>
/// Stages record bytes in a pooled buffer and writes them through to the artifact destination each
/// time the buffer fills, folding them into the record and segment checksums on the way.
/// </summary>
/// <remarks>
/// This is what lets a serializer emit an object directly into a record. Peak managed memory is the
/// staging buffer, not the size of the serialized value, so ingesting a large object graph never
/// produces a large object heap allocation.
/// </remarks>
internal sealed class RecordBufferWriter : IBufferWriter<byte>, IDisposable
{
    /// <summary>The initial size of the pooled staging buffer.</summary>
    private const int DefaultBufferSize = 64 * 1024;

    /// <summary>The writer whose destination staged bytes are drained to.</summary>
    private readonly StreamingArtifactWriter _writer;

    /// <summary>The hasher accumulating the enclosing segment's checksum.</summary>
    private readonly XxHash3 _segmentHasher;

    /// <summary>The hasher accumulating this record's checksum.</summary>
    private readonly XxHash3 _recordHasher;

    /// <summary>The pooled staging buffer.</summary>
    private byte[] _buffer;

    /// <summary>The number of bytes staged in <see cref="_buffer"/> but not yet drained.</summary>
    private int _index;

    /// <summary>The number of bytes already drained to the destination.</summary>
    private long _flushed;

    /// <summary>Whether this writer has been disposed.</summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordBufferWriter" /> class.
    /// </summary>
    /// <param name="writer">The writer whose destination staged bytes are drained to.</param>
    /// <param name="segmentHasher">The hasher accumulating the enclosing segment's checksum.</param>
    /// <param name="recordHasher">The hasher accumulating this record's checksum.</param>
    public RecordBufferWriter(StreamingArtifactWriter writer, XxHash3 segmentHasher, XxHash3 recordHasher)
    {
        _writer = writer;
        _segmentHasher = segmentHasher;
        _recordHasher = recordHasher;
        _buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
    }

    /// <summary>The total number of record bytes accepted so far, drained and staged.</summary>
    public long BytesWritten => _flushed + _index;

    /// <summary>Marks <paramref name="count"/> staged bytes as written by the caller.</summary>
    /// <param name="count">The number of bytes written into the span previously returned.</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="count"/> is negative or exceeds the staged capacity.</exception>
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _index);
        _index += count;
    }

    /// <summary>Returns a writable span of at least <paramref name="sizeHint"/> bytes.</summary>
    /// <param name="sizeHint">The minimum number of contiguous bytes required; 0 requests any non-empty span.</param>
    /// <returns>A writable span into the staging buffer.</returns>
    /// <exception cref="System.ObjectDisposedException">The writer has been disposed.</exception>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_index);
    }

    /// <summary>Returns a writable span of at least <paramref name="sizeHint"/> bytes.</summary>
    /// <param name="sizeHint">The minimum number of contiguous bytes required; 0 requests any non-empty span.</param>
    /// <returns>A writable span into the staging buffer.</returns>
    /// <exception cref="System.ObjectDisposedException">The writer has been disposed.</exception>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_index);
    }

    /// <summary>Drains any staged bytes to the destination and folds them into both checksums.</summary>
    /// <exception cref="System.InvalidOperationException">The record exceeds <see cref="ArtifactFormat.MaxRecordLength"/> bytes.</exception>
    public void Flush()
    {
        if (_index == 0)
        {
            return;
        }

        ReadOnlySpan<byte> staged = _buffer.AsSpan(0, _index);
        _writer.WriteRaw(staged);
        _segmentHasher.Append(staged);
        _recordHasher.Append(staged);

        _flushed += _index;
        _index = 0;

        if (_flushed > ArtifactFormat.MaxRecordLength)
        {
            throw new InvalidOperationException(
                $"A single record cannot exceed {ArtifactFormat.MaxRecordLength} bytes; " +
                "split the input across multiple records.");
        }
    }

    /// <summary>Returns the pooled staging buffer without draining it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    /// <summary>
    /// Ensures at least <paramref name="sizeHint"/> contiguous bytes are available, draining and if
    /// necessary growing the staging buffer.
    /// </summary>
    /// <param name="sizeHint">The minimum number of contiguous bytes required.</param>
    /// <exception cref="System.ObjectDisposedException">The writer has been disposed.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="sizeHint"/> is negative.</exception>
    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        int required = sizeHint == 0 ? 1 : sizeHint;
        if (_buffer.Length - _index >= required)
        {
            return;
        }

        Flush();

        if (_buffer.Length < required)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, _buffer.Length * 2));
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }
    }
}
