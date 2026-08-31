// ============================================================================
// Cartograph
// File: ZipContainer.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A container backed by System.IO.Compression.ZipArchive, offering both a
// stored (uncompressed) mode that is the closest apples-to-apples comparison
// to an uncompressed Cartograph artifact and a Deflate mode that trades CPU
// for a much smaller file
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

using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;

namespace Cartograph.Baseline;

/// <summary>
/// Packs files into a ZIP archive and reads them back through the framework decompressor
/// </summary>
/// <remarks>
/// <para>
/// This is the container a developer reaches for when a single portable file is wanted and the
/// standard library is the only dependency allowed. Two modes are offered because they answer two
/// different questions. <see cref="ContainerKind.ZipStore" /> writes every entry with compression
/// disabled, so the payload lands on disk verbatim; it is the closest apples-to-apples comparison to
/// an uncompressed Cartograph artifact, differing mainly in that the ZIP central directory is read
/// and each entry is copied out of the archive stream rather than mapped in place.
/// <see cref="ContainerKind.ZipDeflate" /> writes every entry with Deflate, producing a far smaller
/// container at the cost of CPU on both the write and the read; Cartograph does not compress at all,
/// so this mode is the honest way to show what compression buys and what it costs.
/// </para>
/// <para>
/// Because a ZIP central directory records only a CRC-32, the XxHash3 computed at pack time is
/// stored in each entry's comment so that the reader can hand it back and the verification stays
/// identical to the other container kinds.
/// </para>
/// </remarks>
/// <param name="store">
/// <see langword="true" /> to write entries uncompressed; <see langword="false" /> to compress them
/// with Deflate at <see cref="System.IO.Compression.CompressionLevel.Optimal" />
/// </param>
internal sealed class ZipContainer(bool store) : BaselineContainer
{
    /// <summary>
    /// Size of the buffer used when copying file contents into an archive entry
    /// </summary>
    private const int CopyBufferSize = 128 * 1024;

    /// <summary>
    /// Whether entries are written uncompressed
    /// </summary>
    private readonly bool _store = store;

    /// <inheritdoc />
    public override string Name => _store ? "zip-store" : "zip-deflate";

    /// <inheritdoc />
    public override string Extension => ".zip";

    /// <inheritdoc />
    /// <exception cref="System.ArgumentNullException"><paramref name="files" /> is <see langword="null" />.</exception>
    public override BaselinePackResult Pack(string path, IReadOnlyList<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        RunMetrics.Scope scope = RunMetrics.Measure("pack");

        CompressionLevel level = _store ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        List<BaselineEntry> entries = new(files.Count);
        long payloadBytes = 0;

        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        {
            byte[] buffer = new byte[CopyBufferSize];

            foreach (SourceFile file in files)
            {
                FileStream? input = null;

                try
                {
                    input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ConsoleReport.Warn($"skipping '{file.RelativePath}': {ex.Message}");
                    continue;
                }

                XxHash3 hasher = new();
                long written = 0;

                using (input)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(file.RelativePath, level);
                    entry.LastWriteTime = new DateTimeOffset(file.LastWriteUtc, TimeSpan.Zero);

                    using Stream target = entry.Open();
                    int read;

                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        target.Write(buffer, 0, read);
                        hasher.Append(buffer.AsSpan(0, read));
                        written += read;
                    }

                    // The central directory only carries a CRC-32, so the XxHash3 is smuggled through
                    // the entry comment. It is read straight back when the archive is reopened.
                    entry.Comment = hasher.GetCurrentHashAsUInt64().ToString("x16", CultureInfo.InvariantCulture);
                }

                entries.Add(new BaselineEntry
                {
                    RelativePath = file.RelativePath,
                    Length = written,
                    LastWriteUtcTicks = file.LastWriteUtc.Ticks,
                    Checksum = hasher.GetCurrentHashAsUInt64(),
                    Offset = 0,
                });

                payloadBytes += written;
            }
        }

        RunMetrics metrics = scope.Stop(payloadBytes);

        return new BaselinePackResult
        {
            ContainerPath = path,
            FileCount = entries.Count,
            PayloadBytes = payloadBytes,
            ContainerBytes = new FileInfo(path).Length,
            Metrics = metrics,
        };
    }

    /// <inheritdoc />
    public override ContainerReader Open(string path, out RunMetrics metrics)
    {
        RunMetrics.Scope scope = RunMetrics.Measure("open");

        ZipArchive archive = ZipFile.OpenRead(path);

        try
        {
            List<BaselineEntry> entries = new(archive.Entries.Count);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                entries.Add(new BaselineEntry
                {
                    RelativePath = entry.FullName,
                    Length = entry.Length,
                    LastWriteUtcTicks = entry.LastWriteTime.UtcDateTime.Ticks,
                    Checksum = ParseChecksum(entry.Comment),
                    Offset = 0,
                });
            }

            ContainerReader reader = new ZipReader(archive, entries);
            metrics = scope.Stop();
            return reader;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Parses the XxHash3 checksum stored in an entry comment
    /// </summary>
    /// <param name="comment">Entry comment, expected to hold a sixteen digit hexadecimal value</param>
    /// <returns>The parsed checksum, or zero when the comment is missing or malformed</returns>
    private static ulong ParseChecksum(string comment) =>
        ulong.TryParse(comment, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value) ? value : 0UL;

    /// <summary>
    /// Serves archive entries by decompressing them into a reused scratch buffer
    /// </summary>
    /// <param name="archive">Open archive positioned over the container</param>
    /// <param name="entries">Entries describing the stored files, ordered to match the archive</param>
    private sealed class ZipReader(ZipArchive archive, List<BaselineEntry> entries) : ContainerReader
    {
        /// <summary>
        /// Open archive whose entries are read on demand
        /// </summary>
        private readonly ZipArchive _archive = archive;

        /// <inheritdoc />
        public override IReadOnlyList<BaselineEntry> Entries { get; } = entries;

        /// <inheritdoc />
        public override ReadOnlyMemory<byte> Read(int index, ref byte[] scratch)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Entries.Count);

            BaselineEntry entry = Entries[index];
            EnsureCapacity(ref scratch, entry.Length);

            int length = (int)entry.Length;

            // Even a stored entry is served through a decompression stream, so the payload is copied
            // out of the archive into managed memory here. That copy is exactly the cost a mapped
            // artifact avoids.
            using Stream source = _archive.Entries[index].Open();
            source.ReadExactly(scratch, 0, length);

            return scratch.AsMemory(0, length);
        }

        /// <inheritdoc />
        public override void Dispose() => _archive.Dispose();
    }
}
