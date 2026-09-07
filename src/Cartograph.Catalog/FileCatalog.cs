// ============================================================================
// Cartograph
// File: FileCatalog.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Self-describing index stored as the first record of the first segment of an
// artifact, listing every packed file together with the segment, record and
// global index that hold its contents
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

namespace Cartograph.Catalog;

/// <summary>
/// Represents the manifest of files stored inside a Cartograph artifact
/// </summary>
/// <remarks>
/// The catalog is serialized to a compact little-endian binary blob and written as record zero of
/// segment zero, so a reader can recover the entire directory structure with a single
/// <see cref="Cartograph.Format.Artifact.ReadRecord(long)" /> call and without scanning the payload.
/// </remarks>
public sealed class FileCatalog {
    /// <summary>
    /// Magic bytes that prefix a serialized catalog
    /// </summary>
    private static readonly byte[] Magic = "CHCG"u8.ToArray();

    /// <summary>
    /// Version of the catalog layout produced and understood by this build
    /// </summary>
    public const int CatalogVersion = 2;

    /// <summary>
    /// Gets the absolute path of the folder that was packed
    /// </summary>
    /// <value>The root directory supplied to the packer, fully qualified.</value>
    public required string SourceRoot {
        get; init;
    }

    /// <summary>
    /// Gets the UTC instant at which the artifact was created
    /// </summary>
    /// <value>The creation timestamp recorded by the packer.</value>
    public required DateTime CreatedUtc {
        get; init;
    }

    /// <summary>
    /// Gets the name of the strategy used to distribute files across segments
    /// </summary>
    /// <value>One of <c>extension</c>, <c>directory</c> or <c>flat</c>.</value>
    public required string GroupingMode {
        get; init;
    }

    /// <summary>
    /// Gets the display name of each content segment, in segment order
    /// </summary>
    /// <value>
    /// One name per content segment. Index <c>0</c> corresponds to segment index <c>1</c> of the
    /// artifact, because segment <c>0</c> always holds the catalog itself.
    /// </value>
    public required IReadOnlyList<string> GroupNames {
        get; init;
    }

    /// <summary>
    /// Gets the catalog entries, ordered by relative path
    /// </summary>
    /// <value>One entry per packed file.</value>
    public required IReadOnlyList<CatalogEntry> Entries {
        get; init;
    }

    /// <summary>
    /// Gets the total number of payload bytes described by the catalog
    /// </summary>
    /// <value>The sum of <see cref="CatalogEntry.Length" /> across every entry.</value>
    public long TotalBytes {
        get {
            long total = 0;

            foreach (CatalogEntry entry in Entries) {
                total += entry.Length;
            }

            return total;
        }
    }

    /// <summary>
    /// Returns the display name of the segment that holds a given entry
    /// </summary>
    /// <param name="entry">Entry whose owning segment should be named</param>
    /// <returns>
    /// The group name recorded for the entry's segment, or a synthesized <c>segment N</c> label when
    /// the catalog does not name it
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="entry" /> is <see langword="null" />.</exception>
    public string GetGroupName(CatalogEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        // Segment 0 always holds the catalog itself, so content segment N maps to group name N - 1.
        int group = entry.SegmentIndex - 1;

        return group >= 0 && group < GroupNames.Count
            ? GroupNames[group]
            : $"segment {entry.SegmentIndex}";
    }

    /// <summary>
    /// Serializes the catalog to its binary representation
    /// </summary>
    /// <returns>A byte array suitable for storage as an artifact record</returns>
    public byte[] Serialize() {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(CatalogVersion);
        writer.Write(SourceRoot);
        writer.Write(CreatedUtc.Ticks);
        writer.Write(GroupingMode);

        writer.Write(GroupNames.Count);

        foreach (string name in GroupNames) {
            writer.Write(name);
        }

        writer.Write(Entries.Count);

        foreach (CatalogEntry entry in Entries) {
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
    public static FileCatalog Deserialize(ReadOnlySequence<byte> sequence) {
        byte[] buffer = sequence.ToArray();

        using MemoryStream stream = new(buffer, writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        byte[] magic = reader.ReadBytes(Magic.Length);

        if (!magic.AsSpan().SequenceEqual(Magic)) {
            throw new InvalidDataException(
                "Record 0 is not a Cartograph file catalog; the artifact was not produced by a catalog-aware packer.");
        }

        int version = reader.ReadInt32();

        if (version != CatalogVersion) {
            throw new InvalidDataException(
                $"Unsupported catalog version {version}; this build reads version {CatalogVersion}.");
        }

        string sourceRoot = reader.ReadString();
        long createdTicks = reader.ReadInt64();
        string groupingMode = reader.ReadString();

        int groupCount = reader.ReadInt32();

        if (groupCount < 0) {
            throw new InvalidDataException("Catalog declares a negative group count.");
        }

        string[] groupNames = new string[groupCount];

        for (int i = 0; i < groupCount; i++) {
            groupNames[i] = reader.ReadString();
        }

        int entryCount = reader.ReadInt32();

        if (entryCount < 0) {
            throw new InvalidDataException("Catalog declares a negative entry count.");
        }

        CatalogEntry[] entries = new CatalogEntry[entryCount];

        for (int i = 0; i < entryCount; i++) {
            entries[i] = new CatalogEntry {
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

        return new FileCatalog {
            SourceRoot = sourceRoot,
            CreatedUtc = new DateTime(createdTicks, DateTimeKind.Utc),
            GroupingMode = groupingMode,
            GroupNames = groupNames,
            Entries = entries,
        };
    }
}
