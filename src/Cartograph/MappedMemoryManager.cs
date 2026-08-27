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
/// pointer for its lifetime via <see cref="SafeMemoryMappedViewHandle.AcquirePointer"/> /
/// <see cref="SafeMemoryMappedViewHandle.ReleasePointer"/>.
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
