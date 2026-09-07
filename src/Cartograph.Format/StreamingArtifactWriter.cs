// ============================================================================
// Cartograph
// File: StreamingArtifactWriter.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Builds a Cartograph artifact incrementally, emitting each record's bytes to
// the destination as it is appended instead of retaining payloads until save
// time, so ingestion memory is bounded by a single pooled buffer rather than by
// the size of the artifact being produced.
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
/// Builds a Cartograph artifact incrementally, writing each record's payload to the destination at
/// the moment it is appended. Unlike <see cref="SegmentedArtifactWriter"/>, which retains in-memory
/// records until <see cref="SegmentedArtifactWriter.Save(string)"/> is called, this writer never
/// holds a payload after it has been emitted.
/// </summary>
/// <remarks>
/// <para>
/// This writer always emits segments in payload-first layout: record payloads are written as they
/// arrive and the record directory follows once the segment is closed. That is what allows a record
/// of unknown length to be appended, because its length and checksum are both known by the time the
/// directory is written. The manifest is written last and the header is patched on completion,
/// producing an artifact byte-for-byte compatible with <see cref="SegmentedArtifactWriter"/> output
/// and readable by <see cref="Artifact"/> without any format change.
/// </para>
/// <para>
/// Steady-state memory is one pooled staging buffer plus 24 bytes of directory state per record in
/// the open segment. Ingesting an artifact of arbitrary size therefore costs a bounded, constant
/// amount of managed memory, and no payload-sized array is ever allocated.
/// </para>
/// <para>
/// The destination must be seekable, because the header is rewritten in place by
/// <see cref="Complete"/>. If the writer is disposed without calling <see cref="Complete"/>, the
/// destination is left with a zeroed placeholder header and is deliberately not a valid artifact.
/// </para>
/// </remarks>
public sealed class StreamingArtifactWriter : IDisposable
{
    /// <summary>The destination the artifact is written to.</summary>
    private readonly Stream _stream;

    /// <summary>Whether this writer opened <see cref="_stream"/> and must therefore dispose it.</summary>
    private readonly bool _ownsStream;

    /// <summary>The segment descriptors closed so far, in the order their segments were written.</summary>
    private readonly List<SegmentDescriptor> _descriptors = [];

    /// <summary>A reusable zero-fill buffer used to emit alignment padding.</summary>
    private readonly byte[] _padScratch = new byte[ArtifactFormat.Alignment];

    /// <summary>The number of bytes written to the destination so far.</summary>
    private long _position;

    /// <summary>The segment currently accepting records, or <see langword="null"/> if none is open.</summary>
    private StreamingSegment? _openSegment;

    /// <summary>Whether <see cref="Complete"/> has already run.</summary>
    private bool _completed;

    /// <summary>Whether this writer has been disposed.</summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingArtifactWriter" /> class writing to
    /// <paramref name="path"/>, overwriting any existing file.
    /// </summary>
    /// <param name="path">The file system path to write the artifact to.</param>
    /// <exception cref="System.ArgumentException"><paramref name="path"/> is <c>null</c> or empty.</exception>
    /// <exception cref="System.IO.IOException">The file could not be created.</exception>
    public StreamingArtifactWriter(string path)
        : this(CreateFile(path), ownsStream: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingArtifactWriter" /> class writing to
    /// <paramref name="stream"/>, which is not disposed by this writer.
    /// </summary>
    /// <param name="stream">The destination stream; must be writable and seekable.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="stream"/> is not writable or not seekable.</exception>
    public StreamingArtifactWriter(Stream stream)
        : this(stream, ownsStream: false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingArtifactWriter" /> class and reserves
    /// space for the header, which is patched in place by <see cref="Complete"/>.
    /// </summary>
    /// <param name="stream">The destination stream; must be writable and seekable.</param>
    /// <param name="ownsStream">Whether disposing this writer should also dispose <paramref name="stream"/>.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="stream"/> is not writable or not seekable.</exception>
    private StreamingArtifactWriter(Stream stream, bool ownsStream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(stream));
        }

        if (!stream.CanSeek)
        {
            throw new ArgumentException(
                "The destination stream must be seekable so the header can be patched on completion.",
                nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;

        // Reserve the header. Its manifest offset and content length are unknown until the last
        // record has been appended, so real values are written back by Complete().
        Span<byte> placeholder = stackalloc byte[ArtifactFormat.HeaderSize];
        placeholder.Clear();
        _stream.Write(placeholder);
        _position = ArtifactFormat.HeaderSize;
    }

    /// <summary>The number of segments closed so far.</summary>
    /// <value>The number of segments closed so far.</value>
    public int SegmentCount => _descriptors.Count;

    /// <summary>The number of bytes written to the destination so far.</summary>
    /// <value>The number of bytes written to the destination so far.</value>
    public long BytesWritten => _position;

    /// <summary>
    /// Opens a new segment and returns the handle used to append records to it. The segment must be
    /// closed, by disposing it or calling <see cref="StreamingSegment.Complete"/>, before another
    /// segment is opened.
    /// </summary>
    /// <param name="segmentId">An optional stable id; defaults to the segment's ordinal.</param>
    /// <returns>A <see cref="StreamingSegment"/> that accepts records for the new segment.</returns>
    /// <exception cref="System.ObjectDisposedException">The writer has been disposed.</exception>
    /// <exception cref="System.InvalidOperationException">The artifact has already been completed.</exception>
    /// <exception cref="System.InvalidOperationException">Another segment is still open.</exception>
    public StreamingSegment BeginSegment(uint? segmentId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed)
        {
            throw new InvalidOperationException("The artifact has already been completed.");
        }

        if (_openSegment is not null)
        {
            throw new InvalidOperationException(
                "A segment is already open; complete it before beginning another.");
        }

        // Padding ahead of a segment sits outside every segment's data region, so it is deliberately
        // excluded from the segment checksum.
        long dataOffset = Platform.AlignUp(_position, ArtifactFormat.Alignment);
        PadTo(dataOffset, hasher: null);

        _openSegment = new StreamingSegment(this, segmentId ?? (uint)_descriptors.Count, dataOffset);
        return _openSegment;
    }

    /// <summary>
    /// Finalizes the artifact by writing the manifest and patching the reserved header. No further
    /// segments or records may be appended afterwards.
    /// </summary>
    /// <exception cref="System.ObjectDisposedException">The writer has been disposed.</exception>
    /// <exception cref="System.InvalidOperationException">The artifact has already been completed.</exception>
    /// <exception cref="System.InvalidOperationException">A segment is still open.</exception>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed)
        {
            throw new InvalidOperationException("The artifact has already been completed.");
        }

        if (_openSegment is not null)
        {
            throw new InvalidOperationException(
                "A segment is still open; complete it before completing the artifact.");
        }

        long manifestOffset = Platform.AlignUp(_position, ArtifactFormat.Alignment);
        PadTo(manifestOffset, hasher: null);

        SegmentManifest manifest = new([.. _descriptors]);
        byte[] manifestBytes = new byte[manifest.ByteLength];
        manifest.Write(manifestBytes);
        _stream.Write(manifestBytes);
        _position += manifestBytes.Length;

        ArtifactHeader header = new()
        {
            VersionMajor = ArtifactFormat.VersionMajor,
            VersionMinor = ArtifactFormat.VersionMinor,
            PointerSize = (byte)IntPtr.Size,
            ManifestOffset = (ulong)manifestOffset,
            ManifestLength = (ulong)manifestBytes.Length,
            ContentLength = (ulong)_position,
        };

        Span<byte> headerBytes = stackalloc byte[ArtifactFormat.HeaderSize];
        header.Write(headerBytes);

        long end = _stream.Position;
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(headerBytes);
        _stream.Seek(end, SeekOrigin.Begin);
        _stream.Flush();

        _completed = true;
    }

    /// <summary>Releases the destination stream if this writer opened it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _openSegment?.Abandon();
        _openSegment = null;

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    /// <summary>Opens <paramref name="path"/> for writing, truncating any existing file.</summary>
    /// <param name="path">The file system path to write the artifact to.</param>
    /// <returns>A writable, seekable <see cref="FileStream"/> positioned at the start of the file.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="path"/> is <c>null</c> or empty.</exception>
    /// <exception cref="System.IO.IOException">The file could not be created.</exception>
    private static FileStream CreateFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>Writes <paramref name="data"/> to the destination and advances the write position.</summary>
    /// <param name="data">The bytes to write.</param>
    internal void WriteRaw(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        _position += data.Length;
    }

    /// <summary>Asynchronously writes <paramref name="data"/> and advances the write position.</summary>
    /// <param name="data">The bytes to write.</param>
    /// <param name="cancellationToken">A token used to cancel the write.</param>
    /// <returns>A task that completes once the bytes have been written.</returns>
    internal async ValueTask WriteRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        _position += data.Length;
    }

    /// <summary>The destination stream, exposed so an open segment can stage bytes through it.</summary>
    /// <value>The destination stream, exposed so an open segment can stage bytes through it.</value>
    internal Stream Destination => _stream;

    /// <summary>The current write position within the destination.</summary>
    /// <value>The current write position within the destination.</value>
    internal long Position => _position;

    /// <summary>Advances the recorded write position by <paramref name="count"/> bytes.</summary>
    /// <param name="count">The number of bytes written directly to <see cref="Destination"/>.</param>
    internal void Advance(long count) => _position += count;

    /// <summary>
    /// Writes zero-fill padding from the current position up to <paramref name="target"/>, optionally
    /// folding the padding into <paramref name="hasher"/>.
    /// </summary>
    /// <param name="target">The target byte position; must be &gt;= the current position.</param>
    /// <param name="hasher">The optional segment hasher to feed padding into; may be <see langword="null"/>.</param>
    /// <exception cref="System.InvalidOperationException">Layout error: attempted to pad backwards.</exception>
    internal void PadTo(long target, XxHash3? hasher)
    {
        if (target < _position)
        {
            throw new InvalidOperationException("Layout error: attempted to pad backwards.");
        }

        long remaining = target - _position;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, _padScratch.Length);
            Array.Clear(_padScratch, 0, chunk);
            _stream.Write(_padScratch, 0, chunk);
            hasher?.Append(_padScratch.AsSpan(0, chunk));
            _position += chunk;
            remaining -= chunk;
        }
    }

    /// <summary>Records a closed segment's descriptor and clears the open-segment slot.</summary>
    /// <param name="segment">The segment that has just been closed.</param>
    /// <param name="descriptor">The descriptor describing the closed segment.</param>
    /// <exception cref="System.InvalidOperationException"><paramref name="segment"/> is not the currently open segment.</exception>
    internal void OnSegmentCompleted(StreamingSegment segment, in SegmentDescriptor descriptor)
    {
        if (!ReferenceEquals(_openSegment, segment))
        {
            throw new InvalidOperationException("The completed segment is not the open segment.");
        }

        _descriptors.Add(descriptor);
        _openSegment = null;
    }
}
