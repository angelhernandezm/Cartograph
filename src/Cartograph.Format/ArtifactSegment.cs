// ============================================================================
// Cartograph
// File: ArtifactSegment.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Represents a single live, immutable segment within an opened Artifact, providing
// synchronous and asynchronous zero-copy record reads with optional checksum verification.
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

namespace Cartograph.Format;

/// <summary>
/// A live segment of an <see cref="Artifact"/>: an immutable, append-only collection of records.
/// </summary>
public sealed class ArtifactSegment {
    /// <summary>The chunk source used to read raw bytes from the artifact file.</summary>
    private readonly IChunkSource _source;

    /// <summary>The on-disk descriptor for this segment, holding offsets, lengths, and the segment checksum.</summary>
    private readonly SegmentDescriptor _descriptor;

    /// <summary>The parsed per-record directory (offsets, lengths, checksums).</summary>
    private readonly RecordDirectory _directory;

    /// <summary>Whether to verify each record's XxHash3 checksum on read.</summary>
    private readonly bool _verifyChecksums;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtifactSegment" /> class.
    /// </summary>
    /// <param name="source">The chunk source providing access to the artifact's raw bytes.</param>
    /// <param name="descriptor">The segment descriptor parsed from the manifest.</param>
    /// <param name="directory">The parsed record directory for this segment.</param>
    /// <param name="verifyChecksums">Whether to verify each record's checksum on read.</param>
    internal ArtifactSegment(IChunkSource source, SegmentDescriptor descriptor, RecordDirectory directory, bool verifyChecksums) {
        _source = source;
        _descriptor = descriptor;
        _directory = directory;
        _verifyChecksums = verifyChecksums;
    }

    /// <summary>The segment's stable identifier.</summary>
    /// <value>The segment's stable identifier.</value>
    public uint SegmentId => _descriptor.SegmentId;

    /// <summary>The number of records in the segment.</summary>
    /// <value>The number of records in the segment.</value>
    public int RecordCount => _directory.Lengths.Length;

    /// <summary>The total byte length of the segment region.</summary>
    /// <value>The total byte length of the segment region.</value>
    public long DataLength => (long)_descriptor.DataLength;

    /// <summary>The file-relative byte offset at which this segment's region begins.</summary>
    /// <value>The file-relative byte offset at which this segment's region begins.</value>
    public long DataOffset => (long)_descriptor.DataOffset;

    /// <summary>The file-relative byte offset of this segment's record directory.</summary>
    /// <value>The file-relative byte offset of this segment's record directory.</value>
    public long DirectoryOffset => (long)_descriptor.DirectoryOffset;

    /// <summary>The file-relative byte offset of this segment's record payload region.</summary>
    /// <value>The file-relative byte offset of this segment's record payload region.</value>
    public long PayloadOffset => (long)_descriptor.PayloadOffset;

    /// <summary>
    /// Whether this segment's payload region precedes its record directory, which is how segments
    /// containing streamed records are laid out.
    /// </summary>
    /// <value>
    /// Whether this segment's payload region precedes its record directory, which is how segments
    /// containing streamed records are laid out.
    /// </value>
    public bool IsPayloadFirst => PayloadOffset < DirectoryOffset;

    /// <summary>The XxHash3 checksum recorded for the whole segment region.</summary>
    /// <value>The XxHash3 checksum recorded for the whole segment region.</value>
    public ulong Checksum => _descriptor.Checksum;

    /// <summary>The length in bytes of the record at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based index of the record within this segment.</param>
    /// <returns>The byte length of the record at <paramref name="index"/>.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public int GetRecordLength(int index) {
        ValidateIndex(index);
        return (int)_directory.Lengths[index];
    }

    /// <summary>Reads the record at <paramref name="index"/> as a zero-copy sequence, verifying its checksum.</summary>
    /// <param name="index">The zero-based index of the record within this segment.</param>
    /// <returns>A <see cref="RecordLease"/> wrapping the record bytes; the caller must dispose it.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="CartographFormatException">The record's checksum does not match (corruption).</exception>
    public RecordLease ReadRecord(int index) {
        ValidateIndex(index);
        long offset = (long)_descriptor.PayloadOffset + _directory.RelOffsets[index];
        int length = (int)_directory.Lengths[index];

        ChunkLease chunk = _source.Read(offset, length);
        return Finish(chunk, index);
    }

    /// <summary>Asynchronously reads the record at <paramref name="index"/>, verifying its checksum.</summary>
    /// <param name="index">The zero-based index of the record within this segment.</param>
    /// <param name="cancellationToken">A token that can cancel the asynchronous read.</param>
    /// <returns>A <see cref="RecordLease"/> wrapping the record bytes; the caller must dispose it.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="CartographFormatException">The record's checksum does not match (corruption).</exception>
    public async ValueTask<RecordLease> ReadRecordAsync(int index, CancellationToken cancellationToken = default) {
        ValidateIndex(index);
        long offset = (long)_descriptor.PayloadOffset + _directory.RelOffsets[index];
        int length = (int)_directory.Lengths[index];

        ChunkLease chunk = await _source.ReadAsync(offset, length, cancellationToken).ConfigureAwait(false);
        return Finish(chunk, index);
    }

    /// <summary>
    /// Optionally verifies the checksum of the chunk and wraps it in a <see cref="RecordLease"/>.
    /// </summary>
    /// <param name="chunk">The raw chunk lease returned from the chunk source.</param>
    /// <param name="index">The zero-based record index, used in the exception message on failure.</param>
    /// <returns>A <see cref="RecordLease"/> wrapping the verified chunk.</returns>
    /// <exception cref="CartographFormatException">Record failed checksum verification (corrupt data).</exception>
    private RecordLease Finish(ChunkLease chunk, int index) {
        if (_verifyChecksums) {
            ulong actual = Artifact.HashSequence(chunk.Sequence);
            if (actual != _directory.Checksums[index]) {
                chunk.Dispose();
                throw new CartographFormatException(
                    $"Record {index} in segment {_descriptor.SegmentId} failed checksum verification (corrupt data).");
            }
        }

        return new RecordLease(chunk);
    }

    /// <summary>
    /// Throws <see cref="System.ArgumentOutOfRangeException"/> if <paramref name="index"/> is outside [0, <see cref="RecordCount"/>).
    /// </summary>
    /// <param name="index">The index to validate.</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    private void ValidateIndex(int index) {
        if ((uint)index >= (uint)RecordCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
