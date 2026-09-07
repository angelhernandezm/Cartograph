// ============================================================================
// Cartograph
// File: ViewLease.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A ref-counted lease that guards live access to a MappedSegment, preventing the
// underlying mapped view from being unmapped while spans into it are still live.
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

namespace Cartograph;

/// <summary>
/// A ref-counted lease that guards live access to a <see cref="MappedSegment"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most important safety mechanism in the substrate. Unmapping a view while
/// spans into it are still live is a <b>segmentation fault, not a managed exception</b>. A lease
/// increments the owning segment's reference count on creation and decrements it on
/// <see cref="Dispose"/>; the underlying view is only unmapped once the count reaches zero.
/// </para>
/// <para>
/// Misuse is made hard: leases are <see cref="IDisposable"/>, double-dispose is safe (idempotent),
/// and any access after disposal throws <see cref="ObjectDisposedException"/> instead of reading
/// freed memory.
/// </para>
/// </remarks>
public sealed class ViewLease : IDisposable {
    /// <summary>The segment this lease holds a reference on, cleared on disposal.</summary>
    private MappedSegment? _segment;

    /// <summary>The leased read-only region of the segment.</summary>
    private readonly ReadOnlyMemory<byte> _memory;

    /// <summary>Non-zero once <see cref="Dispose"/> has run, used to make disposal idempotent.</summary>
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ViewLease" /> class.
    /// </summary>
    /// <param name="segment">The owning segment whose reference count is incremented by this lease.</param>
    /// <param name="memory">The leased read-only memory region.</param>
    internal ViewLease(MappedSegment segment, ReadOnlyMemory<byte> memory) {
        _segment = segment;
        _memory = memory;
    }

    /// <summary>The leased region as read-only memory. Valid only until the lease is disposed.</summary>
    /// <value>The leased region as read-only memory. Valid only until the lease is disposed.</value>
    /// <exception cref="System.ObjectDisposedException">The <see cref="ViewLease"/> has been disposed.</exception>
    public ReadOnlyMemory<byte> Memory {
        get {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _memory;
        }
    }

    /// <summary>The leased region as a read-only span. Valid only until the lease is disposed.</summary>
    /// <value>The leased region as a read-only span. Valid only until the lease is disposed.</value>
    /// <exception cref="System.ObjectDisposedException">The <see cref="ViewLease"/> has been disposed.</exception>
    public ReadOnlySpan<byte> Span => Memory.Span;

    /// <summary>Releases this lease, decrementing the segment reference count. Safe to call more than once.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) {
            MappedSegment? segment = _segment;
            _segment = null;
            segment?.ReleaseLease();
        }
    }
}
