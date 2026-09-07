// ============================================================================
// Cartograph
// File: StreamingSegment.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A segment that is written incrementally by StreamingArtifactWriter. Record
// payloads are emitted to the destination as they are appended and the record
// directory is written when the segment closes, so records of unknown length
// can be ingested without ever being buffered on the managed heap.
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
using System.Buffers.Binary;
using System.IO.Hashing;

namespace Cartograph.Format;

/// <summary>
/// A segment being written incrementally by a <see cref="StreamingArtifactWriter"/>. Each appended
/// record's payload is emitted immediately; only its offset, length and checksum are retained so the
/// record directory can be written when the segment closes.
/// </summary>
/// <remarks>
/// Because the directory follows the payload, a record's length does not have to be known before it
/// is appended. That is what allows a forward-only source of unknown size, such as an HTTP response
/// body, to be ingested directly into an artifact.
/// </remarks>
public sealed class StreamingSegment : IDisposable
{
    /// <summary>The size of the pooled staging buffer used to pump stream-backed records.</summary>
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>The writer that owns this segment and its destination.</summary>
    private readonly StreamingArtifactWriter _writer;

    /// <summary>The file-relative byte offset at which this segment's data region begins.</summary>
    private readonly long _dataOffset;

    /// <summary>The hasher accumulating this segment's checksum over its entire data region.</summary>
    private readonly XxHash3 _hasher = new();

    /// <summary>The directory state accumulated so far, one entry per appended record.</summary>
    private readonly List<RecordEntry> _entries = [];

    /// <summary>Whether this segment has been closed.</summary>
    private bool _completed;

    /// <summary>Whether this segment was abandoned because the writer was disposed early.</summary>
    private bool _abandoned;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingSegment" /> class.
    /// </summary>
    /// <param name="writer">The writer that owns this segment.</param>
    /// <param name="id">The stable numeric identifier for this segment.</param>
    /// <param name="dataOffset">The file-relative offset at which the segment's data region begins.</param>
    internal StreamingSegment(StreamingArtifactWriter writer, uint id, long dataOffset)
    {
        _writer = writer;
        Id = id;
        _dataOffset = dataOffset;
    }

    /// <summary>The stable numeric identifier assigned to this segment.</summary>
    /// <value>The stable numeric identifier assigned to this segment.</value>
    public uint Id { get; }

    /// <summary>The number of records appended so far.</summary>
    /// <value>The number of records appended so far.</value>
    public int RecordCount => _entries.Count;

    /// <summary>Appends a record whose bytes are already in memory, writing them through immediately.</summary>
    /// <param name="data">The raw bytes of the record to append.</param>
    /// <returns>The zero-based index of the appended record within this segment.</returns>
    /// <exception cref="System.InvalidOperationException">The segment has already been completed.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="data"/> is longer than <see cref="ArtifactFormat.MaxRecordLength"/>.</exception>
    public int AppendRecord(ReadOnlySpan<byte> data)
    {
        EnsureOpen();

        if (data.Length > ArtifactFormat.MaxRecordLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                data.Length,
                $"A single record cannot exceed {ArtifactFormat.MaxRecordLength} bytes; " +
                "split the input across multiple records.");
        }

        long relOffset = _writer.Position - _dataOffset;
        ulong checksum = XxHash3.HashToUInt64(data);

        _writer.WriteRaw(data);
        _hasher.Append(data);

        return CloseRecord(relOffset, data.Length, checksum);
    }

    /// <summary>
    /// Appends a record by pumping <paramref name="source"/> to end of stream. The length does not
    /// have to be known in advance and no payload-sized buffer is allocated.
    /// </summary>
    /// <param name="source">The forward-only source to read the record's bytes from.</param>
    /// <returns>The zero-based index of the appended record within this segment.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
    /// <exception cref="System.InvalidOperationException">The segment has already been completed.</exception>
    /// <exception cref="System.InvalidOperationException">The source produced more than <see cref="ArtifactFormat.MaxRecordLength"/> bytes.</exception>
    public int AppendRecord(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureOpen();

        long relOffset = _writer.Position - _dataOffset;
        XxHash3 recordHasher = new();
        long length = 0;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = source.Read(scratch, 0, scratch.Length)) > 0)
            {
                length = CheckedAdd(length, read);
                ReadOnlySpan<byte> chunk = scratch.AsSpan(0, read);
                _writer.WriteRaw(chunk);
                _hasher.Append(chunk);
                recordHasher.Append(chunk);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        return CloseRecord(relOffset, length, recordHasher.GetCurrentHashAsUInt64());
    }

    /// <summary>
    /// Asynchronously appends a record by pumping <paramref name="source"/> to end of stream, so a
    /// network-backed source can be ingested without blocking a thread.
    /// </summary>
    /// <param name="source">The forward-only source to read the record's bytes from.</param>
    /// <param name="cancellationToken">A token used to cancel the ingestion.</param>
    /// <returns>The zero-based index of the appended record within this segment.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
    /// <exception cref="System.InvalidOperationException">The segment has already been completed.</exception>
    /// <exception cref="System.InvalidOperationException">The source produced more than <see cref="ArtifactFormat.MaxRecordLength"/> bytes.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public async ValueTask<int> AppendRecordAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureOpen();

        long relOffset = _writer.Position - _dataOffset;
        XxHash3 recordHasher = new();
        long length = 0;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(scratch.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                length = CheckedAdd(length, read);
                ReadOnlyMemory<byte> chunk = scratch.AsMemory(0, read);
                await _writer.WriteRawAsync(chunk, cancellationToken).ConfigureAwait(false);
                _hasher.Append(chunk.Span);
                recordHasher.Append(chunk.Span);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        return CloseRecord(relOffset, length, recordHasher.GetCurrentHashAsUInt64());
    }

    /// <summary>
    /// Appends a record produced by <paramref name="write"/>, which serializes directly into a
    /// pooled buffer that drains to the destination as it fills. This is the entry point for
    /// ingesting objects, since no intermediate array or string is materialized.
    /// </summary>
    /// <param name="write">A callback that writes the record's bytes to the supplied buffer writer.</param>
    /// <returns>The zero-based index of the appended record within this segment.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="write"/> is <c>null</c>.</exception>
    /// <exception cref="System.InvalidOperationException">The segment has already been completed.</exception>
    /// <exception cref="System.InvalidOperationException">The callback produced more than <see cref="ArtifactFormat.MaxRecordLength"/> bytes.</exception>
    public int AppendRecord(Action<IBufferWriter<byte>> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        EnsureOpen();

        long relOffset = _writer.Position - _dataOffset;
        XxHash3 recordHasher = new();

        using RecordBufferWriter buffer = new(_writer, _hasher, recordHasher);
        write(buffer);
        buffer.Flush();

        return CloseRecord(relOffset, buffer.BytesWritten, recordHasher.GetCurrentHashAsUInt64());
    }

    /// <summary>
    /// Closes the segment by writing its record directory and publishing its descriptor to the
    /// owning writer. No further records may be appended afterwards.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">The segment has already been completed.</exception>
    public void Complete()
    {
        EnsureOpen();

        // The payload region ends aligned, so the directory begins exactly at the current position.
        long directoryOffset = Platform.AlignUp(_writer.Position, ArtifactFormat.Alignment);
        _writer.PadTo(directoryOffset, _hasher);

        Span<byte> entry = stackalloc byte[ArtifactFormat.RecordEntrySize];
        foreach (RecordEntry record in _entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0..], (ulong)record.RelativeOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)record.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], record.Checksum);
            _writer.WriteRaw(entry);
            _hasher.Append(entry);
        }

        SegmentDescriptor descriptor = new()
        {
            SegmentId = Id,
            Flags = ArtifactFormat.SegmentFlagLive,
            DataOffset = (ulong)_dataOffset,
            DataLength = (ulong)(_writer.Position - _dataOffset),
            RecordCount = (ulong)_entries.Count,
            DirectoryOffset = (ulong)directoryOffset,
            PayloadOffset = (ulong)_dataOffset,
            Checksum = _hasher.GetCurrentHashAsUInt64(),
        };

        _completed = true;
        _writer.OnSegmentCompleted(this, descriptor);
    }

    /// <summary>Closes the segment if it is still open, so <c>using</c> blocks publish it.</summary>
    public void Dispose()
    {
        if (!_completed && !_abandoned)
        {
            Complete();
        }
    }

    /// <summary>
    /// Marks this segment as abandoned so disposing it does not attempt to write a directory to a
    /// destination the owning writer has already released.
    /// </summary>
    internal void Abandon() => _abandoned = true;

    /// <summary>Throws if this segment can no longer accept operations.</summary>
    /// <exception cref="System.InvalidOperationException">The segment has been completed or abandoned.</exception>
    private void EnsureOpen()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The segment has already been completed.");
        }

        if (_abandoned)
        {
            throw new InvalidOperationException("The owning writer was disposed before this segment was completed.");
        }
    }

    /// <summary>
    /// Adds <paramref name="read"/> to <paramref name="length"/>, failing if the running total would
    /// exceed the maximum record length.
    /// </summary>
    /// <param name="length">The number of payload bytes accumulated so far.</param>
    /// <param name="read">The number of bytes just produced by the source.</param>
    /// <returns>The new running total.</returns>
    /// <exception cref="System.InvalidOperationException">The running total exceeds <see cref="ArtifactFormat.MaxRecordLength"/>.</exception>
    private static long CheckedAdd(long length, int read)
    {
        long total = length + read;
        if (total > ArtifactFormat.MaxRecordLength)
        {
            throw new InvalidOperationException(
                $"A single record cannot exceed {ArtifactFormat.MaxRecordLength} bytes; " +
                "split the input across multiple records.");
        }

        return total;
    }

    /// <summary>
    /// Records a completed record's directory entry and pads the destination up to the next record
    /// boundary, folding the padding into the segment checksum.
    /// </summary>
    /// <param name="relativeOffset">The record's offset relative to the segment payload region.</param>
    /// <param name="length">The record's payload length in bytes.</param>
    /// <param name="checksum">The XxHash3 checksum of the record's payload.</param>
    /// <returns>The zero-based index of the appended record within this segment.</returns>
    private int CloseRecord(long relativeOffset, long length, ulong checksum)
    {
        _entries.Add(new RecordEntry(relativeOffset, length, checksum));
        _writer.PadTo(Platform.AlignUp(_writer.Position, ArtifactFormat.Alignment), _hasher);
        return _entries.Count - 1;
    }

    /// <summary>
    /// The directory state retained for a single appended record until the segment is closed.
    /// </summary>
    /// <param name="relativeOffset">The record's offset relative to the segment payload region.</param>
    /// <param name="length">The record's payload length in bytes.</param>
    /// <param name="checksum">The XxHash3 checksum of the record's payload.</param>
    private readonly struct RecordEntry(long relativeOffset, long length, ulong checksum)
    {
        /// <summary>The record's offset relative to the start of the segment payload region.</summary>
        /// <value>The record's offset relative to the start of the segment payload region.</value>
        public long RelativeOffset { get; } = relativeOffset;

        /// <summary>The record's payload length in bytes.</summary>
        /// <value>The record's payload length in bytes.</value>
        public long Length { get; } = length;

        /// <summary>The XxHash3 checksum of the record's payload.</summary>
        /// <value>The XxHash3 checksum of the record's payload.</value>
        public ulong Checksum { get; } = checksum;
    }
}
