// ============================================================================
// Cartograph
// File: SegmentedArtifactWriter.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Builds a Cartograph artifact from one or more append-only segments, computing
// aligned layouts, per-record and per-segment XxHash3 checksums, and writing a
// self-consistent, checksummed artifact to a stream or file path.
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

using System.Buffers.Binary;
using System.IO.Hashing;

namespace Cartograph.Format;

/// <summary>
/// Builds a Cartograph artifact from one or more append-only segments, each holding a sequence of
/// records. The writer computes an explicit, aligned layout, per-record and per-segment XxHash3
/// checksums, and a checksummed header, then publishes them behind a single manifest.
/// </summary>
/// <remarks>
/// Segments are immutable once written. The manifest is written last and records which segments are
/// live, giving an atomic publish point: a reader that observes the new manifest sees a fully
/// written, self-consistent set of segments.
/// </remarks>
public sealed class SegmentedArtifactWriter
{
    /// <summary>The ordered list of segment builders added to this writer.</summary>
    private readonly List<SegmentBuilder> _segments = [];

    /// <summary>Adds a new segment and returns a builder to append records to it.</summary>
    /// <param name="segmentId">An optional stable id; defaults to the segment's ordinal.</param>
    /// <returns>A <see cref="SegmentBuilder"/> that accepts records for the new segment.</returns>
    public SegmentBuilder AddSegment(uint? segmentId = null)
    {
        SegmentBuilder builder = new(segmentId ?? (uint)_segments.Count);
        _segments.Add(builder);
        return builder;
    }

    /// <summary>Writes the artifact to <paramref name="path"/>, overwriting any existing file.</summary>
    /// <param name="path">The file system path to write the artifact to.</param>
    /// <exception cref="System.ArgumentException"><paramref name="path"/> is <c>null</c> or empty.</exception>
    /// <exception cref="System.InvalidOperationException">Layout error: attempted to pad backwards.</exception>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Save(stream);
    }

    /// <summary>Writes the artifact to <paramref name="stream"/>, which must be writable and seekable.</summary>
    /// <param name="stream">The destination stream; must support writing.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="System.InvalidOperationException">Layout error: attempted to pad backwards.</exception>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        SegmentGeometry[] geometry = ComputeLayout(out long manifestOffset, out int manifestLength);
        ulong contentLength = (ulong)(manifestOffset + manifestLength);

        ArtifactHeader header = new()
        {
            VersionMajor = ArtifactFormat.VersionMajor,
            VersionMinor = ArtifactFormat.VersionMinor,
            PointerSize = (byte)IntPtr.Size,
            ManifestOffset = (ulong)manifestOffset,
            ManifestLength = (ulong)manifestLength,
            ContentLength = contentLength,
        };

        Span<byte> headerBytes = stackalloc byte[ArtifactFormat.HeaderSize];
        header.Write(headerBytes);

        long position = 0;
        byte[] padScratch = new byte[ArtifactFormat.Alignment];

        stream.Write(headerBytes);
        position += headerBytes.Length;

        SegmentDescriptor[] descriptors = new SegmentDescriptor[_segments.Count];
        for (int s = 0; s < _segments.Count; s++)
        {
            SegmentBuilder segment = _segments[s];
            SegmentGeometry geo = geometry[s];

            PadTo(stream, ref position, geo.DataOffset, padScratch, hasher: null);

            XxHash3 segmentHasher = new();
            ulong[] checksums = new ulong[segment.Records.Count];

            if (geo.PayloadFirst)
            {
                // Payload precedes the directory, so each record's checksum falls out of the same
                // pass that writes it. Without this a streamed record would have to be read twice:
                // once to fill in its directory entry and again to emit its bytes.
                WritePayloads(stream, ref position, segment, geo, padScratch, segmentHasher, checksums);
                PadTo(stream, ref position, geo.DirectoryOffset, padScratch, segmentHasher);
                WriteDirectory(stream, ref position, segment, geo, segmentHasher, checksums);
            }
            else
            {
                for (int r = 0; r < segment.Records.Count; r++)
                {
                    checksums[r] = segment.Records[r].ComputeChecksum();
                }

                WriteDirectory(stream, ref position, segment, geo, segmentHasher, checksums);
                PadTo(stream, ref position, geo.PayloadOffset, padScratch, segmentHasher);
                WritePayloads(stream, ref position, segment, geo, padScratch, segmentHasher, checksums);
            }

            // Trailing pad to the end of the segment region.
            long segmentEnd = geo.DataOffset + geo.DataLength;
            PadTo(stream, ref position, segmentEnd, padScratch, segmentHasher);

            descriptors[s] = new SegmentDescriptor
            {
                SegmentId = segment.Id,
                Flags = ArtifactFormat.SegmentFlagLive,
                DataOffset = (ulong)geo.DataOffset,
                DataLength = (ulong)geo.DataLength,
                RecordCount = (ulong)segment.Records.Count,
                DirectoryOffset = (ulong)geo.DirectoryOffset,
                PayloadOffset = (ulong)geo.PayloadOffset,
                Checksum = segmentHasher.GetCurrentHashAsUInt64(),
            };
        }

        PadTo(stream, ref position, manifestOffset, padScratch, hasher: null);

        SegmentManifest manifest = new(descriptors);
        byte[] manifestBytes = new byte[manifest.ByteLength];
        manifest.Write(manifestBytes);
        stream.Write(manifestBytes);
        position += manifestBytes.Length;

        stream.Flush();
    }

    /// <summary>
    /// Computes the byte-aligned layout for every segment and the trailing manifest,
    /// populating relative record offsets within each segment's payload region.
    /// </summary>
    /// <param name="manifestOffset">Receives the file-relative byte offset of the manifest.</param>
    /// <param name="manifestLength">Receives the byte length of the serialized manifest.</param>
    /// <returns>An array of <see cref="SegmentGeometry"/> values, one per added segment, in order.</returns>
    private SegmentGeometry[] ComputeLayout(out long manifestOffset, out int manifestLength)
    {
        SegmentGeometry[] geometry = new SegmentGeometry[_segments.Count];
        long offset = ArtifactFormat.HeaderSize;

        for (int s = 0; s < _segments.Count; s++)
        {
            SegmentBuilder segment = _segments[s];
            long dataOffset = Platform.AlignUp(offset, ArtifactFormat.Alignment);
            long directorySize = (long)segment.Records.Count * ArtifactFormat.RecordEntrySize;

            long[] relOffsets = new long[segment.Records.Count];
            long relRunning = 0;
            for (int r = 0; r < segment.Records.Count; r++)
            {
                relOffsets[r] = relRunning;
                relRunning = Platform.AlignUp(relRunning + segment.Records[r].Length, ArtifactFormat.Alignment);
            }

            // A zero-byte payload region would start exactly where the directory does, so keep the
            // directory-first layout in that case; there is nothing to stream and nothing to overlap.
            bool payloadFirst = segment.RequiresPayloadFirstLayout && relRunning > 0;

            long directoryOffset;
            long payloadOffset;
            long dataLength;
            if (payloadFirst)
            {
                payloadOffset = dataOffset;
                directoryOffset = Platform.AlignUp(payloadOffset + relRunning, ArtifactFormat.Alignment);
                dataLength = directoryOffset + directorySize - dataOffset;
            }
            else
            {
                directoryOffset = dataOffset;
                payloadOffset = Platform.AlignUp(directoryOffset + directorySize, ArtifactFormat.Alignment);
                dataLength = payloadOffset - dataOffset + relRunning;
            }

            geometry[s] = new SegmentGeometry(
                dataOffset,
                directoryOffset,
                payloadOffset,
                dataLength,
                relOffsets,
                payloadFirst);
            offset = dataOffset + dataLength;
        }

        manifestOffset = Platform.AlignUp(offset, ArtifactFormat.Alignment);
        manifestLength = ArtifactFormat.ManifestHeaderSize + _segments.Count * ArtifactFormat.SegmentDescriptorSize;
        return geometry;
    }

    /// <summary>
    /// Writes the segment's record directory, one entry per record, folding it into the segment checksum.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="position">The current write position; updated in place.</param>
    /// <param name="segment">The segment whose directory is being written.</param>
    /// <param name="geo">The computed layout for <paramref name="segment"/>.</param>
    /// <param name="hasher">The XxHash3 hasher accumulating the segment checksum.</param>
    /// <param name="checksums">The per-record checksums to record, indexed by record position.</param>
    private static void WriteDirectory(
        Stream stream,
        ref long position,
        SegmentBuilder segment,
        in SegmentGeometry geo,
        XxHash3 hasher,
        ulong[] checksums)
    {
        Span<byte> entry = stackalloc byte[ArtifactFormat.RecordEntrySize];
        for (int r = 0; r < segment.Records.Count; r++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0..], (ulong)geo.RecordRelOffsets[r]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)segment.Records[r].Length);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], checksums[r]);
            WriteAndHash(stream, ref position, entry, hasher);
        }
    }

    /// <summary>
    /// Writes every record payload in the segment, padding each up to alignment and folding the
    /// bytes into the segment checksum.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="position">The current write position; updated in place.</param>
    /// <param name="segment">The segment whose payloads are being written.</param>
    /// <param name="geo">The computed layout for <paramref name="segment"/>.</param>
    /// <param name="padScratch">A scratch buffer used to stage zero-fill chunks.</param>
    /// <param name="hasher">The XxHash3 hasher accumulating the segment checksum.</param>
    /// <param name="checksums">Receives each record's checksum as its bytes are written.</param>
    /// <exception cref="System.InvalidOperationException">Layout error: attempted to pad backwards.</exception>
    private static void WritePayloads(
        Stream stream,
        ref long position,
        SegmentBuilder segment,
        in SegmentGeometry geo,
        byte[] padScratch,
        XxHash3 hasher,
        ulong[] checksums)
    {
        for (int r = 0; r < segment.Records.Count; r++)
        {
            long recordStart = geo.PayloadOffset + geo.RecordRelOffsets[r];
            PadTo(stream, ref position, recordStart, padScratch, hasher);

            RecordSource record = segment.Records[r];
            checksums[r] = record.WriteTo(stream, hasher);
            position += record.Length;
        }
    }

    /// <summary>
    /// Writes <paramref name="data"/> to the stream, feeds it into <paramref name="hasher"/>,
    /// and advances <paramref name="position"/> by the number of bytes written.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="position">The current write position; updated in place.</param>
    /// <param name="data">The bytes to write and hash.</param>
    /// <param name="hasher">The XxHash3 hasher accumulating the segment checksum.</param>
    private static void WriteAndHash(Stream stream, ref long position, ReadOnlySpan<byte> data, XxHash3 hasher)
    {
        stream.Write(data);
        hasher.Append(data);
        position += data.Length;
    }

    /// <summary>
    /// Writes zero-fill padding from the current <paramref name="position"/> up to <paramref name="target"/>,
    /// optionally feeding the padding into <paramref name="hasher"/>.
    /// </summary>
    /// <param name="stream">The stream to write zero padding to.</param>
    /// <param name="position">The current write position; updated in place.</param>
    /// <param name="target">The target byte position; must be &gt;= <paramref name="position"/>.</param>
    /// <param name="scratch">A scratch buffer used to stage zero-fill chunks.</param>
    /// <param name="hasher">The optional XxHash3 hasher to feed padding bytes into; may be <see langword="null"/>.</param>
    /// <exception cref="System.InvalidOperationException">Layout error: attempted to pad backwards.</exception>
    private static void PadTo(Stream stream, ref long position, long target, byte[] scratch, XxHash3? hasher)
    {
        if (target < position)
        {
            throw new InvalidOperationException("Layout error: attempted to pad backwards.");
        }

        long remaining = target - position;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, scratch.Length);
            Array.Clear(scratch, 0, chunk);
            stream.Write(scratch, 0, chunk);
            hasher?.Append(scratch.AsSpan(0, chunk));
            position += chunk;
            remaining -= chunk;
        }
    }

    /// <summary>
    /// Holds the computed byte-level layout for a single segment within the artifact being built.
    /// </summary>
    private readonly struct SegmentGeometry(
        long dataOffset,
        long directoryOffset,
        long payloadOffset,
        long dataLength,
        long[] recordRelOffsets,
        bool payloadFirst)
    {
        /// <summary>The file-relative byte offset at which the segment data region begins.</summary>
        /// <value>The file-relative byte offset at which the segment data region begins.</value>
        public long DataOffset { get; } = dataOffset;

        /// <summary>The file-relative byte offset of the record directory within this segment.</summary>
        /// <value>The file-relative byte offset of the record directory within this segment.</value>
        public long DirectoryOffset { get; } = directoryOffset;

        /// <summary>The file-relative byte offset of the record payload region within this segment.</summary>
        /// <value>The file-relative byte offset of the record payload region within this segment.</value>
        public long PayloadOffset { get; } = payloadOffset;

        /// <summary>The total byte length of this segment's data region.</summary>
        /// <value>The total byte length of this segment's data region.</value>
        public long DataLength { get; } = dataLength;

        /// <summary>The payload-relative byte offset of each record, indexed by record position.</summary>
        /// <value>The payload-relative byte offset of each record, indexed by record position.</value>
        public long[] RecordRelOffsets { get; } = recordRelOffsets;

        /// <summary>Whether the payload region precedes the record directory within this segment.</summary>
        /// <value>Whether the payload region precedes the record directory within this segment.</value>
        public bool PayloadFirst { get; } = payloadFirst;
    }
}

/// <summary>Accumulates the records for a single segment being built by a <see cref="SegmentedArtifactWriter"/>.</summary>
/// <remarks>
/// Records may be buffered (<see cref="AddRecord"/>) or streamed from disk
/// (<see cref="AddFileRecord(string)"/>). A segment holding any streamed record is laid out
/// payload-first so the writer can emit and checksum it in a single pass, which keeps packing
/// memory-bounded regardless of how much file data the segment covers.
/// </remarks>
public sealed class SegmentBuilder
{
    /// <summary>The payload sources accumulated for this segment, in record order.</summary>
    private readonly List<RecordSource> _records = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentBuilder" /> class.
    /// </summary>
    /// <param name="id">The stable numeric identifier for this segment.</param>
    internal SegmentBuilder(uint id) => Id = id;

    /// <summary>The stable numeric identifier assigned to this segment.</summary>
    /// <value>The stable numeric identifier assigned to this segment.</value>
    internal uint Id { get; }

    /// <summary>The record payload sources accumulated so far, in record order.</summary>
    /// <value>The record payload sources accumulated so far, in record order.</value>
    internal IReadOnlyList<RecordSource> Records => _records;

    /// <summary>
    /// Whether this segment contains a record that cannot be checksummed without reading its backing
    /// store, and therefore must be laid out payload-first.
    /// </summary>
    /// <value>
    /// Whether this segment contains a record that cannot be checksummed without reading its backing store,
    /// and therefore must be laid out payload-first.
    /// </value>
    internal bool RequiresPayloadFirstLayout { get; private set; }

    /// <summary>The number of records added so far.</summary>
    /// <value>The number of records added so far.</value>
    public int RecordCount => _records.Count;

    /// <summary>Appends a record, copying <paramref name="data"/> into the writer.</summary>
    /// <param name="data">The raw bytes of the record to append.</param>
    /// <returns>This <see cref="SegmentBuilder"/> to allow method chaining.</returns>
    public SegmentBuilder AddRecord(ReadOnlySpan<byte> data)
    {
        _records.Add(new BufferedRecordSource(data.ToArray()));
        return this;
    }

    /// <summary>
    /// Appends the entire contents of <paramref name="path"/> as a single record, streaming it at
    /// save time instead of buffering it on the managed heap.
    /// </summary>
    /// <param name="path">The path of the file to append as a record.</param>
    /// <returns>This <see cref="SegmentBuilder"/> to allow method chaining.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="path"/> is <c>null</c> or empty.</exception>
    /// <exception cref="System.IO.FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException">The file is larger than <see cref="ArtifactFormat.MaxRecordLength"/> bytes.</exception>
    public SegmentBuilder AddFileRecord(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileInfo info = new(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"Cannot append a record from missing file '{path}'.", path);
        }

        return AddFileRecord(path, offset: 0, length: info.Length);
    }

    /// <summary>
    /// Appends the byte range <c>[offset, offset + length)</c> of <paramref name="path"/> as a single
    /// record, streaming it at save time instead of buffering it on the managed heap.
    /// </summary>
    /// <param name="path">The path of the file to read the record's bytes from.</param>
    /// <param name="offset">The byte offset within the file at which the record begins.</param>
    /// <param name="length">The number of bytes the record spans.</param>
    /// <returns>This <see cref="SegmentBuilder"/> to allow method chaining.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="path"/> is <c>null</c> or empty.</exception>
    /// <exception cref="System.IO.FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is negative.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="length"/> exceeds <see cref="ArtifactFormat.MaxRecordLength"/>.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException">The requested range extends past the end of the file.</exception>
    public SegmentBuilder AddFileRecord(string path, long offset, long length)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // Enforced here, rather than only when the artifact is opened, so an oversized record fails
        // with an actionable message at the point it is added. Split large inputs across records.
        if (length > ArtifactFormat.MaxRecordLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"A single record cannot exceed {ArtifactFormat.MaxRecordLength} bytes; " +
                "split the input across multiple records.");
        }

        FileInfo info = new(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"Cannot append a record from missing file '{path}'.", path);
        }

        if (offset + length > info.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"Range [{offset}, {offset + length}) extends past the end of '{path}' ({info.Length} bytes).");
        }

        _records.Add(new FileRecordSource(info.FullName, offset, length));
        RequiresPayloadFirstLayout = true;
        return this;
    }
}
