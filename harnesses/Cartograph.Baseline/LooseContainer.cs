// ============================================================================
// Cartograph
// File: LooseContainer.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// A degenerate container that stores nothing at all: the source folder is the
// container, packing is a no-op and every read is an idiomatic File.ReadAllBytes
// that allocates a fresh array per file. It is the honest lower bound on pack
// effort and the honest upper bound on read allocation
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

namespace Cartograph.Baseline;

/// <summary>
/// Treats the source folder itself as the container, packing nothing and reading each file in place
/// </summary>
/// <param name="root">Fully qualified path of the source folder that acts as the container</param>
/// <remarks><para>
/// This mode exists to mark the two extremes of the comparison. Packing writes no container at all,
/// so its cost is essentially zero and it will win any pack-time race against a mode that actually
/// assembles a file. Reading, by contrast, uses the most idiomatic call a developer could reach for,
/// <see cref="System.IO.File.ReadAllBytes(string)" />, which allocates a fresh managed array for
/// every file. That per-file allocation is deliberately the point: it is what a mapped, zero-copy
/// read avoids, and it is the honest upper bound on read allocation.
/// </para>
/// <para>
/// Because nothing is written, there is no place to record a checksum, so entries carry a checksum
/// of zero and verification falls back to comparing the observed length against the length seen when
/// the tree was scanned.
/// </para></remarks>
internal sealed class LooseContainer(string root) : BaselineContainer {
    /// <summary>
    /// Fully qualified path of the source folder that acts as the container
    /// </summary>
    private readonly string _root = root;

    /// <inheritdoc />
    public override string Name => "loose";

    /// <inheritdoc />
    public override string Extension => string.Empty;

    /// <inheritdoc />
    /// <exception cref="System.ArgumentNullException"><paramref name="files" /> is <see langword="null" />.</exception>
    public override BaselinePackResult Pack(string path, IReadOnlyList<SourceFile> files) {
        ArgumentNullException.ThrowIfNull(files);

        RunMetrics.Scope scope = RunMetrics.Measure("pack");

        long payloadBytes = 0;

        // There is nothing to write. The only work is to total the payload that was already measured
        // during the scan, so this loop touches no file contents and moves no bytes to disk.
        foreach (SourceFile file in files) {
            payloadBytes += file.Length;
        }

        RunMetrics metrics = scope.Stop(payloadBytes);

        return new BaselinePackResult {
            ContainerPath = _root,
            FileCount = files.Count,
            PayloadBytes = payloadBytes,
            ContainerBytes = 0,
            Metrics = metrics,
        };
    }

    /// <inheritdoc />
    /// <exception cref="System.IO.DirectoryNotFoundException">The source folder does not exist.</exception>
    public override ContainerReader Open(string path, out RunMetrics metrics) {
        RunMetrics.Scope scope = RunMetrics.Measure("open");

        if (!Directory.Exists(_root)) {
            throw new DirectoryNotFoundException($"Source folder '{_root}' does not exist.");
        }

        EnumerationOptions enumeration = new() {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        List<BaselineEntry> entries = [];

        // Opening is deliberately cheap: enumerate the tree and record each file's length. No file
        // contents are read here, which is the whole reason this mode looks fast to open.
        foreach (string file in Directory.EnumerateFiles(_root, "*", enumeration)) {
            string relative = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');

            long length;

            try {
                length = new FileInfo(file).Length;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                continue;
            }

            entries.Add(new BaselineEntry {
                RelativePath = relative,
                Length = length,
                LastWriteUtcTicks = 0,
                Checksum = 0,
                Offset = 0,
            });
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        ContainerReader reader = new LooseReader(_root, entries);
        metrics = scope.Stop();
        return reader;
    }

    /// <summary>
    /// Reads files directly from the source tree, allocating a fresh array per read
    /// </summary>
    /// <param name="root">Fully qualified path of the source folder</param>
    /// <param name="entries">Entries describing the discovered files, ordered by relative path</param>
    private sealed class LooseReader(string root, List<BaselineEntry> entries) : ContainerReader {
        /// <summary>
        /// Fully qualified path of the source folder the reader reads from
        /// </summary>
        private readonly string _root = root;

        /// <inheritdoc />
        public override IReadOnlyList<BaselineEntry> Entries { get; } = entries;

        /// <inheritdoc />
        public override ReadOnlyMemory<byte> Read(int index, ref byte[] scratch) {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Entries.Count);

            BaselineEntry entry = Entries[index];
            string full = Path.Combine(_root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            // File.ReadAllBytes allocates a brand new array sized to the file, ignoring the reusable
            // scratch buffer entirely. That is the idiomatic call and the deliberate cost of this mode.
            return File.ReadAllBytes(full);
        }

        /// <inheritdoc />
        public override void Dispose() {
            // Nothing to release: no handle is held open between reads.
        }
    }
}
