// ============================================================================
// Cartograph
// File: CatalogEntry.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Description of a single file stored inside a Cartograph artifact, mapping it
// back to the segment, record and global index that hold its contents along
// with its length, timestamp and checksum
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

namespace Cartograph.Catalog;

/// <summary>
/// Describes a single file that was packed into a Cartograph artifact
/// </summary>
/// <remarks>
/// Cartograph deliberately treats records as opaque bytes, so file names, sizes and timestamps have
/// no representation in the format itself. This type is the catalog's answer to that: the metadata
/// lives in a catalog record written alongside the payload, which is exactly the pattern an
/// application is expected to follow.
/// </remarks>
public sealed class CatalogEntry
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
    /// per-record checksums, which are written and verified regardless.
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

    /// <summary>
    /// Gets the file name portion of <see cref="RelativePath" />
    /// </summary>
    /// <value>The text after the final forward slash, or the whole path when there is none.</value>
    public string Name
    {
        get
        {
            int slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? RelativePath : RelativePath[(slash + 1)..];
        }
    }

    /// <summary>
    /// Gets the directory portion of <see cref="RelativePath" />
    /// </summary>
    /// <value>The text before the final forward slash, or an empty string for a file in the root.</value>
    public string Directory
    {
        get
        {
            int slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? string.Empty : RelativePath[..slash];
        }
    }

    /// <summary>
    /// Gets the extension of <see cref="RelativePath" />, including the leading period
    /// </summary>
    /// <value>An extension such as <c>.cs</c>, or an empty string when the file has none.</value>
    public string Extension
    {
        get
        {
            string name = Name;
            int dot = name.LastIndexOf('.');

            return dot <= 0 ? string.Empty : name[dot..];
        }
    }
}
