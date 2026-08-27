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
public sealed class ViewLease : IDisposable
{
    private MappedSegment? _segment;
    private readonly ReadOnlyMemory<byte> _memory;
    private int _disposed;

    internal ViewLease(MappedSegment segment, ReadOnlyMemory<byte> memory)
    {
        _segment = segment;
        _memory = memory;
    }

    /// <summary>The leased region as read-only memory. Valid only until the lease is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The lease has been disposed.</exception>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _memory;
        }
    }

    /// <summary>The leased region as a read-only span. Valid only until the lease is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The lease has been disposed.</exception>
    public ReadOnlySpan<byte> Span => Memory.Span;

    /// <summary>Releases this lease, decrementing the segment reference count. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            MappedSegment? segment = _segment;
            _segment = null;
            segment?.ReleaseLease();
        }
    }
}
