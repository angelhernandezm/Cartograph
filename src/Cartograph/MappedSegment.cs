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
public sealed class MappedSegment : IDisposable
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly MappedMemoryManager _manager;
    private readonly ReadOnlyMemory<byte> _memory;
    private int _refCount = 1;
    private int _ownerReleased;

    /// <summary>Creates a segment over <paramref name="length"/> bytes starting at <paramref name="fileOffset"/>.</summary>
    /// <param name="mappedFile">The mapped file to create a view over.</param>
    /// <param name="fileOffset">The absolute file offset of the region.</param>
    /// <param name="length">The length of the region (at most <see cref="int.MaxValue"/>).</param>
    /// <param name="access">The requested access; read-only mappings should keep the default.</param>
    public MappedSegment(
        MemoryMappedFile mappedFile,
        long fileOffset,
        int length,
        MemoryMappedFileAccess access = MemoryMappedFileAccess.Read)
    {
        ArgumentNullException.ThrowIfNull(mappedFile);
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        FileOffset = fileOffset;
        _accessor = mappedFile.CreateViewAccessor(fileOffset, length, access);
        _manager = new MappedMemoryManager(_accessor.SafeMemoryMappedViewHandle, _accessor.PointerOffset, length);
        _memory = _manager.Memory;
    }

    /// <summary>The absolute file offset of the region this segment covers.</summary>
    public long FileOffset { get; }

    /// <summary>The number of bytes covered by this segment.</summary>
    public int Length => _manager.Length;

    /// <summary>
    /// The whole segment as read-only memory. Prefer <see cref="Lease"/> when the memory will outlive
    /// the immediate call, so its lifetime is protected by the reference count.
    /// </summary>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            if (Volatile.Read(ref _refCount) <= 0)
            {
                throw new ObjectDisposedException(nameof(MappedSegment));
            }

            return _memory;
        }
    }

    /// <summary>Acquires a reference-counted lease over this segment.</summary>
    /// <exception cref="ObjectDisposedException">The segment's owner has been disposed or it is fully released.</exception>
    public ViewLease Lease()
    {
        if (Volatile.Read(ref _ownerReleased) != 0)
        {
            throw new ObjectDisposedException(nameof(MappedSegment));
        }

        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(nameof(MappedSegment));
            }

            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
            {
                return new ViewLease(this, _memory);
            }
        }
    }

    internal void ReleaseLease()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            Unmap();
        }
    }

    /// <summary>Releases the owner reference. The view is unmapped once all outstanding leases are also released.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
        {
            ReleaseLease();
        }
    }

    private void Unmap()
    {
        ((IDisposable)_manager).Dispose();
        _accessor.Dispose();
    }
}
