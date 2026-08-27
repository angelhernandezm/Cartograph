namespace Cartograph.Format;

/// <summary>
/// A live segment of an <see cref="Artifact"/>: an immutable, append-only collection of records.
/// </summary>
public sealed class ArtifactSegment
{
    private readonly IChunkSource _source;
    private readonly SegmentDescriptor _descriptor;
    private readonly RecordDirectory _directory;
    private readonly bool _verifyChecksums;

    internal ArtifactSegment(IChunkSource source, SegmentDescriptor descriptor, RecordDirectory directory, bool verifyChecksums)
    {
        _source = source;
        _descriptor = descriptor;
        _directory = directory;
        _verifyChecksums = verifyChecksums;
    }

    /// <summary>The segment's stable identifier.</summary>
    public uint SegmentId => _descriptor.SegmentId;

    /// <summary>The number of records in the segment.</summary>
    public int RecordCount => _directory.Lengths.Length;

    /// <summary>The total byte length of the segment region.</summary>
    public long DataLength => (long)_descriptor.DataLength;

    /// <summary>The XxHash3 checksum recorded for the whole segment region.</summary>
    public ulong Checksum => _descriptor.Checksum;

    /// <summary>The length in bytes of the record at <paramref name="index"/>.</summary>
    public int GetRecordLength(int index)
    {
        ValidateIndex(index);
        return (int)_directory.Lengths[index];
    }

    /// <summary>Reads the record at <paramref name="index"/> as a zero-copy sequence, verifying its checksum.</summary>
    /// <exception cref="CartographFormatException">The record's checksum does not match (corruption).</exception>
    public RecordLease ReadRecord(int index)
    {
        ValidateIndex(index);
        long offset = (long)_descriptor.PayloadOffset + _directory.RelOffsets[index];
        int length = (int)_directory.Lengths[index];

        ChunkLease chunk = _source.Read(offset, length);
        return Finish(chunk, index);
    }

    /// <summary>Asynchronously reads the record at <paramref name="index"/>, verifying its checksum.</summary>
    public async ValueTask<RecordLease> ReadRecordAsync(int index, CancellationToken cancellationToken = default)
    {
        ValidateIndex(index);
        long offset = (long)_descriptor.PayloadOffset + _directory.RelOffsets[index];
        int length = (int)_directory.Lengths[index];

        ChunkLease chunk = await _source.ReadAsync(offset, length, cancellationToken).ConfigureAwait(false);
        return Finish(chunk, index);
    }

    private RecordLease Finish(ChunkLease chunk, int index)
    {
        if (_verifyChecksums)
        {
            ulong actual = Artifact.HashSequence(chunk.Sequence);
            if (actual != _directory.Checksums[index])
            {
                chunk.Dispose();
                throw new CartographFormatException(
                    $"Record {index} in segment {_descriptor.SegmentId} failed checksum verification (corrupt data).");
            }
        }

        return new RecordLease(chunk);
    }

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)RecordCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
