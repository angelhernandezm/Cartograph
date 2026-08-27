using System.Buffers;

namespace Cartograph;

/// <summary>
/// A disposable handle to a chunk of bytes read from an <see cref="IChunkSource"/>.
/// </summary>
/// <remarks>
/// The chunk is exposed as a zero-copy <see cref="ReadOnlySequence{Byte}"/>. For mapped sources the
/// backing memory is a slice of one or more mapped windows kept alive by leases; for pooled sources
/// it is a rented buffer. In both cases the sequence is only valid until the lease is disposed, and
/// disposal releases the underlying resource (leases or the pooled buffer).
/// </remarks>
public sealed class ChunkLease : IDisposable
{
    private IDisposable? _owner;

    internal ChunkLease(ReadOnlySequence<byte> sequence, IDisposable? owner)
    {
        Sequence = sequence;
        _owner = owner;
    }

    /// <summary>The chunk bytes. Valid only until this lease is disposed.</summary>
    public ReadOnlySequence<byte> Sequence { get; }

    /// <summary>Releases the backing resource. Safe to call more than once.</summary>
    public void Dispose()
    {
        IDisposable? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Dispose();
    }
}

/// <summary>
/// A pluggable source of file chunks. Two implementations ship with the substrate so the two access
/// strategies can be benchmarked head-to-head: <see cref="MappedChunkSource"/> (mmap, which wins on
/// random reads over a hot page cache) and <see cref="RandomAccessChunkSource"/> (pooled
/// <see cref="System.IO.RandomAccess"/>, which is async-friendly and often wins on large sequential
/// scans). Making this pluggable from day one is a deliberate design decision.
/// </summary>
public interface IChunkSource : IDisposable
{
    /// <summary>The total length of the underlying file in bytes.</summary>
    long Length { get; }

    /// <summary>Reads <c>[offset, offset + length)</c> and returns it as a disposable chunk.</summary>
    ChunkLease Read(long offset, int length);

    /// <summary>Asynchronously reads <c>[offset, offset + length)</c> and returns it as a disposable chunk.</summary>
    ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken cancellationToken = default);
}
