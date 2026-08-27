using System.Buffers;

namespace Cartograph;

/// <summary>
/// A <see cref="ReadOnlySequenceSegment{Byte}"/> node backed by a mapped memory region.
/// </summary>
/// <remarks>
/// These nodes are the building blocks of an arbitrary-length <see cref="ReadOnlySequence{Byte}"/>
/// that spans multiple mapped views. Because <see cref="Span{T}"/> and <see cref="Memory{T}"/>
/// lengths are <see cref="int"/>, any file larger than ~2 GiB REQUIRES multiple views; chaining
/// these nodes is how the substrate presents such a file as one logical, seekable byte sequence.
/// </remarks>
public sealed class MappedSequenceSegment : ReadOnlySequenceSegment<byte>
{
    /// <summary>Creates the first node of a sequence over <paramref name="memory"/>.</summary>
    public MappedSequenceSegment(ReadOnlyMemory<byte> memory)
    {
        Memory = memory;
        RunningIndex = 0;
    }

    private MappedSequenceSegment(ReadOnlyMemory<byte> memory, long runningIndex)
    {
        Memory = memory;
        RunningIndex = runningIndex;
    }

    /// <summary>Appends <paramref name="memory"/> as the next node and returns it.</summary>
    public MappedSequenceSegment Append(ReadOnlyMemory<byte> memory)
    {
        MappedSequenceSegment next = new(memory, RunningIndex + Memory.Length);
        Next = next;
        return next;
    }
}
