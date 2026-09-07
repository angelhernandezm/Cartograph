// ============================================================================
// Cartograph
// File: IChunkSource.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Defines the IChunkSource interface and ChunkLease type — the pluggable abstraction
// for reading byte chunks from a file, with both sync and async paths.
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

namespace Cartograph;

/// <summary>
/// A disposable handle to a chunk of bytes read from an <see cref="IChunkSource"/>.
/// </summary>
/// <remarks>
/// The chunk is exposed as a zero-copy <see cref="ReadOnlySequence{Byte}"/>. For mapped sources the
/// backing memory is a slice of one or more mapped windows kept alive by leases; for pooled sources
/// it is a rented buffer. In both cases the sequence is only valid until the lease is disposed, and
/// disposal releases the underlying resource (leases or the pooled buffer).
/// </remarks>
public sealed class ChunkLease : IDisposable {
    /// <summary>
    /// The object owning the memory behind the sequence, exchanged for <c>null</c> when it is disposed.
    /// </summary>
    private IDisposable? _owner;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkLease" /> class.
    /// </summary>
    /// <remarks>
    /// This constructor is public so that custom <see cref="IChunkSource"/> implementations living
    /// outside this assembly can return chunks. Pass the object that owns the memory backing
    /// <paramref name="sequence"/> as <paramref name="owner"/>; it is disposed exactly once, when
    /// the lease is disposed. Pass <see langword="null"/> when the memory needs no release (for
    /// example a sequence over a plain managed array that the source does not pool).
    /// </remarks>
    /// <param name="sequence">The byte sequence exposed by this lease.</param>
    /// <param name="owner">
    /// The disposable owner that backs the sequence; released on <see cref="Dispose"/>. May be
    /// <see langword="null"/> when the backing memory requires no cleanup.
    /// </param>
    public ChunkLease(ReadOnlySequence<byte> sequence, IDisposable? owner) {
        Sequence = sequence;
        _owner = owner;
    }

    /// <summary>The chunk bytes. Valid only until this lease is disposed.</summary>
    /// <value>The chunk bytes. Valid only until this lease is disposed.</value>
    public ReadOnlySequence<byte> Sequence {
        get;
    }

    /// <summary>Releases the backing resource. Safe to call more than once.</summary>
    public void Dispose() {
        IDisposable? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Dispose();
    }
}

/// <summary>
/// A pluggable source of file chunks. Two implementations ship with the substrate so the two access
/// strategies can be benchmarked head-to-head: <see cref="MappedChunkSource"/> (mmap, which wins on
/// random reads over a hot page cache) and <see cref="RandomAccessChunkSource"/> (pooled
/// <see cref="System.IO.RandomAccess"/>, which is async-friendly and often wins on large sequential
/// scans). Making this pluggable from day one is a deliberate design decision.
/// </summary>
/// <remarks>
/// <para>
/// This interface is a supported extension point: implement it to back an artifact with any store
/// you like — HTTP range requests, S3 or Azure Blob, an encrypted or compressed container, or an
/// in-memory buffer for tests — and pass the instance to
/// <c>Artifact.Open(IChunkSource, ArtifactOpenOptions, bool)</c>. The substrate ships no transport
/// code and takes no dependency on any client library.
/// </para>
/// <para><b>Contract for implementers.</b> An implementation MUST honour all of the following.</para>
/// <list type="bullet">
/// <item><description>
/// <b>Absolute offsets.</b> <c>offset</c> arguments are absolute byte offsets from the
/// start of the artifact, not relative to any window, segment, or previous read.
/// </description></item>
/// <item><description>
/// <b>Exact-length reads.</b> <c>Read</c> and <c>ReadAsync</c> MUST return a chunk whose
/// <see cref="ChunkLease.Sequence"/> is exactly <c>length</c> bytes, or throw. Short reads are never
/// acceptable; loop internally until the request is satisfied. Callers treat a short chunk as a
/// corrupt artifact.
/// </description></item>
/// <item><description>
/// <b>Range validation.</b> A request outside <c>[0, Length)</c> MUST throw
/// <see cref="System.ArgumentOutOfRangeException"/> rather than returning fewer bytes or reading
/// out of bounds.
/// </description></item>
/// <item><description>
/// <b>Stable length.</b> <see cref="Length"/> MUST NOT change over the lifetime of the source.
/// Cartograph artifacts are immutable once written, and every bounds check performed when the
/// artifact was opened is validated against the value observed at that time.
/// </description></item>
/// <item><description>
/// <b>Thread safety.</b> <c>Read</c> and <c>ReadAsync</c> MUST be safe to call concurrently from
/// multiple threads on the same instance, and MUST remain safe while previously returned leases are
/// still alive. <see cref="IDisposable.Dispose"/> is not required to be safe against concurrent
/// reads; callers must stop reading before disposing.
/// </description></item>
/// <item><description>
/// <b>Lease lifetime.</b> The bytes exposed by a returned <see cref="ChunkLease"/> MUST stay valid
/// and unchanged until that lease is disposed. Leases may be disposed in any order, may outlive
/// other leases, and may be disposed more than once.
/// </description></item>
/// <item><description>
/// <b>Disposal.</b> Disposing the source releases its own resources. Whether the source is disposed
/// along with the artifact that uses it is decided by the caller through the <c>ownsSource</c>
/// parameter of <c>Artifact.Open</c>.
/// </description></item>
/// </list>
/// <para>
/// Implementations do <b>not</b> need to verify record checksums or validate artifact structure;
/// the format layer bounds-checks every offset it reads and verifies per-record checksums, so a
/// buggy or hostile source produces a clean <c>CartographFormatException</c> rather than silent
/// corruption.
/// </para>
/// </remarks>
public interface IChunkSource : IDisposable {
    /// <summary>The total length of the underlying file in bytes. Must not change over the source's lifetime.</summary>
    /// <value>The total length of the underlying file in bytes. Must not change over the source's lifetime.</value>
    long Length {
        get;
    }

    /// <summary>Reads <c>[offset, offset + length)</c> and returns it as a disposable chunk.</summary>
    /// <param name="offset">The absolute byte offset within the file to start reading from.</param>
    /// <param name="length">The number of bytes to read. The returned chunk must contain exactly this many bytes.</param>
    /// <returns>A <see cref="ChunkLease"/> containing exactly <paramref name="length"/> bytes; the caller must dispose it.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">The requested range falls outside <c>[0, Length)</c>.</exception>
    ChunkLease Read(long offset, int length);

    /// <summary>Asynchronously reads <c>[offset, offset + length)</c> and returns it as a disposable chunk.</summary>
    /// <param name="offset">The absolute byte offset within the file to start reading from.</param>
    /// <param name="length">The number of bytes to read. The returned chunk must contain exactly this many bytes.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to a <see cref="ChunkLease"/> containing exactly <paramref name="length"/> bytes.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">The requested range falls outside <c>[0, Length)</c>.</exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken cancellationToken = default);
}
