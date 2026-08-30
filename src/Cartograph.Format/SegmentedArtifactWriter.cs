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
        Span<byte> entry = stackalloc byte[ArtifactFormat.RecordEntrySize];
        for (int s = 0; s < _segments.Count; s++)
        {
            SegmentBuilder segment = _segments[s];
            SegmentGeometry geo = geometry[s];

            PadTo(stream, ref position, geo.DataOffset, padScratch, hasher: null);

            XxHash3 segmentHasher = new();

            // Record directory.
            for (int r = 0; r < segment.Records.Count; r++)
            {
                byte[] payload = segment.Records[r];
                BinaryPrimitives.WriteUInt64LittleEndian(entry[0..], (ulong)geo.RecordRelOffsets[r]);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)payload.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], XxHash3.HashToUInt64(payload));
                WriteAndHash(stream, ref position, entry, segmentHasher);
            }

            // Pad to the payload region.
            PadTo(stream, ref position, geo.PayloadOffset, padScratch, segmentHasher);

            // Record payloads, each padded up to alignment.
            for (int r = 0; r < segment.Records.Count; r++)
            {
                long recordStart = geo.PayloadOffset + geo.RecordRelOffsets[r];
                PadTo(stream, ref position, recordStart, padScratch, segmentHasher);
                byte[] payload = segment.Records[r];
                WriteAndHash(stream, ref position, payload, segmentHasher);
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
            long directoryOffset = dataOffset;
            long directorySize = (long)segment.Records.Count * ArtifactFormat.RecordEntrySize;
            long payloadOffset = Platform.AlignUp(directoryOffset + directorySize, ArtifactFormat.Alignment);

            long[] relOffsets = new long[segment.Records.Count];
            long relRunning = 0;
            for (int r = 0; r < segment.Records.Count; r++)
            {
                relOffsets[r] = relRunning;
                relRunning = Platform.AlignUp(relRunning + segment.Records[r].Length, ArtifactFormat.Alignment);
            }

            long dataLength = payloadOffset - dataOffset + relRunning;
            geometry[s] = new SegmentGeometry(dataOffset, directoryOffset, payloadOffset, dataLength, relOffsets);
            offset = dataOffset + dataLength;
        }

        manifestOffset = Platform.AlignUp(offset, ArtifactFormat.Alignment);
        manifestLength = ArtifactFormat.ManifestHeaderSize + _segments.Count * ArtifactFormat.SegmentDescriptorSize;
        return geometry;
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
        long[] recordRelOffsets)
    {
        /// <summary>The file-relative byte offset at which the segment data region begins.</summary>
        public long DataOffset { get; } = dataOffset;

        /// <summary>The file-relative byte offset of the record directory within this segment.</summary>
        public long DirectoryOffset { get; } = directoryOffset;

        /// <summary>The file-relative byte offset of the record payload region within this segment.</summary>
        public long PayloadOffset { get; } = payloadOffset;

        /// <summary>The total byte length of this segment's data region.</summary>
        public long DataLength { get; } = dataLength;

        /// <summary>The payload-relative byte offset of each record, indexed by record position.</summary>
        public long[] RecordRelOffsets { get; } = recordRelOffsets;
    }
}

/// <summary>Accumulates the records for a single segment being built by a <see cref="SegmentedArtifactWriter"/>.</summary>
public sealed class SegmentBuilder
{
    /// <summary>The accumulated record byte arrays for this segment.</summary>
    private readonly List<byte[]> _records = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentBuilder" /> class.
    /// </summary>
    /// <param name="id">The stable numeric identifier for this segment.</param>
    internal SegmentBuilder(uint id) => Id = id;

    /// <summary>The stable numeric identifier assigned to this segment.</summary>
    internal uint Id { get; }

    /// <summary>The records accumulated so far, as a read-only list of raw byte arrays.</summary>
    internal IReadOnlyList<byte[]> Records => _records;

    /// <summary>The number of records added so far.</summary>
    public int RecordCount => _records.Count;

    /// <summary>Appends a record, copying <paramref name="data"/> into the writer.</summary>
    /// <param name="data">The raw bytes of the record to append.</param>
    /// <returns>This <see cref="SegmentBuilder"/> to allow method chaining.</returns>
    public SegmentBuilder AddRecord(ReadOnlySpan<byte> data)
    {
        _records.Add(data.ToArray());
        return this;
    }
}
