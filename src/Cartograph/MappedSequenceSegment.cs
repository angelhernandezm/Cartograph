// ============================================================================
// Cartograph
// File: MappedSequenceSegment.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A ReadOnlySequenceSegment<byte> node backed by a mapped memory region, used to chain
// multiple mapped views into a single logical ReadOnlySequence<byte>.
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
/// A <see cref="ReadOnlySequenceSegment{Byte}"/> node backed by a mapped memory region.
/// </summary>
/// <remarks>
/// These nodes are the building blocks of an arbitrary-length <see cref="ReadOnlySequence{Byte}"/>
/// that spans multiple mapped views. Because <see cref="Span{T}"/> and <see cref="Memory{T}"/>
/// lengths are <see cref="int"/>, any file larger than ~2 GiB REQUIRES multiple views; chaining
/// these nodes is how the substrate presents such a file as one logical, seekable byte sequence.
/// </remarks>
public sealed class MappedSequenceSegment : ReadOnlySequenceSegment<byte>
{
    /// <summary>Creates the first node of a sequence over <paramref name="memory"/>.</summary>
    /// <param name="memory">The mapped memory region this node represents.</param>
    public MappedSequenceSegment(ReadOnlyMemory<byte> memory)
    {
        Memory = memory;
        RunningIndex = 0;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MappedSequenceSegment" /> class as an interior node.
    /// </summary>
    /// <param name="memory">The mapped memory region this node represents.</param>
    /// <param name="runningIndex">The cumulative byte offset of this node within the sequence.</param>
    private MappedSequenceSegment(ReadOnlyMemory<byte> memory, long runningIndex)
    {
        Memory = memory;
        RunningIndex = runningIndex;
    }

    /// <summary>Appends <paramref name="memory"/> as the next node and returns it.</summary>
    /// <param name="memory">The mapped memory region for the new node.</param>
    /// <returns>The newly created <see cref="MappedSequenceSegment"/> appended after this node.</returns>
    public MappedSequenceSegment Append(ReadOnlyMemory<byte> memory)
    {
        MappedSequenceSegment next = new(memory, RunningIndex + Memory.Length);
        Next = next;
        return next;
    }
}
