// ============================================================================
// Cartograph
// File: MappedChunkSource.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// IChunkSource implementation backed by a memory-mapped file; reads are zero-copy
// slices of mapped windows, satisfying page faults from the OS page cache.
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

namespace Cartograph;

/// <summary>
/// An <see cref="IChunkSource"/> backed by a memory-mapped file. Reads are zero-copy slices of the
/// mapped windows; page faults bring bytes in on demand and are shared across processes via the OS
/// page cache.
/// </summary>
public sealed class MappedChunkSource : IChunkSource {
    /// <summary>The mapped file whose windows back every returned chunk.</summary>
    private readonly MappedFile _file;

    /// <summary>Whether disposing this source should also dispose <see cref="_file"/>.</summary>
    private readonly bool _ownsFile;

    /// <summary>
    /// Initializes a new instance of the <see cref="MappedChunkSource" /> class.
    /// </summary>
    /// <param name="file">The mapped file to read from.</param>
    /// <param name="ownsFile">When <see langword="true"/>, disposing this source disposes <paramref name="file"/>.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="file" /> is <c>null</c>.</exception>
    public MappedChunkSource(MappedFile file, bool ownsFile = false) {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _ownsFile = ownsFile;
    }

    /// <summary>Opens <paramref name="path"/> read-only as a mapped chunk source.</summary>
    /// <param name="path">The path to the file to open.</param>
    /// <param name="windowSize">The size of each mapped window in bytes.</param>
    /// <returns>A new <see cref="MappedChunkSource"/> that owns the underlying <see cref="MappedFile"/>.</returns>
    public static MappedChunkSource Open(string path, long windowSize = MappedFile.DefaultWindowSize)
        => new(MappedFile.OpenRead(path, windowSize), ownsFile: true);

    /// <inheritdoc />
    public long Length => _file.Length;

    /// <inheritdoc />
    /// <param name="offset">The byte offset within the file to start reading from.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <returns>A <see cref="ChunkLease"/> containing the requested bytes as a zero-copy sequence.</returns>
    public ChunkLease Read(long offset, int length) {
        MappedSlice slice = _file.Slice(offset, length);
        return new ChunkLease(slice.Sequence, slice);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Mapped reads are satisfied by synchronous, blocking page faults, so there is no genuine async
    /// path; the result is returned as an already-completed <see cref="ValueTask{TResult}"/>.
    /// </remarks>
    /// <param name="offset">The byte offset within the file to start reading from.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>An already-completed <see cref="ValueTask{TResult}"/> wrapping a <see cref="ChunkLease"/>.</returns>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken" />.</exception>
    public ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<ChunkLease>(Read(offset, length));
    }

    /// <inheritdoc />
    public void Dispose() {
        if (_ownsFile) {
            _file.Dispose();
        }
    }
}
