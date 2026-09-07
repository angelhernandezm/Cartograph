// ============================================================================
// Cartograph
// File: ArtifactOpenOptions.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Defines the ChunkSourceKind enum and ArtifactOpenOptions class that control
// which I/O strategy and checksum behavior are used when opening an artifact.
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

/// <summary>Selects the <see cref="IChunkSource"/> strategy used to read record payloads.</summary>
public enum ChunkSourceKind {
    /// <summary>Memory-map the file (default). Zero-copy; wins on random reads over a hot page cache.</summary>
    Mapped = 0,

    /// <summary>Read into pooled buffers via <see cref="System.IO.RandomAccess"/>. Async-friendly; often wins on large sequential scans.</summary>
    RandomAccess = 1,

    /// <summary>
    /// A caller-supplied <see cref="IChunkSource"/> implementation passed to
    /// <see cref="Artifact.Open(IChunkSource, ArtifactOpenOptions, bool)"/>. Reported for any source
    /// that is not one of the built-in strategies.
    /// </summary>
    Custom = 2,
}

/// <summary>Options controlling how an <see cref="Artifact"/> is opened.</summary>
public sealed class ArtifactOpenOptions {
    /// <summary>The default options: memory-mapped access with checksum verification enabled.</summary>
    /// <value>The default options: memory-mapped access with checksum verification enabled.</value>
    public static ArtifactOpenOptions Default { get; } = new();

    /// <summary>
    /// Which chunk source strategy to use for record reads. Ignored when the artifact is opened
    /// from a caller-supplied <see cref="IChunkSource"/>.
    /// </summary>
    /// <value>
    /// Which chunk source strategy to use for record reads. Ignored when the artifact is opened from a
    /// caller-supplied <see cref="IChunkSource"/>.
    /// </value>
    public ChunkSourceKind ChunkSource { get; init; } = ChunkSourceKind.Mapped;

    /// <summary>
    /// The mapped window size, used only when <see cref="ChunkSource"/> is
    /// <see cref="ChunkSourceKind.Mapped"/>. A small value forces the multi-view stitching path.
    /// Ignored when the artifact is opened from a caller-supplied <see cref="IChunkSource"/>.
    /// </summary>
    /// <value>
    /// The mapped window size, used only when <see cref="ChunkSource"/> is
    /// <see cref="ChunkSourceKind.Mapped"/>. A small value forces the multi-view stitching path. Ignored
    /// when the artifact is opened from a caller-supplied <see cref="IChunkSource"/>.
    /// </value>
    public long WindowSize { get; init; } = MappedFile.DefaultWindowSize;

    /// <summary>Whether to verify each record's checksum on read (recommended). Defaults to <see langword="true"/>.</summary>
    /// <value>
    /// Whether to verify each record's checksum on read (recommended). Defaults to <see langword="true"/>.
    /// </value>
    public bool VerifyChecksums { get; init; } = true;
}
