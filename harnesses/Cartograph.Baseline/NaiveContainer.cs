// ============================================================================
// Cartograph
// File: NaiveContainer.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A flat container written with FileStream and BinaryWriter, offering both the
// read-the-whole-file-into-memory strategy and the seek-per-record strategy a
// developer would reach for without a mapped-memory library
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
using System.Text;

namespace Cartograph.Baseline;

/// <summary>
/// Stores files back to back in a single file with an index appended at the end
/// </summary>
/// <remarks>
/// <para>
/// This is the honest hand-rolled equivalent of a Cartograph artifact: a magic number, an offset to
/// the index, the payloads written contiguously, and the index itself at the tail. There is no
/// alignment and no per-record checksum in the container, because a developer writing this by hand
/// would be unlikely to add either.
/// </para>
/// <para>
/// The two read strategies are the interesting part. <see cref="ContainerKind.Naive" /> loads the
/// whole file with <see cref="System.IO.File.ReadAllBytes(string)" />, which is simple, fast to
/// write and allocates the entire artifact on the managed heap. <see cref="ContainerKind.NaiveStream" />
/// reads only the index and then seeks per record into a reused buffer, which is the best that
/// plain .NET can do and is the fairest comparison against a mapped read.
/// </para>
/// </remarks>
/// <param name="streaming">
/// <see langword="true" /> to read only the index and seek per record; <see langword="false" /> to
/// load the whole container into memory
/// </param>
internal sealed class NaiveContainer(bool streaming) : BaselineContainer
{
    /// <summary>
    /// Magic bytes that prefix the container
    /// </summary>
    internal static readonly byte[] Magic = "NBC1"u8.ToArray();

    /// <summary>
    /// Byte length of the fixed container preamble: the magic number plus the index offset
    /// </summary>
    internal const int PreambleSize = 12;

    /// <summary>
    /// Size of the buffer used when copying file contents into the container
    /// </summary>
    private const int CopyBufferSize = 128 * 1024;

    /// <summary>
    /// Whether records are fetched on demand rather than served from a fully loaded container
    /// </summary>
    private readonly bool _streaming = streaming;

    /// <inheritdoc />
    public override string Name => _streaming ? "naive-stream" : "naive";

    /// <inheritdoc />
    public override string Extension => ".nbc";

    /// <inheritdoc />
    public override BaselinePackResult Pack(string path, IReadOnlyList<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        RunMetrics.Scope scope = RunMetrics.Measure("pack");

        List<BaselineEntry> entries = new(files.Count);
        long payloadBytes = 0;

        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> preamble = stackalloc byte[PreambleSize];
            Magic.CopyTo(preamble);
            BinaryPrimitives.WriteInt64LittleEndian(preamble[4..], 0L);
            stream.Write(preamble);

            byte[] buffer = new byte[CopyBufferSize];

            foreach (SourceFile file in files)
            {
                long offset = stream.Position;
                XxHash3 hasher = new();
                long written = 0;

                try
                {
                    using FileStream input = new(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);

                    int read;

                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        stream.Write(buffer, 0, read);
                        hasher.Append(buffer.AsSpan(0, read));
                        written += read;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ConsoleReport.Warn($"skipping '{file.RelativePath}': {ex.Message}");

                    // Rewind so the partially copied bytes do not become part of the container.
                    stream.SetLength(offset);
                    stream.Position = offset;
                    continue;
                }

                entries.Add(new BaselineEntry
                {
                    RelativePath = file.RelativePath,
                    Length = written,
                    LastWriteUtcTicks = file.LastWriteUtc.Ticks,
                    Checksum = hasher.GetCurrentHashAsUInt64(),
                    Offset = offset,
                });

                payloadBytes += written;
            }

            long indexOffset = stream.Position;
            WriteIndex(stream, entries);

            stream.Flush();
            stream.Position = 4;

            Span<byte> offsetBytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, indexOffset);
            stream.Write(offsetBytes);
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

        ContainerReader reader = _streaming
            ? OpenStreaming(path)
            : OpenWhole(path);

        metrics = scope.Stop();
        return reader;
    }

    /// <summary>
    /// Opens the container by loading every byte of it into a single managed array
    /// </summary>
    /// <param name="path">Fully qualified path of the container to open</param>
    /// <returns>A reader that serves records as slices of the loaded array</returns>
    /// <exception cref="System.IO.InvalidDataException">The file does not begin with the container magic bytes.</exception>
    private static ContainerReader OpenWhole(string path)
    {
        byte[] all = File.ReadAllBytes(path);

        if (all.Length < PreambleSize || !all.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a baseline container: bad magic.");
        }

        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(all.AsSpan(4));

        if (indexOffset < PreambleSize || indexOffset > all.Length)
        {
            throw new InvalidDataException("Baseline container has a corrupt index offset.");
        }

        using MemoryStream stream = new(all, (int)indexOffset, all.Length - (int)indexOffset, writable: false);
        List<BaselineEntry> entries = ReadIndex(stream);

        return new WholeFileReader(all, entries);
    }

    /// <summary>
    /// Opens the container by reading only its index
    /// </summary>
    /// <param name="path">Fully qualified path of the container to open</param>
    /// <returns>A reader that fetches records from the file on demand</returns>
    /// <exception cref="System.IO.InvalidDataException">The file does not begin with the container magic bytes.</exception>
    private static ContainerReader OpenStreaming(string path)
    {
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        try
        {
            Span<byte> preamble = stackalloc byte[PreambleSize];
            stream.ReadExactly(preamble);

            if (!preamble[..4].SequenceEqual(Magic))
            {
                throw new InvalidDataException("Not a baseline container: bad magic.");
            }

            long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(preamble[4..]);

            if (indexOffset < PreambleSize || indexOffset > stream.Length)
            {
                throw new InvalidDataException("Baseline container has a corrupt index offset.");
            }

            stream.Position = indexOffset;
            List<BaselineEntry> entries = ReadIndex(stream);

            return new StreamingReader(stream, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends the index to the end of the container
    /// </summary>
    /// <param name="stream">Container stream, positioned at the index offset</param>
    /// <param name="entries">Entries to write</param>
    private static void WriteIndex(Stream stream, List<BaselineEntry> entries)
    {
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(entries.Count);

        foreach (BaselineEntry entry in entries)
        {
            writer.Write(entry.RelativePath);
            writer.Write(entry.Offset);
            writer.Write(entry.Length);
            writer.Write(entry.LastWriteUtcTicks);
            writer.Write(entry.Checksum);
        }

        writer.Flush();
    }

    /// <summary>
    /// Reads the index from the current stream position
    /// </summary>
    /// <param name="stream">Stream positioned at the start of the index</param>
    /// <returns>The entries described by the index</returns>
    /// <exception cref="System.IO.InvalidDataException">The index declares a negative entry count.</exception>
    private static List<BaselineEntry> ReadIndex(Stream stream)
    {
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        int count = reader.ReadInt32();

        if (count < 0)
        {
            throw new InvalidDataException("Baseline container declares a negative entry count.");
        }

        List<BaselineEntry> entries = new(count);

        for (int i = 0; i < count; i++)
        {
            entries.Add(new BaselineEntry
            {
                RelativePath = reader.ReadString(),
                Offset = reader.ReadInt64(),
                Length = reader.ReadInt64(),
                LastWriteUtcTicks = reader.ReadInt64(),
                Checksum = reader.ReadUInt64(),
            });
        }

        return entries;
    }

    /// <summary>
    /// Serves records as slices of a container that was loaded in its entirety
    /// </summary>
    /// <param name="buffer">Complete container contents</param>
    /// <param name="entries">Entries describing the stored files</param>
    private sealed class WholeFileReader(byte[] buffer, List<BaselineEntry> entries) : ContainerReader
    {
        /// <summary>
        /// Complete container contents, held on the managed heap for the lifetime of the reader
        /// </summary>
        private readonly byte[] _buffer = buffer;

        /// <inheritdoc />
        public override IReadOnlyList<BaselineEntry> Entries { get; } = entries;

        /// <inheritdoc />
        public override ReadOnlyMemory<byte> Read(int index, ref byte[] scratch)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Entries.Count);

            BaselineEntry entry = Entries[index];

            // No copy: the payload is already resident, which is the one thing this strategy has
            // going for it. The cost was paid up front, in full, at open time.
            return _buffer.AsMemory((int)entry.Offset, (int)entry.Length);
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            // Nothing to release: the buffer is ordinary managed memory.
        }
    }

    /// <summary>
    /// Fetches records from the container file on demand
    /// </summary>
    /// <param name="stream">Open container stream</param>
    /// <param name="entries">Entries describing the stored files</param>
    private sealed class StreamingReader(FileStream stream, List<BaselineEntry> entries) : ContainerReader
    {
        /// <summary>
        /// Open handle to the container
        /// </summary>
        private readonly FileStream _stream = stream;

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
            RandomAccess.Read(_stream.SafeFileHandle, scratch.AsSpan(0, length), entry.Offset);

            return scratch.AsMemory(0, length);
        }

        /// <inheritdoc />
        public override void Dispose() => _stream.Dispose();
    }
}
