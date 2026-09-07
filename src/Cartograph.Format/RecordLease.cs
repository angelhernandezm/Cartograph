// ============================================================================
// Cartograph
// File: RecordLease.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A disposable handle to a single artifact record, wrapping a ChunkLease and
// exposing the record bytes as a zero-copy ReadOnlySequence<byte>.
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

namespace Cartograph.Format;

/// <summary>
/// A disposable handle to a single record, exposed as a zero-copy <see cref="ReadOnlySequence{Byte}"/>.
/// </summary>
/// <remarks>
/// Under a mapped chunk source the sequence is a view over mapped pages and may span more than one
/// segment when the record crosses a window boundary; under a pooled source it is a single rented
/// buffer. Either way <see cref="Sequence"/> is valid only until the lease is disposed.
/// </remarks>
public sealed class RecordLease : IDisposable {
    /// <summary>The underlying chunk lease that owns the raw memory backing this record.</summary>
    private readonly ChunkLease _chunk;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordLease" /> class.
    /// </summary>
    /// <param name="chunk">The chunk lease that holds the record's raw memory.</param>
    internal RecordLease(ChunkLease chunk) {
        _chunk = chunk;
    }

    /// <summary>The record bytes. Valid only until this lease is disposed.</summary>
    /// <value>The record bytes. Valid only until this lease is disposed.</value>
    public ReadOnlySequence<byte> Sequence => _chunk.Sequence;

    /// <summary>The record length in bytes.</summary>
    /// <value>The record length in bytes.</value>
    public long Length => _chunk.Sequence.Length;

    /// <summary>Whether the record occupies a single contiguous span (safe for <c>MemoryMarshal.Cast</c>).</summary>
    /// <value>Whether the record occupies a single contiguous span (safe for <c>MemoryMarshal.Cast</c>).</value>
    public bool IsSingleSegment => _chunk.Sequence.IsSingleSegment;

    /// <summary>
    /// The first (and, when <see cref="IsSingleSegment"/>, only) contiguous span of the record. Use
    /// this for aligned <c>MemoryMarshal.Cast&lt;byte, float&gt;</c> reads over mapped pages.
    /// </summary>
    /// <value>
    /// The first (and, when <see cref="IsSingleSegment"/>, only) contiguous span of the record. Use this
    /// for aligned <c>MemoryMarshal.Cast&lt;byte, float&gt;</c> reads over mapped pages.
    /// </value>
    public ReadOnlySpan<byte> FirstSpan => _chunk.Sequence.FirstSpan;

    /// <summary>Copies the record into a newly allocated array.</summary>
    /// <returns>A new byte array containing a copy of all the record's bytes.</returns>
    public byte[] ToArray() => _chunk.Sequence.ToArray();

    /// <summary>Releases the underlying lease or pooled buffer. Safe to call more than once.</summary>
    public void Dispose() => _chunk.Dispose();
}
