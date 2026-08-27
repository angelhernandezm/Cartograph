namespace Cartograph;

/// <summary>
/// An <see cref="IChunkSource"/> backed by a memory-mapped file. Reads are zero-copy slices of the
/// mapped windows; page faults bring bytes in on demand and are shared across processes via the OS
/// page cache.
/// </summary>
public sealed class MappedChunkSource : IChunkSource
{
    private readonly MappedFile _file;
    private readonly bool _ownsFile;

    /// <summary>Wraps an existing <see cref="MappedFile"/>.</summary>
    /// <param name="file">The mapped file to read from.</param>
    /// <param name="ownsFile">When <see langword="true"/>, disposing this source disposes <paramref name="file"/>.</param>
    public MappedChunkSource(MappedFile file, bool ownsFile = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _ownsFile = ownsFile;
    }

    /// <summary>Opens <paramref name="path"/> read-only as a mapped chunk source.</summary>
    public static MappedChunkSource Open(string path, long windowSize = MappedFile.DefaultWindowSize)
        => new(MappedFile.OpenRead(path, windowSize), ownsFile: true);

    /// <inheritdoc />
    public long Length => _file.Length;

    /// <inheritdoc />
    public ChunkLease Read(long offset, int length)
    {
        MappedSlice slice = _file.Slice(offset, length);
        return new ChunkLease(slice.Sequence, slice);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Mapped reads are satisfied by synchronous, blocking page faults, so there is no genuine async
    /// path; the result is returned as an already-completed <see cref="ValueTask{TResult}"/>.
    /// </remarks>
    public ValueTask<ChunkLease> ReadAsync(long offset, int length, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<ChunkLease>(Read(offset, length));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsFile)
        {
            _file.Dispose();
        }
    }
}
