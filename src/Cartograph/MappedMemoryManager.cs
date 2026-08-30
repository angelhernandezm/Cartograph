// ============================================================================
// Cartograph
// File: MappedMemoryManager.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A MemoryManager<byte> that exposes a region of a memory-mapped view as
// Memory<byte>/Span<byte> with no managed copy, handling PointerOffset alignment.
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
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace Cartograph;

/// <summary>
/// A <see cref="MemoryManager{T}"/> that exposes a region of a memory-mapped view as
/// <see cref="Memory{Byte}"/> / <see cref="Span{Byte}"/> with no managed copy.
/// </summary>
/// <remarks>
/// <para>
/// The manager wraps a <see cref="SafeMemoryMappedViewHandle"/> and pins the underlying
/// pointer for its lifetime via <c>SafeMemoryMappedViewHandle.AcquirePointer</c> /
/// <c>SafeMemoryMappedViewHandle.ReleasePointer</c>.
/// </para>
/// <para>
/// IMPORTANT: the pointer returned by <c>AcquirePointer</c> is the base of the mapped region,
/// which the runtime aligns down to the OS allocation granularity. The caller-requested offset
/// is recovered by adding <see cref="MemoryMappedViewAccessor.PointerOffset"/> to that base. This
/// class does exactly that; forgetting the <c>PointerOffset</c> addition is a classic and silent
/// data-corruption bug when the requested offset is not granularity-aligned.
/// </para>
/// <para>
/// Although <see cref="MemoryManager{T}.Memory"/> is typed as writable <see cref="Memory{Byte}"/>,
/// read-only mappings MUST be consumed as <see cref="ReadOnlyMemory{Byte}"/> /
/// <see cref="ReadOnlySpan{Byte}"/>. Writing through a read-only mapping is undefined behaviour and
/// will typically raise an access violation.
/// </para>
/// </remarks>
public sealed unsafe class MappedMemoryManager : MemoryManager<byte>
{
    private readonly SafeMemoryMappedViewHandle _handle;
    private readonly int _length;
    private byte* _pointer;
    private bool _acquired;
    private bool _disposed;

    /// <summary>
    /// Creates a manager over <paramref name="length"/> bytes of the view identified by
    /// <paramref name="handle"/>, starting <paramref name="pointerOffset"/> bytes into the acquired
    /// base pointer.
    /// </summary>
    /// <param name="handle">The mapped view handle to pin.</param>
    /// <param name="pointerOffset">
    /// The value of <see cref="MemoryMappedViewAccessor.PointerOffset"/> for the view. This is added
    /// to the acquired base pointer to reach the requested region.
    /// </param>
    /// <param name="length">The number of bytes exposed by this manager (at most <see cref="int.MaxValue"/>).</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="handle" /> is <c>null</c>.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="length" /> or <paramref name="pointerOffset" /> is negative.</exception>
    /// <exception cref="System.InvalidOperationException">Failed to acquire a pointer to the mapped view.</exception>
    public MappedMemoryManager(SafeMemoryMappedViewHandle handle, long pointerOffset, int length)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(pointerOffset);

        _handle = handle;
        _length = length;

        byte* basePointer = null;
        handle.AcquirePointer(ref basePointer);
        if (basePointer is null)
        {
            throw new InvalidOperationException("Failed to acquire a pointer to the mapped view.");
        }

        _acquired = true;
        // Recover the caller-requested offset: base is aligned down to allocation granularity.
        _pointer = basePointer + pointerOffset;
    }

    /// <summary>The number of bytes exposed by this manager.</summary>
    public int Length => _length;

    /// <inheritdoc />
    /// <exception cref="System.ObjectDisposedException">The manager has been disposed.</exception>
    /// <returns>A <see cref="Span{T}"/> over the mapped region.</returns>
    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<byte>(_pointer, _length);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Mapped memory is already address-stable, so no <see cref="System.Runtime.InteropServices.GCHandle"/>
    /// is created. The returned <see cref="MemoryHandle"/> simply points at <c>base + index</c> and
    /// <see cref="Unpin"/> is a no-op.
    /// </remarks>
    /// <param name="elementIndex">The zero-based element index to pin at.</param>
    /// <returns>A <see cref="MemoryHandle"/> pointing at the specified element in the mapped region.</returns>
    /// <exception cref="System.ObjectDisposedException">The manager has been disposed.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="elementIndex"/> is out of range.</exception>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        return new MemoryHandle(_pointer + elementIndex);
    }

    /// <inheritdoc />
    public override void Unpin()
    {
        // No-op: mapped memory does not move and no GC handle was taken.
    }

    /// <inheritdoc />
    /// <param name="disposing"><see langword="true"/> if called from <see cref="IDisposable.Dispose"/>; <see langword="false"/> if called from a finalizer.</param>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_acquired)
        {
            _handle.ReleasePointer();
            _acquired = false;
        }

        _pointer = null;
    }
}
