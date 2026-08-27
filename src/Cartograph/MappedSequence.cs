using System.Buffers;

namespace Cartograph;

/// <summary>
/// Stitches multiple mapped memory chunks into a single <see cref="ReadOnlySequence{Byte}"/>.
/// </summary>
/// <remarks>
/// This arbitrary-length, segmented, zero-copy view over many mapped windows is the main primitive
/// that does not already exist in the .NET ecosystem: it lets a &gt; 2 GiB file be consumed as one
/// logical byte sequence without ever copying payload into the managed heap.
/// </remarks>
public static class MappedSequence
{
    /// <summary>Builds a <see cref="ReadOnlySequence{Byte}"/> over the supplied chunks in order.</summary>
    /// <param name="chunks">The mapped chunks, each typically a slice of one mapped window.</param>
    public static ReadOnlySequence<byte> Create(IReadOnlyList<ReadOnlyMemory<byte>> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        if (chunks.Count == 1)
        {
            return new ReadOnlySequence<byte>(chunks[0]);
        }

        MappedSequenceSegment first = new(chunks[0]);
        MappedSequenceSegment current = first;
        for (int i = 1; i < chunks.Count; i++)
        {
            current = current.Append(chunks[i]);
        }

        return new ReadOnlySequence<byte>(first, 0, current, current.Memory.Length);
    }
}
