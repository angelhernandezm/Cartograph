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
    private ViewLease[]? _leases;

    internal MappedSlice(ReadOnlySequence<byte> sequence, ViewLease[] leases)
    {
        Sequence = sequence;
        _leases = leases;
    }

    /// <summary>The sliced bytes. Valid only until the slice is disposed.</summary>
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
