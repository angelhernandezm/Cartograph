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
public sealed class ChunkLease : IDisposable
{
    private IDisposable? _owner;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkLease" /> class.
    /// </summary>
    /// <param name="sequence">The byte sequence exposed by this lease.</param>
    /// <param name="owner">The disposable owner that backs the sequence; released on <see cref="Dispose"/>.</param>
    internal ChunkLease(ReadOnlySequence<byte> sequence, IDisposable? owner)
    {
        Sequence = sequence;
        _owner = owner;
    }

    /// <summary>The chunk bytes. Valid only until this lease is disposed.</summary>
    public ReadOnlySequence<byte> Sequence { get; }

    /// <summary>Releases the backing resource. Safe to call more than once.</summary>
    public void Dispose()
    {
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
public interface IChunkSource : IDisposable
{
    /// <summary>The total length of the underlying file in bytes.</summary>
    long Length { get; }

    /// <summary>Reads <c>[offset, offset + length)</c> and returns it as a disposable chunk.</summary>
    /// <param name="offset">The byte offset within the file to start reading from.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <returns>A <see cref="ChunkLease"/> containing the requested bytes.</returns>
    ChunkLease Read(long offset, int length);

    /// <summary>Asynchronously reads <c>[offset, offset + length)</c> and returns it as a disposable chunk.</summary>
    /// <param name="offset">The byte offset within the file to start reading from.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to a <see cref="ChunkLease"/> containing the requested bytes.</returns>
    ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken cancellationToken = default);
}
