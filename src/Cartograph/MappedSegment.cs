// ============================================================================
// Cartograph
// File: MappedSegment.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A single ref-counted mapped window over a contiguous region of a file, providing
// lifetime-safe ReadOnlyMemory<byte> access through ViewLease reference counting.
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

using System.IO.MemoryMappedFiles;

namespace Cartograph;

/// <summary>
/// A single mapped window over a contiguous region of a file, exposed as lifetime-safe
/// <see cref="ReadOnlyMemory{Byte}"/> through ref-counted <see cref="ViewLease"/>s.
/// </summary>
/// <remarks>
/// <para>
/// A segment covers at most <see cref="int.MaxValue"/> bytes because <see cref="Span{T}"/> and
/// <see cref="Memory{T}"/> lengths are <see cref="int"/>. Larger regions of a file are covered by
/// tiling multiple segments and stitching them with <see cref="MappedSequence"/>.
/// </para>
/// <para>
/// View offsets must respect the OS <b>allocation granularity</b> (64 KiB on Windows, which differs
/// from the 4 KiB page size). Callers building windows should place them on
/// <see cref="Platform.AllocationGranularity"/> boundaries; the runtime aligns the requested offset
/// down internally and reports the residual via <see cref="MemoryMappedViewAccessor.PointerOffset"/>,
/// which <see cref="MappedMemoryManager"/> re-applies.
/// </para>
/// <para>
/// The segment starts with an owner reference (count = 1). Each <see cref="Lease"/> adds a reference;
/// <see cref="Dispose"/> releases the owner reference. The view is unmapped only when the count
/// reaches zero, so readers holding leases can safely finish even after the owner disposes the
/// segment (for example when a manifest swap retires the underlying region).
/// </para>
/// </remarks>
public sealed class MappedSegment : IDisposable {
    /// <summary>The accessor that owns the mapped view backing this segment.</summary>
    private readonly MemoryMappedViewAccessor _accessor;

    /// <summary>The manager that pins the view and projects it as memory.</summary>
    private readonly MappedMemoryManager _manager;

    /// <summary>The read-only projection of the mapped region.</summary>
    private readonly ReadOnlyMemory<byte> _memory;

    /// <summary>
    /// The number of outstanding references: one for the owner plus one per live lease. The view is
    /// unmapped when this reaches zero.
    /// </summary>
    private int _refCount = 1;

    /// <summary>Non-zero once the owner's reference has been released, used to make disposal idempotent.</summary>
    private int _ownerReleased;

    /// <summary>Creates a segment over <paramref name="length"/> bytes starting at <paramref name="fileOffset"/>.</summary>
    /// <param name="mappedFile">The mapped file to create a view over.</param>
    /// <param name="fileOffset">The absolute file offset of the region.</param>
    /// <param name="length">The length of the region (at most <see cref="int.MaxValue"/>).</param>
    /// <param name="access">The requested access; read-only mappings should keep the default.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="mappedFile" /> is <c>null</c>.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="fileOffset" /> or <paramref name="length" /> is negative.</exception>
    public MappedSegment(
        MemoryMappedFile mappedFile,
        long fileOffset,
        int length,
        MemoryMappedFileAccess access = MemoryMappedFileAccess.Read) {
        ArgumentNullException.ThrowIfNull(mappedFile);
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        FileOffset = fileOffset;
        _accessor = mappedFile.CreateViewAccessor(fileOffset, length, access);
        _manager = new MappedMemoryManager(_accessor.SafeMemoryMappedViewHandle, _accessor.PointerOffset, length);
        _memory = _manager.Memory;
    }

    /// <summary>The absolute file offset of the region this segment covers.</summary>
    /// <value>The absolute file offset of the region this segment covers.</value>
    public long FileOffset {
        get;
    }

    /// <summary>The number of bytes covered by this segment.</summary>
    /// <value>The number of bytes covered by this segment.</value>
    public int Length => _manager.Length;

    /// <summary>
    /// The whole segment as read-only memory. Prefer <see cref="Lease"/> when the memory will outlive
    /// the immediate call, so its lifetime is protected by the reference count.
    /// </summary>
    /// <value>
    /// The whole segment as read-only memory. Prefer <see cref="Lease"/> when the memory will outlive the
    /// immediate call, so its lifetime is protected by the reference count.
    /// </value>
    /// <exception cref="System.ObjectDisposedException">The <see cref="MappedSegment"/> has been fully released.</exception>
    public ReadOnlyMemory<byte> Memory {
        get {
            if (Volatile.Read(ref _refCount) <= 0) {
                throw new ObjectDisposedException(nameof(MappedSegment));
            }

            return _memory;
        }
    }

    /// <summary>
    /// Acquires a reference-counted lease over this segment.
    /// </summary>
    /// <returns>A <see cref="ViewLease"/> that keeps the backing view alive for its lifetime.</returns>
    /// <exception cref="System.ObjectDisposedException">The segment's owner has been disposed or it is fully released.</exception>
    public ViewLease Lease() {
        if (Volatile.Read(ref _ownerReleased) != 0) {
            throw new ObjectDisposedException(nameof(MappedSegment));
        }

        while (true) {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0) {
                throw new ObjectDisposedException(nameof(MappedSegment));
            }

            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current) {
                return new ViewLease(this, _memory);
            }
        }
    }

    /// <summary>
    /// Decrements the reference count and unmaps the view when the count reaches zero.
    /// </summary>
    internal void ReleaseLease() {
        if (Interlocked.Decrement(ref _refCount) == 0) {
            Unmap();
        }
    }

    /// <summary>Releases the owner reference. The view is unmapped once all outstanding leases are also released.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0) {
            ReleaseLease();
        }
    }

    /// <summary>Releases all unmanaged resources: disposes the memory manager and the view accessor.</summary>
    private void Unmap() {
        ((IDisposable)_manager).Dispose();
        _accessor.Dispose();
    }
}
