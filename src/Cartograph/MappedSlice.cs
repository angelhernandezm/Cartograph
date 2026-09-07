// ============================================================================
// Cartograph
// File: MappedSlice.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A disposable zero-copy view over a sub-range of a MappedFile, holding ViewLeases
// on every mapped window it spans to keep the backing memory resident.
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
/// A disposable view over a sub-range of a <see cref="MappedFile"/>, presented as a zero-copy
/// <see cref="ReadOnlySequence{Byte}"/>.
/// </summary>
/// <remarks>
/// The slice holds a <see cref="ViewLease"/> on every mapped window it touches, so the backing
/// memory stays mapped for as long as the slice is alive. <see cref="Sequence"/> must only be
/// consumed before the slice is disposed; reading it afterwards is undefined behaviour.
/// </remarks>
public sealed class MappedSlice : IDisposable
{
    /// <summary>
    /// The leases keeping the sliced windows mapped, exchanged for <c>null</c> when the slice is disposed.
    /// </summary>
    private ViewLease[]? _leases;

    /// <summary>
    /// Initializes a new instance of the <see cref="MappedSlice" /> class.
    /// </summary>
    /// <param name="sequence">The zero-copy byte sequence spanning the sliced region.</param>
    /// <param name="leases">The view leases that keep the backing windows mapped for the slice lifetime.</param>
    internal MappedSlice(ReadOnlySequence<byte> sequence, ViewLease[] leases)
    {
        Sequence = sequence;
        _leases = leases;
    }

    /// <summary>The sliced bytes. Valid only until the slice is disposed.</summary>
    /// <value>The sliced bytes. Valid only until the slice is disposed.</value>
    public ReadOnlySequence<byte> Sequence { get; }

    /// <summary>Releases the leases held by this slice. Safe to call more than once.</summary>
    public void Dispose()
    {
        ViewLease[]? leases = Interlocked.Exchange(ref _leases, null);
        if (leases is null)
        {
            return;
        }

        foreach (ViewLease lease in leases)
        {
            lease.Dispose();
        }
    }
}
