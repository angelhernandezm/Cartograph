using System.Buffers;

namespace Cartograph.Format;

/// <summary>
/// A disposable handle to a single record, exposed as a zero-copy <see cref="ReadOnlySequence{Byte}"/>.
/// </summary>
/// <remarks>
/// Under a mapped chunk source the sequence is a view over mapped pages and may span more than one
/// segment when the record crosses a window boundary; under a pooled source it is a single rented
/// buffer. Either way <see cref="Sequence"/> is valid only until the lease is disposed.
/// </remarks>
public sealed class RecordLease : IDisposable
{
    private readonly ChunkLease _chunk;

    internal RecordLease(ChunkLease chunk)
    {
        _chunk = chunk;
    }

    /// <summary>The record bytes. Valid only until this lease is disposed.</summary>
    public ReadOnlySequence<byte> Sequence => _chunk.Sequence;

    /// <summary>The record length in bytes.</summary>
    public long Length => _chunk.Sequence.Length;

    /// <summary>Whether the record occupies a single contiguous span (safe for <c>MemoryMarshal.Cast</c>).</summary>
    public bool IsSingleSegment => _chunk.Sequence.IsSingleSegment;

    /// <summary>
    /// The first (and, when <see cref="IsSingleSegment"/>, only) contiguous span of the record. Use
    /// this for aligned <c>MemoryMarshal.Cast&lt;byte, float&gt;</c> reads over mapped pages.
    /// </summary>
    public ReadOnlySpan<byte> FirstSpan => _chunk.Sequence.FirstSpan;

    /// <summary>Copies the record into a newly allocated array.</summary>
    public byte[] ToArray() => _chunk.Sequence.ToArray();

    /// <summary>Releases the underlying lease or pooled buffer. Safe to call more than once.</summary>
    public void Dispose() => _chunk.Dispose();
}
