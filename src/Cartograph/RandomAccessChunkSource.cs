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
    public RandomAccessChunkSource(SafeFileHandle handle, bool ownsHandle = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
        _ownsHandle = ownsHandle;
        Length = RandomAccess.GetLength(handle);
    }

    /// <summary>Opens <paramref name="path"/> read-only as a pooled random-access chunk source.</summary>
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

    private void ValidateRange(long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Requested range extends past the end of the file.");
        }
    }

    private sealed class PooledBuffer(byte[] buffer) : IDisposable
    {
        private byte[]? _buffer = buffer;

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
