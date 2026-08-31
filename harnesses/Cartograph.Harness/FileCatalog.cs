// ============================================================================
// Cartograph
// File: FileCatalog.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Self-describing index that the harness stores as the first record of the
// first segment, mapping every packed file back to its segment, record and
// global index along with its length, timestamp and checksum
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

using System.Buffers;
using System.Text;

namespace Cartograph.Harness;

/// <summary>
/// Describes a single file that was packed into a Cartograph artifact
/// </summary>
/// <remarks>
/// Cartograph deliberately treats records as opaque bytes, so file names, sizes and timestamps have
/// no representation in the format itself. This type is the harness' answer to that: the metadata
/// lives in a catalog record that the harness writes alongside the payload, which is exactly the
/// pattern an application is expected to follow.
/// </remarks>
internal sealed class CatalogEntry
{
    /// <summary>
    /// Gets the path of the file relative to the packed root, using forward slashes
    /// </summary>
    /// <value>A relative path such as <c>src/Cartograph/MappedFile.cs</c>.</value>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Gets the length of the file, in bytes
    /// </summary>
    /// <value>The total byte length of the file, summed across every record that holds its contents.</value>
    public required long Length { get; init; }

    /// <summary>
    /// Gets the number of consecutive records this file occupies
    /// </summary>
    /// <value>
    /// The count of records the file was split into. Files larger than the configured piece size are
    /// streamed across several records, so a file occupies the range
    /// <c>[<see cref="GlobalIndex" />, <see cref="GlobalIndex" /> + <see cref="RecordCount" />)</c>.
    /// A zero-length file still occupies exactly one record.
    /// </value>
    public required int RecordCount { get; init; }

    /// <summary>
    /// Gets the last write time of the source file, expressed in UTC ticks
    /// </summary>
    /// <value>The tick count of the file's UTC last write time at the moment it was packed.</value>
    public required long LastWriteUtcTicks { get; init; }

    /// <summary>
    /// Gets the XxHash3 checksum of the whole file computed at pack time
    /// </summary>
    /// <value>The 64-bit hash of the entire file when it was computed, or <c>0</c> when it was not.</value>
    /// <remarks>
    /// Streaming packs skip this by default because computing a whole-file hash would require an extra
    /// full read pass over every file. When the value is <c>0</c> it simply means "not computed"; it
    /// does not indicate a defect. Artifact integrity is still protected by Cartograph's own
    /// per-record checksums, which are written and verified regardless. Pass <c>--checksums</c> to the
    /// packer to spend the extra read pass and populate this field.
    /// </remarks>
    public required ulong Checksum { get; init; }

    /// <summary>
    /// Gets the zero-based index of the segment that holds this file
    /// </summary>
    /// <value>An index into <see cref="Cartograph.Format.Artifact.Segments" />.</value>
    public required int SegmentIndex { get; init; }

    /// <summary>
    /// Gets the zero-based index of the file's first record within its segment
    /// </summary>
    /// <value>An index accepted by <see cref="Cartograph.Format.ArtifactSegment.ReadRecord(int)" />.</value>
    public required int RecordIndex { get; init; }

    /// <summary>
    /// Gets the zero-based index of the file's first record across the whole artifact
    /// </summary>
    /// <value>
    /// The first of <see cref="RecordCount" /> consecutive indices, each accepted by
    /// <see cref="Cartograph.Format.Artifact.ReadRecord(long)" />.
    /// </value>
    public required long GlobalIndex { get; init; }

    /// <summary>
    /// Gets the last write time of the source file as a UTC <see cref="System.DateTime" />
    /// </summary>
    /// <value>The value of <see cref="LastWriteUtcTicks" /> interpreted as a UTC instant.</value>
    public DateTime LastWriteUtc => new(LastWriteUtcTicks, DateTimeKind.Utc);
}

/// <summary>
/// Represents the harness catalog: the manifest of files stored inside an artifact
/// </summary>
/// <remarks>
/// The catalog is serialized to a compact little-endian binary blob and written as record zero of
/// segment zero, so a reader can recover the entire directory structure with a single
/// <see cref="Cartograph.Format.Artifact.ReadRecord(long)" /> call and without scanning the payload.
/// </remarks>
internal sealed class FileCatalog
{
    /// <summary>
    /// Magic bytes that prefix a serialized catalog
    /// </summary>
    private static readonly byte[] Magic = "CHCG"u8.ToArray();

    /// <summary>
    /// Version of the catalog layout produced and understood by this build
    /// </summary>
    private const int CatalogVersion = 2;

    /// <summary>
    /// Gets the absolute path of the folder that was packed
    /// </summary>
    /// <value>The root directory supplied on the command line, fully qualified.</value>
    public required string SourceRoot { get; init; }

    /// <summary>
    /// Gets the UTC instant at which the artifact was created
    /// </summary>
    /// <value>The creation timestamp recorded by the packer.</value>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>
    /// Gets the name of the strategy used to distribute files across segments
    /// </summary>
    /// <value>One of <c>extension</c>, <c>directory</c> or <c>flat</c>.</value>
    public required string GroupingMode { get; init; }

    /// <summary>
    /// Gets the display name of each content segment, in segment order
    /// </summary>
    /// <value>
    /// One name per content segment. Index <c>0</c> corresponds to segment index <c>1</c> of the
    /// artifact, because segment <c>0</c> always holds the catalog itself.
    /// </value>
    public required IReadOnlyList<string> GroupNames { get; init; }

    /// <summary>
    /// Gets the catalog entries, ordered by relative path
    /// </summary>
    /// <value>One entry per packed file.</value>
    public required IReadOnlyList<CatalogEntry> Entries { get; init; }

    /// <summary>
    /// Gets the total number of payload bytes described by the catalog
    /// </summary>
    /// <value>The sum of <see cref="CatalogEntry.Length" /> across every entry.</value>
    public long TotalBytes
    {
        get
        {
            long total = 0;

            foreach (CatalogEntry entry in Entries)
            {
                total += entry.Length;
            }

            return total;
        }
    }

    /// <summary>
    /// Serializes the catalog to its binary representation
    /// </summary>
    /// <returns>A byte array suitable for storage as an artifact record</returns>
    public byte[] Serialize()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(CatalogVersion);
        writer.Write(SourceRoot);
        writer.Write(CreatedUtc.Ticks);
        writer.Write(GroupingMode);

        writer.Write(GroupNames.Count);

        foreach (string name in GroupNames)
        {
            writer.Write(name);
        }

        writer.Write(Entries.Count);

        foreach (CatalogEntry entry in Entries)
        {
            writer.Write(entry.RelativePath);
            writer.Write(entry.Length);
            writer.Write(entry.LastWriteUtcTicks);
            writer.Write(entry.Checksum);
            writer.Write(entry.SegmentIndex);
            writer.Write(entry.RecordIndex);
            writer.Write(entry.GlobalIndex);
            writer.Write(entry.RecordCount);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Deserializes a catalog from the bytes of an artifact record
    /// </summary>
    /// <param name="sequence">Record contents, possibly split across several mapped pages</param>
    /// <returns>The reconstructed catalog</returns>
    /// <exception cref="System.IO.InvalidDataException">The record does not begin with the catalog magic bytes.</exception>
    /// <exception cref="System.IO.InvalidDataException">The catalog version is not supported by this build.</exception>
    /// <exception cref="System.IO.InvalidDataException">The catalog declares a negative group or entry count.</exception>
    public static FileCatalog Deserialize(ReadOnlySequence<byte> sequence)
    {
        byte[] buffer = sequence.ToArray();

        using MemoryStream stream = new(buffer, writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        byte[] magic = reader.ReadBytes(Magic.Length);

        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                "Record 0 is not a harness catalog; the artifact was not produced by this tool.");
        }

        int version = reader.ReadInt32();

        if (version != CatalogVersion)
        {
            throw new InvalidDataException(
                $"Unsupported catalog version {version}; this build reads version {CatalogVersion}.");
        }

        string sourceRoot = reader.ReadString();
        long createdTicks = reader.ReadInt64();
        string groupingMode = reader.ReadString();

        int groupCount = reader.ReadInt32();

        if (groupCount < 0)
        {
            throw new InvalidDataException("Catalog declares a negative group count.");
        }

        string[] groupNames = new string[groupCount];

        for (int i = 0; i < groupCount; i++)
        {
            groupNames[i] = reader.ReadString();
        }

        int entryCount = reader.ReadInt32();

        if (entryCount < 0)
        {
            throw new InvalidDataException("Catalog declares a negative entry count.");
        }

        CatalogEntry[] entries = new CatalogEntry[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            entries[i] = new CatalogEntry
            {
                RelativePath = reader.ReadString(),
                Length = reader.ReadInt64(),
                LastWriteUtcTicks = reader.ReadInt64(),
                Checksum = reader.ReadUInt64(),
                SegmentIndex = reader.ReadInt32(),
                RecordIndex = reader.ReadInt32(),
                GlobalIndex = reader.ReadInt64(),
                RecordCount = reader.ReadInt32(),
            };
        }

        return new FileCatalog
        {
            SourceRoot = sourceRoot,
            CreatedUtc = new DateTime(createdTicks, DateTimeKind.Utc),
            GroupingMode = groupingMode,
            GroupNames = groupNames,
            Entries = entries,
        };
    }
}
