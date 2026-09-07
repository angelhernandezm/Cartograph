// ============================================================================
// Cartograph
// File: RecordSource.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Payload providers for records appended to a segment, covering both fully
// buffered byte arrays and memory-bounded, file-backed streaming records
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
/// Supplies the bytes of a single record to <see cref="SegmentedArtifactWriter"/>.
/// </summary>
/// <remarks>
/// A source exposes its <see cref="Length"/> without materializing its payload, which is what lets
/// the writer compute the whole artifact layout up front while still streaming the bytes themselves
/// at write time. Sources that cannot report a checksum without reading their backing store are
/// written using the payload-first segment layout, so a single pass both emits and checksums them.
/// </remarks>
internal abstract class RecordSource {
    /// <summary>The byte length of the record.</summary>
    /// <value>The byte length of the record.</value>
    public abstract long Length {
        get;
    }

    /// <summary>
    /// Whether this source can report <see cref="ComputeChecksum"/> cheaply, without re-reading a
    /// backing store. Segments containing any source that cannot are laid out payload-first.
    /// </summary>
    /// <value>
    /// Whether this source can report <see cref="ComputeChecksum"/> cheaply, without re-reading a backing
    /// store. Segments containing any source that cannot are laid out payload-first.
    /// </value>
    public abstract bool HasCheapChecksum {
        get;
    }

    /// <summary>Computes the XxHash3 checksum of the record's bytes.</summary>
    /// <returns>The XxHash3 checksum of the record payload.</returns>
    /// <exception cref="System.IO.IOException">The backing store could not be read.</exception>
    public abstract ulong ComputeChecksum();

    /// <summary>
    /// Writes the record payload to <paramref name="stream"/>, feeding every byte into
    /// <paramref name="segmentHasher"/>, and returns the record's own XxHash3 checksum.
    /// </summary>
    /// <param name="stream">The destination stream positioned at the record's payload offset.</param>
    /// <param name="segmentHasher">The hasher accumulating the enclosing segment's checksum.</param>
    /// <returns>The XxHash3 checksum of the bytes just written.</returns>
    /// <exception cref="System.IO.IOException">The backing store could not be read.</exception>
    /// <exception cref="System.IO.EndOfStreamException">The backing store ended before the declared length.</exception>
    public abstract ulong WriteTo(Stream stream, XxHash3 segmentHasher);
}

/// <summary>
/// A record whose payload is fully materialized as a byte array on the managed heap.
/// </summary>
internal sealed class BufferedRecordSource : RecordSource {
    /// <summary>The materialized record payload.</summary>
    private readonly byte[] _payload;

    /// <summary>The lazily computed XxHash3 checksum of <see cref="_payload"/>.</summary>
    private ulong? _checksum;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferedRecordSource" /> class.
    /// </summary>
    /// <param name="payload">The record bytes; taken by reference and never mutated.</param>
    public BufferedRecordSource(byte[] payload) => _payload = payload;

    /// <summary>The byte length of the buffered payload.</summary>
    /// <value>The byte length of the buffered payload.</value>
    public override long Length => _payload.Length;

    /// <summary>Always <see langword="true"/>; the payload is already in memory.</summary>
    /// <value>Always <see langword="true"/>; the payload is already in memory.</value>
    public override bool HasCheapChecksum => true;

    /// <summary>Computes (and caches) the XxHash3 checksum of the buffered payload.</summary>
    /// <returns>The XxHash3 checksum of the record payload.</returns>
    public override ulong ComputeChecksum() => _checksum ??= XxHash3.HashToUInt64(_payload);

    /// <summary>Writes the buffered payload and folds it into the segment checksum.</summary>
    /// <param name="stream">The destination stream positioned at the record's payload offset.</param>
    /// <param name="segmentHasher">The hasher accumulating the enclosing segment's checksum.</param>
    /// <returns>The XxHash3 checksum of the bytes just written.</returns>
    public override ulong WriteTo(Stream stream, XxHash3 segmentHasher) {
        stream.Write(_payload);
        segmentHasher.Append(_payload);
        return ComputeChecksum();
    }
}

/// <summary>
/// A record whose payload is streamed from a byte range of a file, so packing never buffers the
/// record on the managed heap.
/// </summary>
/// <remarks>
/// This is the mechanism that makes packing memory-bounded: an artifact containing many gigabytes of
/// file data is written using a single pooled scratch buffer, independent of the total payload size.
/// </remarks>
internal sealed class FileRecordSource : RecordSource {
    /// <summary>The size of the pooled scratch buffer used to stage file reads.</summary>
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>The absolute path of the backing file.</summary>
    private readonly string _path;

    /// <summary>The byte offset within the backing file at which the record begins.</summary>
    private readonly long _offset;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRecordSource" /> class.
    /// </summary>
    /// <param name="path">The path of the backing file.</param>
    /// <param name="offset">The byte offset within the file at which the record begins.</param>
    /// <param name="length">The number of bytes the record spans.</param>
    public FileRecordSource(string path, long offset, long length) {
        _path = path;
        _offset = offset;
        Length = length;
    }

    /// <summary>The byte length of the record's range within the backing file.</summary>
    /// <value>The byte length of the record's range within the backing file.</value>
    public override long Length {
        get;
    }

    /// <summary>Always <see langword="false"/>; checksumming requires reading the file.</summary>
    /// <value>Always <see langword="false"/>; checksumming requires reading the file.</value>
    public override bool HasCheapChecksum => false;

    /// <summary>Reads the backing range and computes its XxHash3 checksum without writing it.</summary>
    /// <returns>The XxHash3 checksum of the record payload.</returns>
    /// <exception cref="System.IO.EndOfStreamException">The file ended before the declared length.</exception>
    public override ulong ComputeChecksum() {
        XxHash3 hasher = new();
        Pump(destination: null, segmentHasher: null, recordHasher: hasher);
        return hasher.GetCurrentHashAsUInt64();
    }

    /// <summary>Streams the backing range to <paramref name="stream"/> in bounded chunks.</summary>
    /// <param name="stream">The destination stream positioned at the record's payload offset.</param>
    /// <param name="segmentHasher">The hasher accumulating the enclosing segment's checksum.</param>
    /// <returns>The XxHash3 checksum of the bytes just written.</returns>
    /// <exception cref="System.IO.EndOfStreamException">The file ended before the declared length.</exception>
    public override ulong WriteTo(Stream stream, XxHash3 segmentHasher) {
        XxHash3 recordHasher = new();
        Pump(stream, segmentHasher, recordHasher);
        return recordHasher.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// Reads the record's byte range from the backing file in bounded chunks, optionally writing it
    /// to <paramref name="destination"/> and folding it into the supplied hashers.
    /// </summary>
    /// <param name="destination">The stream to write to, or <see langword="null"/> to only hash.</param>
    /// <param name="segmentHasher">The segment hasher to feed, or <see langword="null"/> to skip.</param>
    /// <param name="recordHasher">The per-record hasher to feed.</param>
    /// <exception cref="System.IO.EndOfStreamException">The file ended before the declared length.</exception>
    private void Pump(Stream? destination, XxHash3? segmentHasher, XxHash3 recordHasher) {
        using FileStream file = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 0,
            FileOptions.SequentialScan);

        byte[] scratch = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try {
            long remaining = Length;
            long position = _offset;
            while (remaining > 0) {
                int want = (int)Math.Min(remaining, scratch.Length);
                int read = RandomAccess.Read(file.SafeFileHandle, scratch.AsSpan(0, want), position);
                if (read <= 0) {
                    throw new EndOfStreamException(
                        $"File '{_path}' ended {remaining} byte(s) before the declared record length.");
                }

                ReadOnlySpan<byte> chunk = scratch.AsSpan(0, read);
                destination?.Write(chunk);
                segmentHasher?.Append(chunk);
                recordHasher.Append(chunk);

                position += read;
                remaining -= read;
            }
        } finally {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
