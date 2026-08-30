// ============================================================================
// Cartograph
// File: RandomAccessChunkSource.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// IChunkSource backed by RandomAccess reads into pooled ArrayPool<byte> buffers;
// async-friendly and often superior for large sequential file scans.
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
using Microsoft.Win32.SafeHandles;

namespace Cartograph;

/// <summary>
/// An <see cref="IChunkSource"/> backed by <see cref="RandomAccess"/> reads into pooled buffers.
/// </summary>
/// <remarks>
/// Each read rents a buffer from <see cref="ArrayPool{T}"/>, copies the requested range into it, and
/// returns it as a single-segment <see cref="System.Buffers.ReadOnlySequence{Byte}"/>. Unlike the mapped source this
/// copies into managed memory, but the copy is bounded by the record size and the buffer is pooled,
/// so there is no steady-state allocation proportional to total file size. It is async-friendly and
/// often wins on large sequential scans.
/// </remarks>
public sealed class RandomAccessChunkSource : IChunkSource
{
    private readonly SafeFileHandle _handle;
    private readonly bool _ownsHandle;

    /// <summary>Wraps an existing file handle.</summary>
    /// <param name="handle">The file handle to read from.</param>
    /// <param name="ownsHandle">When <see langword="true"/>, disposing this source disposes <paramref name="handle"/>.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="handle" /> is <c>null</c>.</exception>
    public RandomAccessChunkSource(SafeFileHandle handle, bool ownsHandle = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
        _ownsHandle = ownsHandle;
        Length = RandomAccess.GetLength(handle);
    }

    /// <summary>Opens <paramref name="path"/> read-only as a pooled random-access chunk source.</summary>
    /// <param name="path">The path to the file to open.</param>
    /// <returns>A new <see cref="RandomAccessChunkSource"/> that owns the underlying file handle.</returns>
    public static RandomAccessChunkSource Open(string path)
    {
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);
        return new RandomAccessChunkSource(handle, ownsHandle: true);
    }

    /// <inheritdoc />
    public long Length { get; }

    /// <inheritdoc />
    /// <param name="offset">The byte offset within the file to start reading from.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <returns>A <see cref="ChunkLease"/> backed by a pooled buffer containing the requested bytes.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="offset" /> or <paramref name="length" /> is negative, or the requested range extends past the end of the file.</exception>
    /// <exception cref="System.IO.EndOfStreamException">Unexpected end of file while reading a chunk.</exception>
    public ChunkLease Read(long offset, int length)
    {
        ValidateRange(offset, length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int total = 0;
            while (total < length)
            {
                int read = RandomAccess.Read(_handle, buffer.AsSpan(total, length - total), offset + total);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of file while reading a chunk.");
                }

                total += read;
            }

            return new ChunkLease(new ReadOnlySequence<byte>(buffer, 0, length), new PooledBuffer(buffer));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <inheritdoc />
    /// <param name="offset">The byte offset within the file to start reading from.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <param name="cancellationToken">A token that may cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to a <see cref="ChunkLease"/> backed by a pooled buffer.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="offset" /> or <paramref name="length" /> is negative, or the requested range extends past the end of the file.</exception>
    /// <exception cref="System.IO.EndOfStreamException">Unexpected end of file while reading a chunk.</exception>
    /// <exception cref="System.OperationCanceledException">The operation was canceled via <paramref name="cancellationToken" />.</exception>
    public async ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken cancellationToken = default)
    {
        ValidateRange(offset, length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int total = 0;
            while (total < length)
            {
                int read = await RandomAccess
                    .ReadAsync(_handle, buffer.AsMemory(total, length - total), offset + total, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of file while reading a chunk.");
                }

                total += read;
            }

            return new ChunkLease(new ReadOnlySequence<byte>(buffer, 0, length), new PooledBuffer(buffer));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHandle)
        {
            _handle.Dispose();
        }
    }

    /// <summary>Validates that <paramref name="offset"/> and <paramref name="length"/> form a legal range within the file.</summary>
    /// <param name="offset">The byte offset to validate.</param>
    /// <param name="length">The number of bytes to validate.</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="offset" /> or <paramref name="length" /> is negative, or the requested range extends past the end of the file.</exception>
    private void ValidateRange(long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Requested range extends past the end of the file.");
        }
    }

    /// <summary>
    /// Wraps a rented <see cref="ArrayPool{T}"/> buffer and returns it to the pool on disposal.
    /// </summary>
    private sealed class PooledBuffer(byte[] buffer) : IDisposable
    {
        private byte[]? _buffer = buffer;

        /// <summary>Returns the rented buffer to <see cref="ArrayPool{Byte}.Shared"/>. Safe to call more than once.</summary>
        public void Dispose()
        {
            byte[]? rented = Interlocked.Exchange(ref _buffer, null);
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
