// ============================================================================
// Cartograph
// File: MappedSequence.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Static factory that stitches multiple mapped memory chunks into a single
// zero-copy ReadOnlySequence<byte>, enabling logical access to files larger than 2 GiB.
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
/// Stitches multiple mapped memory chunks into a single <see cref="ReadOnlySequence{Byte}"/>.
/// </summary>
/// <remarks>
/// This arbitrary-length, segmented, zero-copy view over many mapped windows is the main primitive
/// that does not already exist in the .NET ecosystem: it lets a &gt; 2 GiB file be consumed as one
/// logical byte sequence without ever copying payload into the managed heap.
/// </remarks>
public static class MappedSequence {
    /// <summary>Builds a <see cref="ReadOnlySequence{Byte}"/> over the supplied chunks in order.</summary>
    /// <param name="chunks">The mapped chunks, each typically a slice of one mapped window.</param>
    /// <returns>A <see cref="ReadOnlySequence{Byte}"/> spanning all supplied chunks in order.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="chunks" /> is <c>null</c>.</exception>
    public static ReadOnlySequence<byte> Create(IReadOnlyList<ReadOnlyMemory<byte>> chunks) {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0) {
            return ReadOnlySequence<byte>.Empty;
        }

        if (chunks.Count == 1) {
            return new ReadOnlySequence<byte>(chunks[0]);
        }

        MappedSequenceSegment first = new(chunks[0]);
        MappedSequenceSegment current = first;
        for (int i = 1; i < chunks.Count; i++) {
            current = current.Append(chunks[i]);
        }

        return new ReadOnlySequence<byte>(first, 0, current, current.Memory.Length);
    }
}
